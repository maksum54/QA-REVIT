using System.Text;
using System.Text.Json;
using QARevit.Core.Config;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Ai;

/// <summary>Satu usulan AI untuk temuan yang tidak punya perbaikan otomatis.</summary>
public sealed record AiFix(string Sheet, string Action, string Value, string Reason);

/// <summary>
/// AI hanya mengusulkan; yang diterapkan ke model adalah usulan yang lolos validasi di sini dan dicentang pengguna.
/// Aksi yang boleh: isi REV_NOTE, ganti judul, ganti nomor, tempel revisi. Selain itu ditandai "manual".
/// </summary>
public static class AiFixPlanner
{
    public const string SystemPrompt =
        "Kamu QA/QC engineer senior untuk gambar MEP (elektrikal & elektronik) di Revit. Kamu mengusulkan perbaikan data sheet, " +
        "bukan mengobrol. Balas HANYA dengan JSON array yang valid, tanpa teks lain dan tanpa markdown.";

    private const int MaxIssues = 80;
    private const int MaxNoteLength = 60;

    /// <summary>Temuan yang dikirim ke AI: yang tidak bisa Fix otomatis dan tingkatnya Warning ke atas.</summary>
    public static List<QaIssue> Candidates(QaReport report) =>
        report.Issues.Where(i => !i.CanFix && i.Severity >= Severity.Warning && i.UniqueId is not null).Take(MaxIssues).ToList();

    public static string BuildPrompt(QaReport report)
    {
        var issues = Candidates(report);
        var ids = issues.Select(i => i.UniqueId).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine("Temuan QA yang belum bisa diperbaiki otomatis (rule | sheet | temuan):");
        foreach (var i in issues) sb.AppendLine($"- {i.RuleId} | {i.SheetNumber} | {i.Message}");

        sb.AppendLine();
        sb.AppendLine("Data sheet terkait (nomor | judul | revisi di sheet | REV_NOTE):");
        foreach (var s in report.Sheets.Where(s => ids.Contains(s.UniqueId)))
            sb.AppendLine($"- {s.Number} | {s.Name} | {Revisions(s)} | {s.RevNote ?? ""}");

        var revisions = report.Sheets.SelectMany(s => s.Revisions).GroupBy(r => r.Sequence).OrderBy(g => g.Key).Select(g => g.First()).ToList();
        if (revisions.Count > 0)
            sb.AppendLine("\nRevisi yang ada di proyek: " + string.Join("; ", revisions.Select(r => $"Seq {r.Sequence} = {r.Date} {r.Description}")));

        // Gaya note yang sudah dipakai, supaya usulan konsisten dengan sheet lain di sistem yang sama.
        var notes = report.Sheets.Where(s => !string.IsNullOrWhiteSpace(s.RevNote))
            .GroupBy(s => (System: SheetCode.TryParse(s.Number)?.System ?? "?", Note: s.RevNote!.Trim()))
            .Select(g => $"{g.Key.System}: \"{g.Key.Note}\" ({g.Count()} sheet)").ToList();
        if (notes.Count > 0) sb.AppendLine("\nREV_NOTE yang sudah dipakai per sistem: " + string.Join("; ", notes));

        sb.AppendLine("""

            Usulkan satu perbaikan per temuan, hanya dengan aksi ini:
            - "set_note": isi REV_NOTE revisi terakhir. value = teks pendek HURUF BESAR bergaya sama dengan note sheet lain di sistem itu.
            - "rename": value = judul sheet baru.
            - "renumber": value = nomor sheet baru sesuai pola PROYEK-DWG-STAGE-ME-ZONA-SISTEM-NNNN.
            - "apply_revision": value = nomor Seq revisi yang harus ditempel ke sheet (hanya Seq yang ada di proyek).
            - "manual": tidak bisa diperbaiki lewat data (mis. sheet kosong, harus menaruh view). value kosong; reason = langkah singkat untuk modeler.
            Bentuk jawaban: [{"sheet":"<nomor sheet persis seperti di data>","action":"...","value":"...","reason":"<alasan singkat>"}]
            Jangan mengusulkan sheet yang tidak ada di data. Kalau datanya tidak cukup untuk yakin, pakai "manual".
            """);
        return sb.ToString();
    }

    private static string Revisions(SheetInfo s) =>
        s.Revisions.Count == 0 ? "(belum ada)" : string.Join(", ", s.Revisions.Select(r => $"{r.Number}=Seq {r.Sequence} {r.Date} {r.Description}"));

    /// <summary>Ambil JSON array dari jawaban AI (model sering membungkusnya dengan ```json).</summary>
    public static List<AiFix> Parse(string answer)
    {
        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start) throw new FormatException("Jawaban AI tidak berisi daftar usulan (JSON array).");
        using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
        var list = new List<AiFix>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string Get(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim() : "";
            if (Get("sheet").Length > 0) list.Add(new AiFix(Get("sheet"), Get("action").ToLowerInvariant(), Get("value"), Get("reason")));
        }
        return list;
    }

    /// <summary>Ubah usulan AI menjadi baris perubahan. Usulan yang tidak valid dibuang dan dilaporkan di <paramref name="rejected"/>.</summary>
    public static List<SheetChange> ToChanges(IEnumerable<AiFix> fixes, IReadOnlyList<SheetInfo> sheets, QaConfig cfg, out List<string> rejected)
    {
        rejected = new List<string>();
        var byNumber = sheets.GroupBy(s => s.Number.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var knownSeqs = sheets.SelectMany(s => s.Revisions).Select(r => r.Sequence).ToHashSet();
        var changes = new List<SheetChange>();

        foreach (var f in fixes)
        {
            if (!byNumber.TryGetValue(f.Sheet, out var s)) { rejected.Add($"{f.Sheet}: sheet tidak ada di model."); continue; }
            var why = f.Reason.Length > 0 ? " " + f.Reason : "";
            SheetChange Row(ChangeKind kind, string message, string? number = null, string? name = null, ExtraChange? extra = null) => new()
            {
                Kind = kind, UniqueId = s.UniqueId, Number = s.Number, Name = s.Name, NewNumber = number, NewName = name, Extra = extra,
                Message = "Usulan AI: " + message + why, Selected = kind != ChangeKind.Manual,
            };

            switch (f.Action)
            {
                case "set_note" when f.Value.Length is > 0 and <= MaxNoteLength:
                    changes.Add(Row(ChangeKind.UpdateRevision, $"REV_NOTE = '{f.Value}'.",
                        extra: new ExtraChange(s.UniqueId, s.Number, f.Value, null, $"note '{s.RevNote}' → '{f.Value}'")));
                    break;
                case "rename" when f.Value.Length > 0 && f.Value != s.Name:
                    changes.Add(Row(ChangeKind.RenameSheet, "ganti judul.", name: f.Value));
                    break;
                case "renumber":
                    var code = SheetCode.TryParse(f.Value);
                    var problems = code?.Validate(cfg).ToList();
                    if (code is null || problems!.Count > 0) rejected.Add($"{s.Number}: nomor usulan '{f.Value}' tidak sesuai pola/kode proyek.");
                    else if (byNumber.ContainsKey(f.Value)) rejected.Add($"{s.Number}: nomor usulan '{f.Value}' sudah dipakai sheet lain.");
                    else changes.Add(Row(ChangeKind.RenumberSheet, "ganti nomor.", number: f.Value));
                    break;
                case "apply_revision":
                    var digits = new string(f.Value.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out var seq) || !knownSeqs.Contains(seq)) rejected.Add($"{s.Number}: revisi usulan '{f.Value}' tidak ada di proyek.");
                    else if (s.Revisions.Any(r => r.Sequence == seq)) rejected.Add($"{s.Number}: Seq {seq} sudah ada di sheet.");
                    else changes.Add(Row(ChangeKind.UpdateRevision, $"tempel revisi Seq {seq}.", extra: new ExtraChange(s.UniqueId, s.Number, null,
                            s.Revisions.Select(r => r.Sequence).Append(seq).ToList(), $"revisi +Seq {seq}")));
                    break;
                case "manual":
                    changes.Add(Row(ChangeKind.Manual, "kerjakan manual."));
                    break;
                default:
                    rejected.Add($"{s.Number}: usulan '{f.Action}' = '{f.Value}' tidak valid.");
                    break;
            }
        }
        return changes;
    }
}
