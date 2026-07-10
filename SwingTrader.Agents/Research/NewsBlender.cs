using System.Text;

namespace SwingTrader.Agents.Research;

// One article normalised from either news source, ready for the sentiment
// prompt and the archive.
public sealed record NewsArticle(
    string Source,          // "Finnhub" | "Tiingo"
    string Title,
    string? Summary,
    DateTime PublishedAtUtc,
    string? Url);

// Pure merge of the two news feeds for the sentiment prompt: dedup first
// (both feeds carry the same PR headlines), newest first, capped. Kept free
// of HTTP/config so the dedup rules are directly testable.
public static class NewsBlender
{
    public static List<NewsArticle> Blend(IEnumerable<NewsArticle> articles, int maxArticles)
    {
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<NewsArticle>();

        // Newest-first BEFORE dedup so the survivor of a duplicate pair is the
        // fresher copy (its summary/description tends to be the fuller one).
        foreach (var article in articles.OrderByDescending(a => a.PublishedAtUtc))
        {
            if (string.IsNullOrWhiteSpace(article.Title)) continue;

            if (!string.IsNullOrWhiteSpace(article.Url) && !seenUrls.Add(article.Url!.Trim()))
                continue;
            // Same story republished across aggregators has a different URL
            // but a near-identical headline - normalised title is the
            // cross-source key.
            if (!seenTitles.Add(NormaliseTitle(article.Title)))
                continue;

            result.Add(article);
            if (result.Count >= maxArticles) break;
        }

        return result;
    }

    // Lowercase alphanumeric-only: case, punctuation, quoting style and
    // spacing all vary across syndicated copies of one headline.
    internal static string NormaliseTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
