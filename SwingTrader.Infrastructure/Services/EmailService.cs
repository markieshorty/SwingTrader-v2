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
            // Display name so recipients see "Cadentic <support@…>" rather
            // than a bare address. Falls back to the brand default if unset.
            From = new MailAddress(_config.FromAddress,
                string.IsNullOrWhiteSpace(_config.FromName) ? "Cadentic" : _config.FromName),
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
        await SendAsync(recipients, $"Cadentic Daily Brief — {today:dd MMM yyyy}", fullHtml);
    }

    private static string WrapHtml(string body)
    {
        // Branded wrapper (24 Jul 2026): navy masthead with the Cadentic.trade
        // wordmark, brand-blue accents, pale-blue table striping - matches the
        // app's palette. Email-safe fonts only (no webfont loading in mail
        // clients); inline-ish CSS kept simple for client compatibility.
        const string css = """
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                   margin: 0; padding: 0; background: #f2f7fc; color: #0b1220; }
            .frame { max-width: 700px; margin: 0 auto; padding: 0 0 28px; }
            .masthead { background: #0f172a; border-radius: 0 0 10px 10px; padding: 16px 24px; }
            .masthead .brand { color: #f1f5f9; font-size: 20px; font-weight: 800; letter-spacing: 0.5px; }
            .masthead .brand .tld { color: #94a3b8; font-weight: 500; }
            .content { background: #ffffff; border: 1px solid #dbe8f4; border-radius: 10px;
                       margin-top: 14px; padding: 22px 26px; }
            table { border-collapse: collapse; width: 100%; margin: 8px 0; }
            td, th { border: 1px solid #dbe8f4; padding: 6px 10px; text-align: left; }
            th { background: #eaf2fa; color: #0b1220; }
            tr:nth-child(even) { background: #f7fafd; }
            blockquote { border-left: 4px solid #2563eb; margin: 8px 0; padding: 4px 12px;
                         color: #52657a; background: #f2f7fc; }
            hr { border: none; border-top: 1px solid #dbe8f4; margin: 20px 0; }
            h1 { font-size: 1.5em; margin: 0 0 4px; color: #0b1220; }
            h2 { font-size: 1.15em; color: #1d4ed8; margin-top: 24px; }
            h3 { font-size: 1.02em; color: #52657a; margin-top: 16px;
                 text-transform: uppercase; letter-spacing: 0.05em; font-size: 0.82em; }
            code { background: #eaf2fa; padding: 2px 4px; border-radius: 3px; font-size: 0.9em; }
            a { color: #2563eb; }
            .footer { text-align: center; color: #94a3b8; font-size: 11px; margin-top: 16px; }
            """;

        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" + css + "</style></head><body>"
            + "<div class=\"frame\">"
            + "<div class=\"masthead\"><span class=\"brand\">Cadentic<span class=\"tld\">.trade</span></span></div>"
            + "<div class=\"content\">" + body + "</div>"
            + "<div class=\"footer\">Cadentic — markets, on a cadence. Not financial advice.</div>"
            + "</div></body></html>";
    }
}
