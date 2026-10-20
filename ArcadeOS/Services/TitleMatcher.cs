using System.Text.RegularExpressions;
namespace ArcadeOS.Services;

public static class TitleMatcher
{
    // Similarity, not a probability. Ambiguous runners-up are deliberately left unmatched.
    public static string? Find(string requested, IEnumerable<string> candidates, bool allowSubtitle = false)
    {
        string query = MetadataNames.Normalize(requested);
        if (query.Length < 5) return null;
        string Numbers(string s) => string.Join(",", Regex.Matches(s, @"\d+").Select(m => m.Value));
        var ranked = candidates.Select(name => (Name: name, Key: MetadataNames.Normalize(name)))
            .Where(x => Numbers(query) == Numbers(x.Key))
            .Select(x => (x.Name, Score: allowSubtitle && x.Key.StartsWith(query, StringComparison.Ordinal) ? .94 : Similarity(query, x.Key)))
            .OrderByDescending(x => x.Score).Take(2).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= .86 && (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= .06)
            ? ranked[0].Name : null;
    }
    private static double Similarity(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1]; current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1d - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
