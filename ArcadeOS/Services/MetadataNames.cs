using System.Text.RegularExpressions;

namespace ArcadeOS.Services;

public static class MetadataNames
{
    public static string Clean(string title) => Regex.Replace(title, @"\s*(\([^)]*\)|\[[^\]]*\])", "").Trim();
    public static string Normalize(string title) => Regex.Replace(Clean(title).ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
}
