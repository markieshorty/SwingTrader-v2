using System.Security.Cryptography;
using System.Text;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

// The exit dials a replay ran under, plus a stable identity for them
// (docs/scoring-engine-plan SPEC §3, requirement "frozen dials").
//
// Every ShadowOutcome records the version of the dial set that produced it, and
// rows are only comparable within a version. This is what stops the second
// sweep making the first sweep's results uninterpretable - the defect that
// already bites the Almost tab, which replays with TODAY's tactics against
// signals scored under older ones and reports the difference as insight.
public sealed record SetupDials(
    decimal StopLossPct,
    decimal TargetPct,
    int GuideHoldDays,
    decimal TrailingActivationPct,
    decimal TrailingDistancePct);

public sealed class DialSet
{
    private readonly IReadOnlyDictionary<SetupType, SetupDials> _bySetup;
    private readonly SetupDials _fallback;

    public string Version { get; }

    private DialSet(IReadOnlyDictionary<SetupType, SetupDials> bySetup, SetupDials fallback, string version)
    {
        _bySetup = bySetup;
        _fallback = fallback;
        Version = version;
    }

    public SetupDials For(SetupType setup) =>
        _bySetup.TryGetValue(setup, out var d) ? d : _fallback;

    // Builds from the account's per-setup tactics, falling back to the risk
    // profile for setups with no tactics row - the same resolution order the
    // live monitor and the Almost tab use, so a replay reproduces what would
    // actually have happened rather than an idealised variant.
    public static DialSet FromAccount(
        IEnumerable<SetupTactics> tactics, AccountRiskProfile profile)
    {
        var fallback = new SetupDials(
            profile.StopLossPct, profile.TargetPct, profile.MaxHoldDays,
            (decimal)profile.TrailingActivationPct, (decimal)profile.TrailingDistancePct);

        var bySetup = tactics.ToDictionary(
            t => t.SetupType,
            t => new SetupDials(
                t.StopLossPct, t.TargetPct, t.GuideHoldDays,
                (decimal)t.TrailingActivationPct, (decimal)t.TrailingDistancePct));

        return new DialSet(bySetup, fallback, ComputeVersion(bySetup, fallback));
    }

    // Deterministic across processes and runs: setups in enum order, invariant
    // formatting, fixed decimal places. A version that shifted with culture or
    // dictionary ordering would silently split one dial set into several and
    // fragment the calibration population.
    private static string ComputeVersion(
        IReadOnlyDictionary<SetupType, SetupDials> bySetup, SetupDials fallback)
    {
        var sb = new StringBuilder();
        void Append(string label, SetupDials d) =>
            sb.Append(label).Append(':')
              .Append(d.StopLossPct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(d.TargetPct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(d.GuideHoldDays.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(d.TrailingActivationPct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(d.TrailingDistancePct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(';');

        Append("*", fallback);
        foreach (var setup in bySetup.Keys.OrderBy(k => (int)k))
        {
            Append(((int)setup).ToString(System.Globalization.CultureInfo.InvariantCulture), bySetup[setup]);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        // 16 hex chars is ample to separate a handful of dial sets and keeps the
        // column narrow enough to index comfortably.
        return "pct-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
