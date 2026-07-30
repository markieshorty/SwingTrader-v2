using FluentAssertions;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Manual-resume veto: when the owner manually resumes entries that an AUTO
// pause (circuit breaker / regime autopause) stopped, further auto pauses are
// vetoed for the rest of that ET trading day - otherwise the next Monitor
// cycle re-checks the same condition and flips the pause straight back on.
public class AccountAutoPauseVetoTests
{
    private static readonly DateOnly Today = new(2026, 7, 30);

    [Theory]
    [InlineData(ExecutionPauseReason.CircuitBreaker)]
    [InlineData(ExecutionPauseReason.RegimeAutopause)]
    public void ManualResumeOfAutoPause_SetsVetoForThatDay(ExecutionPauseReason reason)
    {
        var account = new Account();
        account.PauseExecution(TradingMode.Live, reason, DateTime.UtcNow);

        account.ResumeExecution(TradingMode.Live, vetoAutoPauseForEtDay: Today);

        account.IsExecutionPaused(TradingMode.Live).Should().BeFalse();
        account.IsAutoPauseVetoed(TradingMode.Live, Today).Should().BeTrue();
        // Expires by itself at the ET day rollover.
        account.IsAutoPauseVetoed(TradingMode.Live, Today.AddDays(1)).Should().BeFalse();
        // Per-mode: Demo is untouched.
        account.IsAutoPauseVetoed(TradingMode.Demo, Today).Should().BeFalse();
    }

    [Fact]
    public void ManualResumeOfManualPause_DoesNotSetVeto()
    {
        var account = new Account();
        account.PauseExecution(TradingMode.Live, ExecutionPauseReason.Manual, DateTime.UtcNow);

        account.ResumeExecution(TradingMode.Live, vetoAutoPauseForEtDay: Today);

        account.IsAutoPauseVetoed(TradingMode.Live, Today).Should().BeFalse();
    }

    [Fact]
    public void AutoResume_WithoutVetoDate_DoesNotSetVeto()
    {
        // The regime auto-resume path passes no date - the machine releasing
        // its own pause must never veto the machine.
        var account = new Account();
        account.PauseExecution(TradingMode.Live, ExecutionPauseReason.RegimeAutopause, DateTime.UtcNow);

        account.ResumeExecution(TradingMode.Live);

        account.IsAutoPauseVetoed(TradingMode.Live, Today).Should().BeFalse();
    }
}
