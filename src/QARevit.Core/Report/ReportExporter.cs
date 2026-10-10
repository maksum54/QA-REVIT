using QARevit.Core.Excel;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Report;

/// <summary>Satu kolom revisi di drawing list Excel; judulnya diawali nomor urut supaya bisa dibaca kembali.</summary>
public sealed record RevisionColumn(int Sequence, string Date, string Description)
{
    public string Header => $"Seq {Sequence} · {Date} · {Description}";
}

public static class ReportExporter
{
    public static void ExportQa(string path, QaReport report, IReadOnlyList<SheetChange>? registerDiff = null, string? aiAdvice = null)
    {
        var summary = new List<IReadOnlyList<object?>>
        {
            new object?[] { "QA REVIT REPORT", report.CreatedAt.ToString("yyyy-MM-dd HH:mm") },
            new object?[] { "Total sheet", report.Total },
            new object?[] { "Passed", report.Passed },
            new object?[] { "Failed", report.Failed },
            new object?[] { "Pass rate (%)", Math.Round(report.PassRate * 100, 1) },
            new object?[] { "Error", report.Issues.Count(i => i.Severity == Severity.Error) },
            new object?[] { "Warning", report.Issues.Count(i => i.Severity == Severity.Warning) },
            new object?[] { "Info", report.Issues.Count(i => i.Severity == Severity.Info) },
        };

        var issues = new List<IReadOnlyList<object?>> { new object?[] { "SEVERITY", "RULE", "SHEET NUMBER", "MESSAGE", "ADVICE", "AUTO FIX" } };
        issues.AddRange(report.Issues.Select(i => (IReadOnlyList<object?>)new object?[]
            { i.Severity.ToString(), i.RuleId, i.SheetNumber, i.Message, i.Advice, i.CanFix ? i.FixValue : "" }));

        var sheets = new List<(string, IEnumerable<IReadOnlyList<object?>>)> { ("Summary", summary), ("Issues", issues) };

        if (registerDiff is { Count: > 0 })
        {
            var diff = new List<IReadOnlyList<object?>> { new object?[] { "KIND", "NUMBER", "NEW NUMBER", "NAME", "NEW NAME", "MESSAGE" } };
            diff.AddRange(registerDiff.Select(c => (IReadOnlyList<object?>)new object?[]
                { c.Kind.ToString(), c.Number, c.NewNumber, c.Name, c.NewName, c.Message }));
            sheets.Add(("Register Diff", diff));
        }
        if (!string.IsNullOrWhiteSpace(aiAdvice))
        {
            var lines = new List<IReadOnlyList<object?>> { new object?[] { "AI ADVICE" } };
            lines.AddRange(aiAdvice.Split('\n').Select(l => (IReadOnlyList<object?>)new object?[] { l.TrimEnd('\r') }));
            sheets.Add(("AI Advice", lines));
        }
        SimpleXlsx.Write(path, sheets);
    }

    /// <summary>
    /// Export drawing list dari model dengan format yang bisa di-import kembali. Dengan <paramref name="states"/> dan
    /// <paramref name="revisions"/>, ditambah kolom NOTE dan satu kolom per revisi (x = revisi ada di sheet) yang bisa diedit.
    /// </summary>
    public static void ExportRegister(string path, IReadOnlyList<SheetInfo> sheets, IReadOnlyList<SheetRevState>? states = null,
        IReadOnlyList<RevisionColumn>? revisions = null, QaReport? report = null)
    {
        var book = new List<(string, IEnumerable<IReadOnlyList<object?>>)> { ("Drawing List", DrawingListRows(sheets, states, revisions, report)) };
        if (report is not null)
        {
            var issues = new List<IReadOnlyList<object?>> { new object?[] { "SEVERITY", "RULE", "SHEET NUMBER", "MESSAGE", "ADVICE" } };
            issues.AddRange(report.Issues.Select(i => (IReadOnlyList<object?>)new object?[] { i.Severity.ToString(), i.RuleId, i.SheetNumber, i.Message, i.Advice }));
            book.Add(("Hasil QA", issues));
        }
        SimpleXlsx.Write(path, book);
    }

    public static List<IReadOnlyList<object?>> DrawingListRows(IReadOnlyList<SheetInfo> sheets, IReadOnlyList<SheetRevState>? states = null,
        IReadOnlyList<RevisionColumn>? revisions = null, QaReport? report = null)
    {
        var extras = states is not null && revisions is not null;
        var byId = (states ?? Array.Empty<SheetRevState>()).ToDictionary(s => s.UniqueId);
        var revs = revisions ?? Array.Empty<RevisionColumn>();

        var header = new List<object?> { "GUID", "DRAWING NUMBER", "NAMA DRAWING", "REV", "REV DATE" };
        if (extras) { header.Add("NOTE"); header.AddRange(revs.Select(r => r.Header)); }
        header.AddRange(new[] { "SCALE", "TITLE BLOCK" });
        if (report is not null) header.Add("HASIL QA");

        var rows = new List<IReadOnlyList<object?>> { header };
        foreach (var s in sheets.OrderBy(s => s.Number, StringComparer.Ordinal))
        {
            var row = new List<object?> { s.UniqueId, s.Number, s.Name, s.CurrentRevision, s.CurrentRevisionDate };
            if (extras)
            {
                var state = byId.GetValueOrDefault(s.UniqueId);
                row.Add(state?.Note ?? "");
                row.AddRange(revs.Select(r => state is not null && state.RevisionSeqs.Contains(r.Sequence) ? "x" : ""));
            }
            row.Add(string.Join(", ", s.ViewScales.Distinct()));
            row.Add(s.TitleBlock);
            if (report is not null)
            {
                var found = report.Issues.Where(i => i.UniqueId == s.UniqueId && i.Severity >= Severity.Warning).Select(i => i.RuleId).Distinct().ToList();
                row.Add(found.Count == 0 ? "OK" : string.Join(", ", found));
            }
            rows.Add(row);
        }
        return rows;
    }
}
