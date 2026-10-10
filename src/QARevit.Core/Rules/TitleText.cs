using System.Text.RegularExpressions;

namespace QARevit.Core.Rules;

public static class TitleText
{
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Rapikan spasi: trim, spasi ganda jadi satu.</summary>
    public static string NormalizeWhitespace(string name) => MultiSpace.Replace(name.Trim(), " ");

    public static bool ContainsWord(string title, string keyword) =>
        title.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ganti typo yang terdaftar (case-insensitive, utuh per kata/frasa).</summary>
    public static string FixTypos(string name, IReadOnlyDictionary<string, string> typos)
    {
        var result = name;
        foreach (var (wrong, right) in typos)
            result = Regex.Replace(result, $@"\b{Regex.Escape(wrong)}\b", right, RegexOptions.IgnoreCase);
        return result;
    }
}
