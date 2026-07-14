namespace SwingTrader.Agents;

// Claude is prompted for raw JSON but intermittently decorates it - markdown
// fences, a stray trailing ``` after bare JSON, a sentence of preamble. The
// per-service fence-stripping heuristics only handled the exact
// ```json\n{...}\n``` shape and let other variants through to the
// deserializer (14 Jul 2026: 5/19 bellwether scores failed on a trailing
// fence). Extracting the outermost JSON value span is robust to all of them.
public static class ClaudeJson
{
    // Returns the substring from the first '{' or '[' to the last matching
    // '}' or ']' - the JSON value with any decoration discarded. Falls back
    // to the trimmed input when no JSON delimiters are found, so the caller's
    // deserializer still throws its usual descriptive error.
    public static string Extract(string raw)
    {
        var text = raw.Trim();

        var objStart = text.IndexOf('{');
        var arrStart = text.IndexOf('[');
        var start = (objStart, arrStart) switch
        {
            (-1, -1) => -1,
            (-1, _) => arrStart,
            (_, -1) => objStart,
            _ => Math.Min(objStart, arrStart),
        };
        if (start < 0) return text;

        var closer = text[start] == '{' ? '}' : ']';
        var end = text.LastIndexOf(closer);
        if (end <= start) return text;

        return text[start..(end + 1)];
    }
}
