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
        "Kamu adalah QA/QC engineer senior untuk gambar MEP (elektrikal & elektronik) di Revit. " +
        "Jawab dalam Bahasa Indonesia, ringkas dan praktis. Gunakan sistem penomoran gambar proyek: " +
        "PROYEK-DWG-STAGE-ME-ZONA-SISTEM-NNNN, urutan stage CD (Concept Design) → BD (Basic Design) → " +
        "DD (Detail Design) → DC (Detail Construction). Jangan mengarang sheet yang tidak ada di data.";

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
            sb.AppendLine($"- {i.Severity} | {i.RuleId} | {i.SheetNumber} | {i.Message}");
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
        var prompt = _context + "\nBuat rencana perbaikan berprioritas: (1) masalah kritis yang harus selesai sebelum issue, " +
                     "(2) perbaikan cepat yang bisa massal, (3) risiko/hal yang perlu dikonfirmasi ke tim. Sebutkan nomor sheet.";
        return await AskAsync(prompt, ct).ConfigureAwait(false);
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
public static class RuleAdvice
{
    public static string Build(QaReport report, IReadOnlyList<SheetChange>? diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HASIL QA: {report.Passed}/{report.Total} sheet lulus ({report.PassRate:P0}).");
        sb.AppendLine();
        var groups = report.Issues.GroupBy(i => i.RuleId)
            .OrderByDescending(g => g.Max(i => i.Severity)).ThenByDescending(g => g.Count());
        var n = 1;
        foreach (var g in groups)
        {
            var worst = g.Max(i => i.Severity);
            var fixable = g.Count(i => i.CanFix);
            sb.AppendLine($"{n++}. [{worst}] {g.Key} — {g.Count()} temuan" + (fixable > 0 ? $", {fixable} bisa Fix otomatis" : ""));
            sb.AppendLine($"   Saran: {g.First().Advice}");
            sb.AppendLine($"   Contoh: {string.Join(", ", g.Take(5).Select(i => i.SheetNumber))}" + (g.Count() > 5 ? " ..." : ""));
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
