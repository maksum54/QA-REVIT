using QARevit.Core.Excel;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Report;

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

    /// <summary>Export register dari model dengan format yang bisa di-import kembali.</summary>
    public static void ExportRegister(string path, IReadOnlyList<SheetInfo> sheets)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "GUID", "DRAWING NUMBER", "NAMA DRAWING", "REV", "REV DATE", "SCALE" } };
        rows.AddRange(sheets.OrderBy(s => s.Number, StringComparer.Ordinal).Select(s => (IReadOnlyList<object?>)new object?[]
            { s.UniqueId, s.Number, s.Name, s.CurrentRevision, s.CurrentRevisionDate, string.Join(", ", s.ViewScales.Distinct()) }));
        SimpleXlsx.Write(path, new[] { ("Drawing List", (IEnumerable<IReadOnlyList<object?>>)rows) });
    }
}
