using System.Text.RegularExpressions;

namespace SwingTrader.Agents.Filings;

// Rules-based corporate-distress detection (docs/filing-delta-plan FD3) - NO
// Claude involvement, by design: the high-value doom signals are structured
// (8-K item codes) or a narrow phrase ("going concern"), so a parser catches
// them at zero token cost and near-zero false positives. Pure static so the
// rules are directly testable.
public static partial class DistressDetector
{
    // The 8-K items that scream "this company may be uninvestable soon":
    //   3.01 - notice of delisting / failure to satisfy a continued-listing rule
    //   1.03 - bankruptcy or receivership
    //   4.02 - non-reliance on previously issued financials (restatement)
    // Deliberately a short list: item codes like 2.02 (results) or 5.02
    // (officer changes) are routine and would drown the signal.
    private static readonly Dictionary<string, string> DistressItems = new()
    {
        ["3.01"] = "8-K Item 3.01: notice of delisting or failure to satisfy a continued listing rule",
        ["1.03"] = "8-K Item 1.03: bankruptcy or receivership",
        ["4.02"] = "8-K Item 4.02: non-reliance on previously issued financial statements",
    };

    // Item codes arrive comma-separated from EDGAR's submissions JSON (e.g.
    // "3.01,9.01"). Returns the human-readable reason per matched code.
    public static IReadOnlyList<string> DetectFromItems(string? items)
    {
        if (string.IsNullOrWhiteSpace(items)) return [];
        return items.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(DistressItems.ContainsKey)
            .Select(code => DistressItems[code])
            .ToList();
    }

    // Going-concern doubt in filing text: "substantial doubt" within ~300
    // chars of "going concern" - the standard disclosure phrasing ("substantial
    // doubt about the Company's ability to continue as a going concern").
    // Scanned over the MD&A section ONLY: risk-factor sections routinely
    // discuss going concern hypothetically ("could raise substantial doubt"),
    // while an MD&A statement is management's actual conclusion. A rare false
    // positive costs a recoverable Watch-demotion, not a hard block.
    public static bool HasGoingConcernLanguage(string? mdaText) =>
        mdaText is not null && GoingConcernRegex().IsMatch(mdaText);

    [GeneratedRegex(
        @"substantial\s+doubt[\s\S]{0,300}?going\s+concern|going\s+concern[\s\S]{0,300}?substantial\s+doubt",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoingConcernRegex();
}
