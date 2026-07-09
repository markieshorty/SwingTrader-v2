namespace SwingTrader.Infrastructure.Configuration;

// SMTP credentials are a platform-level secret (one relay for all accounts),
// unlike Finnhub/Tiingo/T212/Claude keys — recipients themselves are per-account
// (NotificationRecipient), not configured here.
public class EmailConfig
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string FromAddress { get; set; } = string.Empty;
    // Display name shown on the "From" line, e.g. "Acme Trading <support@…>".
    // Optional - EmailService falls back to the brand name if empty.
    public string FromName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
