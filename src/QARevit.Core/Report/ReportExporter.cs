using QARevit.Core.Excel;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Report;

/// <summary>Satu kolom revisi di drawing list Excel; judulnya diawali nomor urut supaya bisa dibaca kembali.</summary>
public sealed record RevisionColumn(int Sequence, string Date, string Description)
{
    public string Header => $"Seq {Sequence}\n{Date}\n{Description}";
}

public static class ReportExporter
{
    private const string DisciplineParam = "ACT SHEET DISCIPLINE";
    private const string SeriesParam = "ACT SHEET SERIES";
    private const string SortParam = "ACT SHEET SORTING CODE";
    private const string Ungrouped = "LAINNYA";

    // Warna kolom revisi, berulang: merah, kuning, hijau, cyan, biru, jingga, ungu.
    private static readonly string[] RevisionFills = { "FFFF8080", "FFFFFF80", "FF90EE90", "FF80FFFF", "FF66A3FF", "FFFFC080", "FFD9B3FF" };

    public static void ExportQa(string path, QaReport report, IReadOnlyList<SheetChange>? registerDiff = null, string? aiAdvice = null) =>
        SimpleXlsx.Write(path, QaSheets(report, registerDiff, aiAdvice));

    private static List<(string, IEnumerable<IReadOnlyList<object?>>, SheetFormat?)> QaSheets(QaReport report,
        IReadOnlyList<SheetChange>? registerDiff, string? aiAdvice)
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

        var sheets = new List<(string, IEnumerable<IReadOnlyList<object?>>, SheetFormat?)>
        {
            ("Summary", summary, null),
            ("Issues", issues, new SheetFormat { AutoFilter = true, ColumnWidths = new double[] { 11, 16, 30, 60, 60, 30 } }),
        };

        if (registerDiff is { Count: > 0 })
        {
            var diff = new List<IReadOnlyList<object?>> { new object?[] { "KIND", "NUMBER", "NEW NUMBER", "NAME", "NEW NAME", "MESSAGE" } };
            diff.AddRange(registerDiff.Select(c => (IReadOnlyList<object?>)new object?[]
                { c.Kind.ToString(), c.Number, c.NewNumber, c.Name, c.NewName, c.Message }));
            sheets.Add(("Register Diff", diff, null));
        }
        if (!string.IsNullOrWhiteSpace(aiAdvice))
        {
            var lines = new List<IReadOnlyList<object?>> { new object?[] { "AI ADVICE" } };
            lines.AddRange(aiAdvice.Split('\n').Select(l => (IReadOnlyList<object?>)new object?[] { l.TrimEnd('\r') }));
            sheets.Add(("AI Advice", lines, null));
        }
        return sheets;
    }

    /// <summary>
    /// Export drawing list dari model sebagai sheet index yang bisa di-import kembali: kolom GUID tersembunyi, filter di header,
    /// sheet dikelompokkan per disiplin dan seri. Dengan <paramref name="states"/> dan <paramref name="revisions"/> ditambah kolom
    /// NOTE dan satu kolom per revisi (x = revisi ada di sheet). Dengan <paramref name="report"/> ditambah sheet hasil QA.
    /// </summary>
    public static void ExportRegister(string path, IReadOnlyList<SheetInfo> sheets, IReadOnlyList<SheetRevState>? states = null,
        IReadOnlyList<RevisionColumn>? revisions = null, QaReport? report = null, IReadOnlyList<SheetChange>? registerDiff = null,
        string? aiAdvice = null)
    {
        var (rows, format) = DrawingList(sheets, states, revisions, report);
        var book = new List<(string, IEnumerable<IReadOnlyList<object?>>, SheetFormat?)> { ("Drawing List", rows, format) };
        if (report is not null) book.AddRange(QaSheets(report, registerDiff, aiAdvice));
        SimpleXlsx.Write(path, book);
    }

    public static List<IReadOnlyList<object?>> DrawingListRows(IReadOnlyList<SheetInfo> sheets, IReadOnlyList<SheetRevState>? states = null,
        IReadOnlyList<RevisionColumn>? revisions = null, QaReport? report = null) => DrawingList(sheets, states, revisions, report).Rows;

    private static (List<IReadOnlyList<object?>> Rows, SheetFormat Format) DrawingList(IReadOnlyList<SheetInfo> sheets,
        IReadOnlyList<SheetRevState>? states, IReadOnlyList<RevisionColumn>? revisions, QaReport? report)
    {
        var extras = states is not null && revisions is not null;
        var byId = (states ?? Array.Empty<SheetRevState>()).ToDictionary(s => s.UniqueId);
        var revs = revisions ?? Array.Empty<RevisionColumn>();

        // Kolom A (GUID) disembunyikan: dipakai mengenali sheet saat di-import walau nomornya diganti.
        var header = new List<object?> { "GUID", "CURRENT REV", "DRAWING NO.", "SHEET NAME" };
        var widths = new List<double> { 4, 13, 30, 62 };
        var centered = new HashSet<int> { 2 };
        var fills = new Dictionary<int, string>();
        if (extras)
        {
            header.Add("NOTE");
            widths.Add(30);
            foreach (var r in revs)
            {
                header.Add(r.Header);
                widths.Add(12);
                centered.Add(header.Count);
                fills[header.Count] = RevisionFills[fills.Count % RevisionFills.Length];
            }
        }
        if (report is not null) { header.Add("HASIL QA"); widths.Add(26); }

        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { null, "SHEET INDEX" },
            new object?[] { null, extras
                ? "Yang bisa diedit lalu dilampirkan kembali di QA Revit: nomor, judul, NOTE, dan tanda x di kolom revisi."
                : "Yang bisa diedit lalu di-import kembali di QA Revit: nomor dan judul." },
            header,
        };
        var groupRows = new HashSet<int>();

        string Param(SheetInfo s, string name) => s.Parameters.GetValueOrDefault(name)?.Trim() ?? "";
        var grouped = sheets.Any(s => Param(s, DisciplineParam).Length > 0);
        var ordered = sheets
            .OrderBy(s => grouped && Param(s, DisciplineParam).Length == 0)
            .ThenBy(s => Param(s, DisciplineParam), StringComparer.Ordinal)
            .ThenBy(s => Param(s, SeriesParam), StringComparer.Ordinal)
            .ThenBy(s => int.TryParse(Param(s, SortParam), out var code) ? code : int.MaxValue)
            .ThenBy(s => s.Number, StringComparer.Ordinal);

        string? discipline = null, series = null;
        foreach (var s in ordered)
        {
            if (grouped)
            {
                var d = Param(s, DisciplineParam) is { Length: > 0 } x ? x : Ungrouped;
                if (d != discipline)
                {
                    rows.Add(new object?[] { null, d });
                    groupRows.Add(rows.Count);
                    (discipline, series) = (d, null);
                }
                var sr = Param(s, SeriesParam);
                if (sr != series && sr.Length > 0)
                {
                    rows.Add(new object?[] { null, sr });
                    groupRows.Add(rows.Count);
                }
                series = sr;
            }

            var row = new List<object?> { s.UniqueId, s.CurrentRevision, s.Number, s.Name };
            if (extras)
            {
                var state = byId.GetValueOrDefault(s.UniqueId);
                row.Add(state?.Note ?? "");
                row.AddRange(revs.Select(r => state is not null && state.RevisionSeqs.Contains(r.Sequence) ? "x" : null));
            }
            if (report is not null)
            {
                var found = report.Issues.Where(i => i.UniqueId == s.UniqueId && i.Severity >= Severity.Warning).Select(i => i.RuleId).Distinct().ToList();
                row.Add(found.Count == 0 ? "OK" : string.Join(", ", found));
            }
            rows.Add(row);
        }

        return (rows, new SheetFormat
        {
            HeaderRow = 3, AutoFilter = true, ColumnWidths = widths, HiddenColumns = new HashSet<int> { 1 }, CenteredColumns = centered,
            TitleRows = new HashSet<int> { 1 }, TitleColumn = 2, GroupRows = groupRows, ColumnFills = fills,
        });
    }
}
