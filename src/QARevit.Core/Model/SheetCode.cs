using System.Text.RegularExpressions;
using QARevit.Core.Config;

namespace QARevit.Core.Model;

/// <summary>
/// Nomor sheet terurai, contoh KRF-DWG-BD-ME-01.1-LP-4401[.2]
/// = proyek-tipe dokumen-stage-disiplin-zona-sistem-(jenis gambar + urutan)[.phase]
/// </summary>
public sealed record SheetCode(
    string Project, string DocType, string Stage, string Discipline,
    string Zone, string System, char DrawingType, string Sequence, string? Phase)
{
    private static readonly Regex Loose = new(
        @"^(?<p>[A-Z0-9]{2,6})-(?<d>[A-Z]{2,4})-(?<s>[A-Z]{2})-(?<disc>[A-Z]{2})-(?<z>\d{2}(?:\.\d+)?)-(?<sys>[A-Z]{2})-(?<t>\d)(?<n>\d{3})(?:\.(?<ph>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Number => $"{DrawingType}{Sequence}";

    /// <summary>Urai struktur saja (tanpa cek daftar kode yang valid).</summary>
    public static SheetCode? TryParse(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var m = Loose.Match(number.Trim());
        if (!m.Success) return null;
        return new SheetCode(m.Groups["p"].Value, m.Groups["d"].Value, m.Groups["s"].Value, m.Groups["disc"].Value,
            m.Groups["z"].Value, m.Groups["sys"].Value, m.Groups["t"].Value[0], m.Groups["n"].Value,
            m.Groups["ph"].Success ? m.Groups["ph"].Value : null);
    }

    public string Format() =>
        $"{Project}-{DocType}-{Stage}-{Discipline}-{Zone}-{System}-{DrawingType}{Sequence}" + (Phase is null ? "" : $".{Phase}");

    public SheetCode WithStage(string stage) => this with { Stage = stage };

    /// <summary>Daftar pelanggaran terhadap kode yang terdaftar di config.</summary>
    public IEnumerable<string> Validate(QaConfig cfg)
    {
        if (cfg.ProjectCodes.Count > 0 && !cfg.ProjectCodes.Contains(Project, StringComparer.OrdinalIgnoreCase))
            yield return $"kode proyek '{Project}' tidak terdaftar ({string.Join(", ", cfg.ProjectCodes)})";
        if (!string.Equals(DocType, cfg.DocType, StringComparison.OrdinalIgnoreCase))
            yield return $"tipe dokumen '{DocType}' seharusnya '{cfg.DocType}'";
        if (cfg.StageIndex(Stage) < 0)
            yield return $"stage '{Stage}' tidak dikenal ({string.Join(", ", cfg.Stages.Select(s => s.Code))})";
        if (!string.Equals(Discipline, cfg.Discipline, StringComparison.OrdinalIgnoreCase))
            yield return $"disiplin '{Discipline}' seharusnya '{cfg.Discipline}'";
        if (!cfg.Systems.ContainsKey(System))
            yield return $"kode sistem '{System}' tidak dikenal";
        if (cfg.DrawingTypes.Count > 0 && !cfg.DrawingTypes.ContainsKey(DrawingType.ToString()))
            yield return $"jenis gambar '{DrawingType}xxx' tidak dikenal";
    }
}
