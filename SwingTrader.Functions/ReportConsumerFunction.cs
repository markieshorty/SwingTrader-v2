using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Report;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (report-jobs queue): builds the
// daily markdown brief and emails it to every account recipient subscribed to
// the DailyReport category.
public class ReportConsumerFunction(
    IJobLogRepository jobLog,
    IReportGenerationService reportService,
    IReportRepository reportRepo,
    INotificationRecipientRepository recipients,
    IApprovalRepository approvalRepo,
    IEmailService email,
    IUserHttpClientFactory clientFactory,
    ILogger<ReportConsumerFunction> logger)
{
    [Function("ReportConsumer")]
    public async Task Run(
        [ServiceBusTrigger("report-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ReportJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Report", message.ReportDate, ct);

        try
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(message.AccountId, ct);
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);

            var report = await reportService.GenerateAsync(message.AccountId, finnhub, t212, claude, message.ReportDate, ct);

            var allRecipients = await recipients.ListAsync(message.AccountId, ct);
            var toAddresses = allRecipients
                .Where(r => r.Categories.HasFlag(NotificationCategory.DailyReport))
                .Select(r => r.Email)
                .ToList();

            if (toAddresses.Count > 0)
            {
                await email.SendDailyReportAsync(toAddresses, report.ReportMarkdown);
                report.WasSent = true;
                await reportRepo.UpdateAsync(report);
            }
            else
            {
                logger.LogInformation("No DailyReport recipients for account {AccountId} — report generated but not emailed", message.AccountId);
            }

            // Sent separately, and only to recipients who've explicitly opted
            // into TradeApproval - not everyone who gets the general report.
            // Skip if the user has already approved today's trades (e.g. Report
            // ran twice in a day after manual retry).
            var existingApproval = await approvalRepo.GetByDateAsync(message.AccountId, message.ReportDate);
            if (report.ApprovalMarkdown is not null && existingApproval?.IsApproved != true)
            {
                var approvalAddresses = allRecipients
                    .Where(r => r.Categories.HasFlag(NotificationCategory.TradeApproval))
                    .Select(r => r.Email)
                    .ToList();

                if (approvalAddresses.Count > 0)
                {
                    await email.SendSimpleEmailAsync(
                        approvalAddresses, report.ApprovalMarkdown,
                        $"SwingTrader — Action Required: Approve Today's Trades ({message.ReportDate:dd MMM yyyy})");
                }
                else
                {
                    logger.LogWarning(
                        "Approval required for account {AccountId} but no recipient has TradeApproval enabled — trades will not be approved today",
                        message.AccountId);
                }
            }

            await jobLog.MarkCompletedAsync(message.AccountId, "Report", message.ReportDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Report", message.ReportDate, ex.Message, ct);
            throw;
        }
    }
}
