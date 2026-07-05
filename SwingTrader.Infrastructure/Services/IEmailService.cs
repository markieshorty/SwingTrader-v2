namespace SwingTrader.Infrastructure.Services;

public interface IEmailService
{
    Task SendAsync(List<string> recipients, string subject, string htmlBody);
    Task SendDailyReportAsync(List<string> recipients, string reportMarkdown);
    Task SendSimpleEmailAsync(List<string> recipients, string markdown, string subject);
}
