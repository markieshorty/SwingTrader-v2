using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwingTrader.Agents.Filings;

// Pure text machinery for the filing-delta pipeline (docs/filing-delta-plan):
// HTML -> plain text, Item-boundary section extraction, normalization + hash
// (the gate that keeps Claude out of copy-paste quarters), and the
// paragraph-level diff that bounds tokens when text DID change. Everything
// here is deterministic and testable without touching EDGAR.
public static partial class FilingTextExtractor
{
    public sealed record Sections(string? RiskFactors, string? Mda);

    // ── HTML -> text ──────────────────────────────────────────────────────────

    public static string HtmlToText(string html)
    {
        // Strip script/style wholesale, turn block-ish closers into newlines,
        // drop remaining tags, decode entities, squeeze whitespace. Filing
        // HTML is machine-generated soup - a heuristic pass beats a full DOM
        // parser here because we only need stable TEXT, not structure.
        var text = ScriptStyleRegex().Replace(html, " ");
        text = BlockCloseRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = text.Replace(' ', ' ');
        text = HorizontalWhitespaceRegex().Replace(text, " ");
        text = BlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    // ── Section extraction ────────────────────────────────────────────────────

    // 10-K: Item 1A (Risk Factors) ends at Item 1B/2; MD&A is Item 7 ending at
    // 7A/8. 10-Q: Risk Factors is Part II Item 1A ending at Item 2/5/6; MD&A
    // is Part I Item 2 ending at Item 3/4. Boundaries are located on the
    // LAST occurrence of each header (early occurrences are the table of
    // contents). Missing/ambiguous section -> null, never a throw.
    public static Sections ExtractSections(string plainText, string filingType) =>
        filingType switch
        {
            "10-K" => new Sections(
                Slice(plainText, ["item 1a"], ["item 1b", "item 2"]),
                Slice(plainText, ["item 7"], ["item 7a", "item 8"], excludeStarts: ["item 7a"])),
            "10-Q" => new Sections(
                Slice(plainText, ["item 1a"], ["item 2", "item 5", "item 6"]),
                Slice(plainText, ["item 2"], ["item 3", "item 4"])),
            _ => new Sections(null, null),
        };

    // Evaluates EVERY start-header occurrence (each ended by the first end
    // header after it) and keeps the LONGEST resulting section. Longest-wins
    // handles both failure modes of a positional heuristic at once: a
    // table-of-contents hit produces a tiny "section" (its end header is the
    // very next ToC line), and a 10-Q's ambiguous "Item 2" (Part I = the real
    // MD&A, Part II = the short Unregistered Sales item) resolves to the
    // MD&A because it is pages long - last-occurrence used to pick Part II.
    // Headers are matched at line starts to avoid mid-sentence mentions
    // ("see Item 1A above").
    private static string? Slice(
        string text, string[] startHeaders, string[] endHeaders, string[]? excludeStarts = null)
    {
        var lower = text.ToLowerInvariant();

        var starts = new List<int>();
        foreach (var header in startHeaders)
        {
            foreach (Match m in LineStartOccurrences(lower, header))
            {
                // "item 7" must not match "item 7a" when 7a is a different section.
                if (excludeStarts is not null && excludeStarts.Any(ex =>
                        lower.Length >= m.Index + ex.Length &&
                        lower.Substring(m.Index, ex.Length) == ex))
                    continue;
                starts.Add(m.Index);
            }
        }
        if (starts.Count == 0) return null;

        var endCandidates = endHeaders
            .SelectMany(h => LineStartOccurrences(lower, h).Select(m => m.Index))
            .OrderBy(i => i)
            .ToList();

        string? best = null;
        foreach (var start in starts)
        {
            var end = endCandidates.FirstOrDefault(i => i > start + 50, text.Length);
            var section = text[start..end].Trim();
            if (best is null || section.Length > best.Length) best = section;
        }

        // A "section" of a few hundred chars is a table-of-contents hit, not
        // the real thing - treat as extraction failure rather than hashing
        // noise that would flag every quarter as changed.
        return best is { Length: >= 500 } ? best : null;
    }

    private static MatchCollection LineStartOccurrences(string lowerText, string header) =>
        Regex.Matches(lowerText, $@"(?m)^[ \t]*{Regex.Escape(header)}\b", RegexOptions.CultureInvariant);

    // ── Normalize + hash (the gate) ──────────────────────────────────────────

    // Case-folded, whitespace-collapsed, digits stripped: page numbers and
    // dollar figures change every quarter even in copy-paste text, and the
    // signal we are gating on is LANGUAGE change, not number change (numbers
    // are the fundamentals pipeline's job).
    public static string Normalize(string text)
    {
        var t = text.ToLowerInvariant();
        t = DigitsRegex().Replace(t, "");
        t = AnyWhitespaceRegex().Replace(t, " ");
        return t.Trim();
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    // ── Paragraph diff ────────────────────────────────────────────────────────

    public sealed record ParagraphDiff(List<string> Added, List<string> Removed);

    // Set difference of normalized paragraphs: what appears in the new text
    // but not the old (Added) and vice versa (Removed). Order-insensitive by
    // design - reshuffled boilerplate is not a change worth tokens.
    public static ParagraphDiff DiffParagraphs(string oldText, string newText)
    {
        static Dictionary<string, string> Index(string text) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.Length >= 80) // headings/fragments aren't diffable prose
                .GroupBy(Normalize)
                .ToDictionary(g => g.Key, g => g.First());

        var oldParas = Index(oldText);
        var newParas = Index(newText);

        return new ParagraphDiff(
            newParas.Where(kv => !oldParas.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList(),
            oldParas.Where(kv => !newParas.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList());
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"</(p|div|tr|table|h[1-6]|li|br)\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"[ \t]*\n[ \t]*(\n[ \t]*)+")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AnyWhitespaceRegex();
}
