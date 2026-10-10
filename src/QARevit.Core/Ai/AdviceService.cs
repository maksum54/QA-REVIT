using System.Text;
using QARevit.Core.Config;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Ai;

/// <summary>
/// Membangun konteks hasil QA untuk AI. Yang dikirim hanya nomor/judul sheet dan temuan, bukan file model.
/// </summary>
public sealed class AdviceService
{
    private readonly IAiClient _client;
    private readonly List<ChatMessage> _history = new();
    private string _context = "";

    public AdviceService(IAiClient client) => _client = client;

    public const string SystemPrompt =
        "Kamu QA/QC engineer senior untuk gambar MEP (elektrikal & elektronik) di Revit, menjawab untuk drafter/engineer " +
        "yang akan langsung mengerjakan perbaikannya. Jawab dalam Bahasa Indonesia yang lugas: kalimat pendek, langsung ke " +
        "tindakan, tanpa basa-basi pembuka atau penutup. Format: markdown sederhana saja (judul dengan ##, butir dengan -, " +
        "**tebal** untuk nomor sheet). Jangan pakai tabel. Sistem penomoran gambar proyek: " +
        "PROYEK-DWG-STAGE-ME-ZONA-SISTEM-NNNN, urutan stage CD (Concept Design) → BD (Basic Design) → " +
        "DD (Detail Design) → DC (Detail Construction). Tulis nomor sheet persis seperti di data, jangan mengarang sheet, " +
        "dan kalau data tidak cukup untuk menjawab, katakan apa yang kurang.";

    public const string AdvicePrompt =
        "\nTulis rencana perbaikan dengan struktur persis ini, lewati bagian yang kosong:\n" +
        "## Kesimpulan\n1-2 kalimat: set gambar ini siap di-issue atau belum, dan apa penghalang utamanya.\n" +
        "## Kerjakan dulu\nMaksimal 5 butir, hanya hal yang menghalangi issue. Bentuk tiap butir: **nomor sheet** - masalahnya, lalu tindakannya.\n" +
        "## Bisa diperbaiki massal\nHal yang selesai sekali klik (yang bertanda 'ada Fix otomatis') atau sekali kerja untuk banyak sheet.\n" +
        "## Perlu dikonfirmasi\nHal yang butuh keputusan tim sebelum diubah.\n" +
        "Gabungkan sheet yang masalahnya sama dalam satu butir. Maksimal sekitar 200 kata.";

    public static string BuildContext(QaReport report, QaConfig cfg, IReadOnlyList<SheetChange>? diff, int maxIssues = 150)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ringkasan QA: total {report.Total} sheet, lulus {report.Passed}, gagal {report.Failed} ({report.PassRate:P0}).");
        var stages = report.Sheets.Select(s => SheetCode.TryParse(s.Number)?.Stage).Where(s => s is not null)
            .GroupBy(s => s).Select(g => $"{g.Key} ({cfg.StageName(g.Key)}): {g.Count()}");
        sb.AppendLine("Stage di model: " + string.Join(", ", stages));
        sb.AppendLine("Kode sistem: " + string.Join(", ", cfg.Systems.Select(kv => $"{kv.Key}={kv.Value.Name}")));
        sb.AppendLine();
        sb.AppendLine("Temuan (severity | rule | sheet | pesan):");
        foreach (var i in report.Issues.Take(maxIssues))
            sb.AppendLine($"- {i.Severity} | {i.RuleId} | {i.SheetNumber} | {i.Message}" + (i.CanFix ? " | ada Fix otomatis" : ""));
        if (report.Issues.Count > maxIssues) sb.AppendLine($"... dan {report.Issues.Count - maxIssues} temuan lain.");
        if (diff is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Perbedaan register Excel vs model:");
            foreach (var c in diff.Take(80))
                sb.AppendLine($"- {c.Kind} | {c.Number} | {c.NewNumber ?? ""} | {c.NewName ?? c.Name} | {c.Message}");
        }
        return sb.ToString();
    }

    /// <summary>Set konteks QA terbaru tanpa memanggil AI (dipakai sebelum chat).</summary>
    public void SetContext(QaReport report, QaConfig cfg, IReadOnlyList<SheetChange>? diff)
    {
        _context = BuildContext(report, cfg, diff);
        _history.Clear();
    }

    public async Task<string> GenerateAdviceAsync(QaReport report, QaConfig cfg, IReadOnlyList<SheetChange>? diff, CancellationToken ct = default)
    {
        _context = BuildContext(report, cfg, diff);
        _history.Clear();
        return await AskAsync(_context + AdvicePrompt, ct).ConfigureAwait(false);
    }

    /// <summary>Tanya lanjutan (chat) dengan konteks QA terakhir.</summary>
    public Task<string> ChatAsync(string question, CancellationToken ct = default)
    {
        var text = _history.Count == 0 && _context.Length > 0 ? _context + "\nPertanyaan: " + question : question;
        return AskAsync(text, ct);
    }

    private async Task<string> AskAsync(string text, CancellationToken ct)
    {
        _history.Add(new ChatMessage("user", text));
        try
        {
            var answer = await _client.CompleteAsync(SystemPrompt, _history, ct).ConfigureAwait(false);
            _history.Add(new ChatMessage("assistant", answer));
            return answer;
        }
        catch
        {
            _history.RemoveAt(_history.Count - 1);
            throw;
        }
    }
}

/// <summary>Advice berbasis aturan (offline, tanpa API key).</summary>
/// <summary>Temuan satu aturan; daftar grup diurutkan dari yang paling berat.</summary>
public sealed record RuleAdviceGroup(string RuleId, string Title, Severity Severity, int Fixable, string Advice, IReadOnlyList<QaIssue> Issues);

public static class RuleAdvice
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["CODE-FORMAT"] = "Format nomor sheet salah",
        ["DUPLICATE"] = "Nomor sheet ganda",
        ["CONSISTENCY"] = "Kode proyek / stage tidak seragam",
        ["SYSTEM-TITLE"] = "Kode sistem tidak cocok dengan judul",
        ["DRAWING-TYPE"] = "Seri nomor tidak cocok dengan judul",
        ["TITLE-TEXT"] = "Spasi ganda atau typo di judul",
        ["EMPTY-SHEET"] = "Sheet masih kosong",
        ["DC-READY"] = "Belum lengkap untuk issue DC",
        ["ZONE-COMPLETE"] = "Zona belum lengkap",
        ["REV-SHEETLIST"] = "Drawing list tidak sinkron dengan revisi",
        ["REV-CLOUD"] = "Revision cloud tidak sinkron dengan revisi",
        ["REV-KOP"] = "Isian revisi di kop tidak sinkron",
        ["REV-DATE"] = "Tanggal revisi mundur atau sheet belum di-issue",
        ["SHEET-SETUP"] = "Sheet tidak masuk drawing list atau kop bermasalah",
        ["DUP-TITLE"] = "Judul sheet kembar",
    };

    public static IReadOnlyList<RuleAdviceGroup> Groups(QaReport report) =>
        report.Issues.GroupBy(i => i.RuleId)
            .OrderByDescending(g => g.Max(i => i.Severity)).ThenByDescending(g => g.Count())
            .Select(g => new RuleAdviceGroup(g.Key, Titles.GetValueOrDefault(g.Key, g.Key), g.Max(i => i.Severity),
                g.Count(i => i.CanFix), g.First().Advice, g.ToList()))
            .ToList();

    public static string Build(QaReport report, IReadOnlyList<SheetChange>? diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HASIL QA: {report.Passed}/{report.Total} sheet lulus ({report.PassRate:P0}).");
        sb.AppendLine();
        var n = 1;
        foreach (var g in Groups(report))
        {
            sb.AppendLine($"{n++}. [{g.Severity}] {g.RuleId} — {g.Title}: {g.Issues.Count} temuan" + (g.Fixable > 0 ? $", {g.Fixable} bisa Fix otomatis" : ""));
            sb.AppendLine($"   Saran: {g.Advice}");
            sb.AppendLine($"   Contoh: {string.Join(", ", g.Issues.Take(5).Select(i => i.SheetNumber))}" + (g.Issues.Count > 5 ? " ..." : ""));
        }
        if (diff is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("REGISTER EXCEL vs MODEL:");
            foreach (var g in diff.GroupBy(c => c.Kind))
                sb.AppendLine($"- {g.Key}: {g.Count()}" + (g.Key == ChangeKind.StageMismatch ? $" — {g.First().Message}" : ""));
        }
        if (report.Issues.Count == 0 && (diff is null || diff.Count == 0))
            sb.AppendLine("Tidak ada temuan. Set gambar siap di-issue.");
        return sb.ToString();
    }
}
