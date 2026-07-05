using System.Net;
using System.Net.Mail;
using Markdig;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Services;

public class EmailService(IOptions<EmailConfig> options, ILogger<EmailService> logger) : IEmailService
{
    private readonly EmailConfig _config = options.Value;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public async Task SendAsync(List<string> recipients, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_config.SmtpHost))
        {
            logger.LogWarning("Email not configured (SmtpHost empty) — skipping send");
            return;
        }

        var validRecipients = recipients.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (validRecipients.Count == 0)
        {
            logger.LogInformation("No recipients configured — email not sent: {Subject}", subject);
            return;
        }

        using var client = new SmtpClient(_config.SmtpHost, _config.SmtpPort)
        {
            Credentials = new NetworkCredential(_config.Username, _config.Password),
            EnableSsl = true
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_config.FromAddress),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        foreach (var address in validRecipients)
            message.To.Add(address);

        await client.SendMailAsync(message);
        logger.LogInformation("Email sent to {Count} recipient(s): {Subject}", validRecipients.Count, subject);
    }

    public async Task SendSimpleEmailAsync(List<string> recipients, string markdown, string subject)
    {
        var bodyHtml = Markdown.ToHtml(markdown, Pipeline);
        var fullHtml = WrapHtml(bodyHtml);
        await SendAsync(recipients, subject, fullHtml);
    }

    public async Task SendDailyReportAsync(List<string> recipients, string reportMarkdown)
    {
        var bodyHtml = Markdown.ToHtml(reportMarkdown, Pipeline);
        var fullHtml = WrapHtml(bodyHtml);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SendAsync(recipients, $"SwingTrader Daily Brief — {today:dd MMM yyyy}", fullHtml);
    }

    private static string WrapHtml(string body)
    {
        const string css = """
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                   max-width: 700px; margin: 0 auto; padding: 20px; color: #1a1a1a; }
            table { border-collapse: collapse; width: 100%; margin: 8px 0; }
            td, th { border: 1px solid #ddd; padding: 6px 10px; text-align: left; }
            tr:nth-child(even) { background: #f9f9f9; }
            blockquote { border-left: 4px solid #e0e0e0; margin: 8px 0; padding: 4px 12px;
                         color: #666; background: #fafafa; }
            hr { border: none; border-top: 1px solid #e0e0e0; margin: 20px 0; }
            h1 { font-size: 1.6em; margin-bottom: 4px; }
            h2 { font-size: 1.2em; color: #333; margin-top: 24px; }
            h3 { font-size: 1.05em; color: #555; margin-top: 16px; }
            code { background: #f4f4f4; padding: 2px 4px; border-radius: 3px; font-size: 0.9em; }
            """;

        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{css}</style></head><body>{body}</body></html>";
    }
}
