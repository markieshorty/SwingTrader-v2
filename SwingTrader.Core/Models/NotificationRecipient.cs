using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class NotificationRecipient : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public NotificationCategory Categories { get; set; } = NotificationCategory.All;
}
