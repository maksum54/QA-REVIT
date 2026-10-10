using QARevit.Core.Config;
using QARevit.Core.Model;

namespace QARevit.Core.Rules;

/// <summary>Struktur nomor sheet dan semua kodenya harus terdaftar di config.</summary>
public sealed class CodeFormatRule : IQaRule
{
    public string Id => "CODE-FORMAT";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            var code = SheetCode.TryParse(s.Number);
            if (code is null)
            {
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Format nomor sheet tidak sesuai standar.",
                    Advice = "Gunakan pola PROYEK-DWG-STAGE-ME-ZONA-SISTEM-NNNN, contoh KRF-DWG-BD-ME-01.1-LP-1101.",
                };
                continue;
            }
            foreach (var problem in code.Validate(cfg))
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Nomor sheet: {problem}.",
                    Advice = "Perbaiki nomor sheet, atau tambahkan kode baru ke file konfigurasi proyek bila memang valid.",
                };
        }
    }
}

public sealed class DuplicateNumberRule : IQaRule
{
    public string Id => "DUPLICATE";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        // Revit menolak nomor yang persis sama, jadi yang dicari: sama setelah abaikan stage/spasi/huruf besar.
        var groups = sheets
            .GroupBy(s => SheetCode.TryParse(s.Number)?.WithStage("*").Format() ?? s.Number.Trim().ToUpperInvariant())
            .Where(g => g.Count() > 1);
        foreach (var g in groups)
            foreach (var s in g)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Nomor ganda dengan: {string.Join(", ", g.Where(o => o != s).Select(o => o.Number))}.",
                    Advice = "Satu gambar hanya boleh punya satu nomor. Hapus sheet lama atau beri nomor urut berikutnya.",
                };
    }
}

/// <summary>Satu set gambar harus satu proyek dan satu stage.</summary>
public sealed class ConsistencyRule : IQaRule
{
    public string Id => "CONSISTENCY";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var parsed = sheets.Select(s => (Sheet: s, Code: SheetCode.TryParse(s.Number)))
            .Where(x => x.Code is not null).ToList();
        if (parsed.Count == 0) yield break;

        foreach (var issue in Minority(parsed, x => x.Code!.Project, "kode proyek", cfg)) yield return issue;
        foreach (var issue in Minority(parsed, x => x.Code!.Stage, "stage", cfg)) yield return issue;
    }

    private IEnumerable<QaIssue> Minority(List<(SheetInfo Sheet, SheetCode? Code)> parsed,
        Func<(SheetInfo Sheet, SheetCode? Code), string> key, string label, QaConfig cfg)
    {
        var majority = parsed.GroupBy(key).OrderByDescending(g => g.Count()).First().Key;
        foreach (var x in parsed.Where(x => key(x) != majority))
        {
            var isStage = label == "stage";
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Warning, SheetNumber = x.Sheet.Number, UniqueId = x.Sheet.UniqueId,
                Message = $"Beda {label}: '{key(x)}' sementara mayoritas set memakai '{majority}'" +
                          (isStage ? $" ({cfg.StageName(majority)})." : "."),
                Advice = isStage
                    ? $"Samakan stage seluruh set. Gunakan 'Ganti Stage' untuk mengubah semua nomor ke {majority} sekaligus."
                    : $"Pastikan sheet ini memang milik proyek {majority}.",
                Fix = isStage ? FixKind.RenumberSheet : FixKind.None,
                FixValue = isStage ? x.Code!.WithStage(majority).Format() : null,
            };
        }
    }
}

/// <summary>Judul sheet harus sesuai kode sistemnya (LP → LIGHTING, EG → GROUNDING, ...).</summary>
public sealed class SystemTitleRule : IQaRule
{
    public string Id => "SYSTEM-TITLE";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            var code = SheetCode.TryParse(s.Number);
            if (code is null || !cfg.Systems.TryGetValue(code.System, out var sys) || sys.Keywords.Count == 0) continue;
            if (sys.Keywords.Any(k => TitleText.ContainsWord(s.Name, k))) continue;

            var guess = cfg.Systems.FirstOrDefault(kv => kv.Key != code.System &&
                kv.Value.Keywords.Any(k => TitleText.ContainsWord(s.Name, k)));
            var hint = guess.Key is null ? "" : $" Judulnya lebih cocok dengan sistem {guess.Key} ({guess.Value.Name}).";
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                Message = $"Kode sistem {code.System} ({sys.Name}) tidak cocok dengan judul \"{s.Name}\".{hint}",
                Advice = guess.Key is null
                    ? $"Pastikan judul memuat salah satu kata: {string.Join(", ", sys.Keywords)}."
                    : $"Ganti kode sistem menjadi {guess.Key}, atau perbaiki judul sheet.",
                Fix = guess.Key is null ? FixKind.None : FixKind.RenumberSheet,
                FixValue = guess.Key is null ? null : (code with { System = guess.Key }).Format(),
            };
        }
    }
}

/// <summary>Digit pertama nomor urut menentukan jenis gambar (1 layout, 3 section, 4 enlarged, 6 schematic).</summary>
public sealed class DrawingTypeTitleRule : IQaRule
{
    public string Id => "DRAWING-TYPE";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            var code = SheetCode.TryParse(s.Number);
            if (code is null || !cfg.DrawingTypes.TryGetValue(code.DrawingType.ToString(), out var own)) continue;
            if (code.System == "EL") continue; // drawing list / cover punya aturan sendiri

            var ownMatch = own.Keywords.Any(k => TitleText.ContainsWord(s.Name, k));
            var other = cfg.DrawingTypes.FirstOrDefault(kv => kv.Key != code.DrawingType.ToString() &&
                kv.Value.MustContainKeyword && kv.Value.Keywords.Any(k => TitleText.ContainsWord(s.Name, k)));

            if (!ownMatch && other.Key is not null)
            {
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Judul menunjukkan jenis {other.Value.Name} ({other.Key}xxx) tetapi nomor memakai {code.DrawingType}xxx ({own.Name}).",
                    Advice = $"Pindahkan ke seri {other.Key}xxx sesuai jenis gambar.",
                };
            }
            else if (own.MustContainKeyword && !ownMatch)
            {
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Info, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Seri {code.DrawingType}xxx ({own.Name}) tetapi judul tidak memuat: {string.Join(", ", own.Keywords)}.",
                    Advice = "Periksa apakah judul atau seri nomornya yang keliru.",
                };
            }
        }
    }
}

/// <summary>Spasi ganda, spasi di ujung, typo umum.</summary>
public sealed class TitleTextRule : IQaRule
{
    public string Id => "TITLE-TEXT";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Judul sheet kosong.", Advice = "Isi judul sesuai drawing list.",
                };
                continue;
            }
            var spaced = TitleText.NormalizeWhitespace(s.Name);
            var fixedName = TitleText.FixTypos(spaced, cfg.Typos);
            if (fixedName == s.Name) continue;

            var reasons = new List<string>();
            if (spaced != s.Name) reasons.Add("spasi ganda/berlebih");
            if (fixedName != spaced) reasons.Add("typo");
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                Message = $"Judul ada {string.Join(" dan ", reasons)}: \"{s.Name}\".",
                Advice = $"Ganti menjadi \"{fixedName}\" (bisa Fix otomatis).",
                Fix = FixKind.RenameSheet, FixValue = fixedName,
            };
        }
    }
}

public sealed class EmptySheetRule : IQaRule
{
    public string Id => "EMPTY-SHEET";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets.Where(s => !s.IsPlaceholder && s.PlacedViewCount == 0))
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                Message = "Sheet tidak berisi view atau schedule.",
                Advice = "Tempatkan view yang sesuai, atau hapus sheet bila tidak dipakai.",
            };
    }
}

/// <summary>Stage konstruksi (DC) wajib lengkap: revisi, tanggal, checked/approved, tanpa cloud yang belum issued.</summary>
public sealed class ConstructionStageRule : IQaRule
{
    public string Id => "DC-READY";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            var code = SheetCode.TryParse(s.Number);
            if (code is null || !string.Equals(code.Stage, cfg.ConstructionStage, StringComparison.OrdinalIgnoreCase)) continue;

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(s.CurrentRevision)) missing.Add("Current Revision");
            if (string.IsNullOrWhiteSpace(s.CurrentRevisionDate)) missing.Add("Revision Date");
            foreach (var p in cfg.ConstructionRequiredParameters)
                if (!s.Parameters.TryGetValue(p, out var v) || string.IsNullOrWhiteSpace(v)) missing.Add(p);

            if (missing.Count > 0)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Stage {cfg.StageName(code.Stage)}: parameter kosong — {string.Join(", ", missing)}.",
                    Advice = "Gambar konstruksi wajib punya revisi ter-issue, tanggal, dan nama pemeriksa/penyetuju di title block.",
                };
            if (s.HasUnissuedRevisionClouds)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Ada revision cloud dengan revisi yang belum di-issue.",
                    Advice = "Issue revisinya di Sheet Issues/Revisions sebelum gambar dikirim untuk konstruksi.",
                };
            if (s.IsPlaceholder)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Error, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Sheet placeholder masih ada di set konstruksi.",
                    Advice = "Buat gambar sebenarnya atau keluarkan dari set DC.",
                };
        }
    }
}

/// <summary>Per zona: kalau ada layout lighting/receptacle/grounding, ketiganya biasanya harus ada.</summary>
public sealed class ZoneCompletenessRule : IQaRule
{
    public string Id => "ZONE-COMPLETE";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var zc = cfg.ZoneCompleteness;
        if (!zc.Enabled) yield break;

        var byZone = sheets.Select(s => SheetCode.TryParse(s.Number)).Where(c => c is not null && c.Zone != "00")
            .GroupBy(c => c!.Zone);
        foreach (var zone in byZone)
        {
            var systems = zone.Select(c => c!.System).ToHashSet();
            if (!systems.Overlaps(zc.WhenAnyOf)) continue;
            var missing = zc.Expect.Where(e => !systems.Contains(e)).ToList();
            if (missing.Count == 0) continue;
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Info, SheetNumber = $"Zona {zone.Key}",
                Message = $"Zona {zone.Key} belum punya gambar sistem: {string.Join(", ", missing.Select(m => cfg.Systems.TryGetValue(m, out var d) ? $"{m} ({d.Name})" : m))}.",
                Advice = "Pastikan memang tidak diperlukan di zona ini, atau tambahkan sheet-nya ke register.",
            };
        }
    }
}
