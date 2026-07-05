namespace SwingTrader.Core.Enums;

public enum AccountRole
{
    Owner,   // created the account; can invite/remove members, manage billing/settings
    Member,  // invited; full access to shared trading data, cannot invite/remove others
}
