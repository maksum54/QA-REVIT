namespace QARevit.Core.Register;

/// <summary>Cocokkan teks kop dari Excel ("Family : Type", nama family, atau nama type) ke daftar kop di model.</summary>
public static class TitleBlockMatcher
{
    public static T? Find<T>(string? requested, IEnumerable<T> options, Func<T, string> family, Func<T, string> type) where T : class
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var q = Norm(requested);
        var list = options.ToList();
        return list.FirstOrDefault(o => Norm($"{family(o)} : {type(o)}") == q)
            ?? list.FirstOrDefault(o => Norm($"{family(o)}:{type(o)}") == q)
            ?? list.FirstOrDefault(o => Norm(type(o)) == q)
            ?? list.FirstOrDefault(o => Norm(family(o)) == q);
    }

    private static string Norm(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Replace(" : ", ":").Replace(" :", ":").Replace(": ", ":").ToUpperInvariant();
}
