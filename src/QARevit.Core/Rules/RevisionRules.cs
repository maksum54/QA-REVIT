using System.Globalization;
using QARevit.Core.Config;
using QARevit.Core.Model;

namespace QARevit.Core.Rules;

// Aturan di file ini menangkap hal yang sering terlewat saat issue: revisi, cloud, kop, dan drawing list
// diisi di tempat berbeda di Revit, jadi mudah tidak sinkron.

/// <summary>Kolom tanggal yang dicentang di sheet list harus sama dengan revisi yang menempel di sheet.</summary>
public sealed class SheetListSyncRule : IQaRule
{
    public string Id => "REV-SHEETLIST";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            if (s.TickedIssueDates is not { } ticked) continue;
            var dates = s.Revisions.Select(r => r.Date).ToHashSet();
            if (dates.SetEquals(ticked)) continue;
            yield return new QaIssue
            {
                RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                Message = $"Drawing list tidak sinkron: kolom tercentang [{Join(ticked)}], revisi di sheet [{Join(dates)}].",
                Advice = "Tab Issue / Revisi: klik sel STATUS sheet ini, lalu pilih mana yang benar (revisi atau centang).",
                Fix = FixKind.SyncSheetList,
                FixValue = dates.Count == 0 ? "Lepas semua centang kolom" : "Centang kolom: " + Join(dates),
            };
        }
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values.OrderBy(v => v, StringComparer.Ordinal));
}

/// <summary>Revisi terakhir harus punya revision cloud sendiri; cloud lama yang belum dipindah berarti lupa diganti revisinya.</summary>
public sealed class RevisionCloudRule : IQaRule
{
    public string Id => "REV-CLOUD";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            if (s.Revisions.Count < 2) continue; // first issue tidak butuh cloud
            var current = s.Revisions[^1];
            var stale = s.Revisions.Take(s.Revisions.Count - 1).Where(r => r.Clouds > 0).ToList();
            if (current.Clouds > 0)
            {
                if (stale.Count > 0)
                    yield return new QaIssue
                    {
                        RuleId = Id, Severity = Severity.Info, SheetNumber = s.Number, UniqueId = s.UniqueId,
                        Message = $"Masih ada revision cloud milik revisi lama di sheet revisi {current.Number}: {Where(stale)}.",
                        ElementIds = stale.SelectMany(r => r.CloudIds).ToList(),
                        Advice = "Go To Sheet membuka tempat cloud itu dan memilihnya. Hapus atau sembunyikan cloud revisi lama, atau ganti parameter Revision-nya bila itu perubahan baru.",
                    };
                continue;
            }
            yield return stale.Count > 0
                ? new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Cloud tidak sinkron: sheet sudah revisi {current.Number} ({current.Date}) tetapi revision cloud masih milik revisi lama: {Where(stale)}.",
                    ElementIds = stale.SelectMany(r => r.CloudIds).ToList(),
                    Advice = $"Pilih cloud di sheet/view, ganti parameter Revision ke Seq {current.Sequence}, atau gambar cloud baru untuk perubahan revisi {current.Number}.",
                    Fix = FixKind.MoveClouds,
                    FixValue = $"Pindahkan cloud ke revisi {current.Number}",
                }
                : new QaIssue
                {
                    RuleId = Id, Severity = Severity.Info, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Revisi {current.Number} ({current.Date}) tidak punya revision cloud: perubahannya tidak ditandai di gambar.",
                    Advice = "Gambar revision cloud di bagian yang berubah. Abaikan bila sheet ini memang tanpa cloud (cover, drawing list).",
                };
        }
    }

    /// <summary>"1 cloud revisi A di view 'X'": supaya cloud-nya bisa ditemukan.</summary>
    private static string Where(IEnumerable<SheetRevision> revisions) => string.Join("; ", revisions.Select(r =>
        $"{r.Clouds} cloud revisi {r.Number}" + (r.CloudPlaces.Count > 0 ? " " + string.Join(" dan ", r.CloudPlaces) : "")));
}

/// <summary>Isian revisi di kop (SUB_REV, REV_NOTE, APVD) diketik manual, jadi harus dicek terhadap revisi di sheet.</summary>
public sealed class TitleBlockRevisionRule : IQaRule
{
    public string Id => "REV-KOP";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        foreach (var s in sheets)
        {
            var problems = new List<string>();
            if (s.Revisions.Count == 0)
            {
                // Sisa isian dari revisi yang sudah dilepas atau dihapus.
                if (s.SubRevs?.FirstOrDefault(v => v.Length > 0) is { } left) problems.Add($"SUB_REV '{left}' terisi padahal sheet tidak punya revisi");
                if (s.Approvers is { } rows && rows.Any(v => v.Length > 0)) problems.Add("APVD terisi padahal sheet tidak punya revisi");
                if (problems.Count > 0) yield return Problem(s, problems);
                continue;
            }
            var current = s.Revisions[^1];

            if (s.SubRevs is { } subs)
            {
                var wrong = subs.Where(v => v.Length > 0 && v != current.Number).Distinct().ToList();
                if (wrong.Count > 0) problems.Add($"SUB_REV '{string.Join("/", wrong)}' seharusnya '{current.Number}'");
                else if (subs.All(v => v.Length == 0)) problems.Add($"SUB_REV kosong, seharusnya '{current.Number}'");
            }
            if (s.RevNote is { } note)
            {
                if (s.Revisions.Count >= 2 && note.Length == 0) problems.Add($"REV_NOTE kosong untuk revisi {current.Number}");
                if (s.Revisions.Count == 1 && note.Length > 0) problems.Add($"REV_NOTE '{note}' terisi padahal baru first issue");
            }
            if (s.Approvers is { } apv)
            {
                var empty = Enumerable.Range(0, Math.Min(s.Revisions.Count, apv.Count)).Where(i => apv[i].Length == 0)
                    .Select(i => s.Revisions[i].Number).ToList();
                if (empty.Count > 0) problems.Add($"APVD kosong di baris revisi {string.Join(", ", empty)}");
                if (apv.Skip(s.Revisions.Count).Any(v => v.Length > 0)) problems.Add("APVD terisi di baris yang tidak punya revisi");
            }

            if (problems.Count > 0) yield return Problem(s, problems);
        }
    }

    private QaIssue Problem(SheetInfo s, List<string> problems) => new()
    {
        RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
        Message = "Kop tidak sinkron dengan revisi: " + string.Join("; ", problems) + ".",
        Advice = "Tab Issue / Revisi: centang sheet, pilih revisi terakhirnya, isi Note dan APVD, lalu Terapkan Issue.",
        // SUB_REV dan APVD bisa diturunkan dari revisi di sheet; isi REV_NOTE harus diketik orang.
        Fix = problems.Any(p => p.StartsWith("SUB_REV") || p.StartsWith("APVD")) ? FixKind.SyncTitleBlock : FixKind.None,
        FixValue = "SUB_REV dan APVD mengikuti revisi di sheet",
    };
}

/// <summary>Tanggal revisi di satu sheet harus maju; sheet yang belum punya revisi berarti lupa di-issue.</summary>
public sealed class RevisionDateRule : IQaRule
{
    public string Id => "REV-DATE";
    private static readonly string[] Formats = { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "yyyy.MM.dd" };

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var coded = sheets.Where(s => SheetCode.TryParse(s.Number) is not null).ToList();
        var mostIssued = coded.Count(s => s.Revisions.Count > 0) * 2 > coded.Count;

        foreach (var s in sheets)
        {
            for (var i = 1; i < s.Revisions.Count; i++)
            {
                var (a, b) = (s.Revisions[i - 1], s.Revisions[i]);
                if (!TryDate(a.Date, out var da) || !TryDate(b.Date, out var db) || db >= da) continue;
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Tanggal revisi mundur: revisi {b.Number} ({b.Date}) lebih awal dari revisi {a.Number} ({a.Date}).",
                    Advice = "Periksa urutan di Sheet Issues/Revisions (Seq harus urut tanggal), atau lepas revisi yang salah tempel di tab Issue / Revisi.",
                };
            }
            if (mostIssued && s.Revisions.Count == 0 && coded.Contains(s))
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Sheet belum punya revisi/issue sama sekali, padahal sheet lain sudah di-issue.",
                    Advice = "Tab Issue / Revisi: centang sheet ini dan Terapkan Issue dengan revisi first issue-nya.",
                };
        }
    }

    private static bool TryDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>Sheet bernomor valid yang tidak akan muncul di drawing list, tanpa kop, atau kopnya bertumpuk.</summary>
public sealed class SheetSetupRule : IQaRule
{
    public string Id => "SHEET-SETUP";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var coded = sheets.Where(s => SheetCode.TryParse(s.Number) is not null).ToList();
        var usesDiscipline = coded.Any(s => s.Parameters.ContainsKey("ACT SHEET DISCIPLINE"));
        foreach (var s in coded)
        {
            var problems = new List<string>();
            if (s.Parameters.GetValueOrDefault("Appears In Sheet List") == "No") problems.Add("'Appears In Sheet List' tidak dicentang");
            if (s.Parameters.GetValueOrDefault("ACT SHEET IN SCHEDULE") == "No") problems.Add("'ACT SHEET IN SCHEDULE' tidak dicentang");
            if (usesDiscipline && !s.Parameters.ContainsKey("ACT SHEET DISCIPLINE")) problems.Add("'ACT SHEET DISCIPLINE' kosong");
            if (usesDiscipline && !s.Parameters.ContainsKey("ACT SHEET SERIES")) problems.Add("'ACT SHEET SERIES' kosong");
            if (problems.Count > 0)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = "Sheet tidak akan muncul (atau salah kelompok) di drawing list: " + string.Join("; ", problems) + ".",
                    Advice = "Isi parameter sheet itu di Properties, samakan dengan sheet lain di seri yang sama.",
                };

            if (s.TitleBlockCount is { } count && count != 1 && !s.IsPlaceholder)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = count == 0 ? "Sheet tidak punya kop (title block)." : $"Ada {count} kop bertumpuk di sheet ini.",
                    Advice = count == 0 ? "Tab Kop / Title Block: centang sheet ini lalu Ganti Kop." : "Hapus kop yang berlebih di sheet.",
                };
        }
    }
}

/// <summary>Dua sheet dengan judul persis sama biasanya hasil duplikat sheet yang judulnya lupa diganti.</summary>
public sealed class DuplicateTitleRule : IQaRule
{
    public string Id => "DUP-TITLE";

    public IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var groups = sheets.Where(s => SheetCode.TryParse(s.Number) is not null && s.Name.Trim().Length > 0)
            .GroupBy(s => TitleText.NormalizeWhitespace(s.Name).ToUpperInvariant()).Where(g => g.Count() > 1);
        foreach (var g in groups)
            foreach (var s in g)
                yield return new QaIssue
                {
                    RuleId = Id, Severity = Severity.Warning, SheetNumber = s.Number, UniqueId = s.UniqueId,
                    Message = $"Judul sama persis dengan: {string.Join(", ", g.Where(o => o != s).Select(o => o.Number))}.",
                    Advice = "Sheet hasil duplikat biasanya lupa diganti judulnya. Bedakan judulnya (lantai, area, nomor #).",
                };
    }
}
