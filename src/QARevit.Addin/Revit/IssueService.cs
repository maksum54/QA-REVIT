using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace QARevit.Addin.Revit;

public sealed record RevisionOption(long Id, int Sequence, string Date, string Description)
{
    public string Label => $"Seq {Sequence} · {Date} · {Description}";
}

/// <summary>Satu kolom issue di sheet list: parameter Yes/No "*ACT ISSUE NN" dan judul kolomnya (tanggal).</summary>
public sealed record IssueColumn(string Number, string Heading, bool IsFree)
{
    public string ParamName => $"*ACT ISSUE {Number}";
    public string FieldName => $"ACT ISSUE {Number}";
    public string Label => IsFree ? $"{Number} · (kosong)" : $"{Number} · {Heading}";
}

/// <summary>Baris di tab Issue.</summary>
public sealed class IssueRow
{
    public bool Selected { get; set; }
    public string UniqueId { get; init; } = "";
    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public string Revisions { get; init; } = "";
    public string Issues { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed record IssueData(List<RevisionOption> Revisions, List<IssueColumn> Columns, List<IssueRow> Sheets,
    long ScheduleId, string ScheduleName, string Approver);

/// <summary>
/// Issue sheet sekali klik: revisi di sheet, centang kolom sheet list, SUB_REV / REV_NOTE dan "N APV" di kop.
/// </summary>
public static class IssueService
{
    private static readonly Regex IssueField = new(@"^ACT ISSUE (\d\d)$");
    private const int ApproverRows = 6;

    public static IssueData Collect(Document doc)
    {
        var revisions = Revision.GetAllRevisionIds(doc).Select(doc.GetElement).OfType<Revision>()
            .Select(r => new RevisionOption(r.Id.Value, r.SequenceNumber, r.RevisionDate ?? "", r.Description ?? ""))
            .ToList();
        var (schedule, columns) = FindColumns(doc, revisions.Select(r => r.Date).ToHashSet());

        var rows = new List<IssueRow>();
        var approvers = new List<string>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                     .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, StringComparer.Ordinal))
        {
            var onSheet = RevisionsOn(doc, sheet);
            var ticked = columns.Where(c => sheet.LookupParameter(c.ParamName)?.AsInteger() == 1).ToList();
            var synced = columns.Count == 0 ||
                         onSheet.Select(r => r.RevisionDate ?? "").ToHashSet().SetEquals(ticked.Select(c => c.Heading));
            rows.Add(new IssueRow
            {
                UniqueId = sheet.UniqueId,
                Number = sheet.SheetNumber,
                Name = sheet.Name,
                Revisions = string.Join(", ", onSheet.Select(r => $"{sheet.GetRevisionNumberOnSheet(r.Id)} {r.RevisionDate}")),
                Issues = string.Join(", ", ticked.Select(c => c.IsFree ? c.Number : c.Heading)),
                Status = synced ? "OK" : "⚠ centang ≠ revisi",
            });
            if (sheet.LookupParameter("1 APV")?.AsString() is { Length: > 0 } a) approvers.Add(a);
        }

        var approver = approvers.GroupBy(a => a).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        return new IssueData(revisions, columns, rows, schedule?.Id.Value ?? -1, schedule?.Name ?? "", approver);
    }

    /// <summary>Sheet list yang judul kolom issue-nya paling banyak cocok dengan tanggal revisi.</summary>
    private static (ViewSchedule?, List<IssueColumn>) FindColumns(Document doc, HashSet<string> revisionDates)
    {
        ViewSchedule? best = null;
        var bestColumns = new List<IssueColumn>();
        var bestScore = -1;
        foreach (var schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                     .Where(v => !v.IsTemplate && v.Definition.CategoryId == new ElementId(BuiltInCategory.OST_Sheets)))
        {
            var columns = new List<IssueColumn>();
            var def = schedule.Definition;
            for (var i = 0; i < def.GetFieldCount(); i++)
            {
                var field = def.GetField(i);
                var m = IssueField.Match(field.GetName());
                if (!m.Success || field.IsHidden) continue;
                var heading = (field.ColumnHeading ?? "").Trim();
                columns.Add(new IssueColumn(m.Groups[1].Value, heading, heading is "" or "-" || heading == field.GetName()));
            }
            var score = columns.Count(c => revisionDates.Contains(c.Heading));
            if (columns.Count == 0 || score <= bestScore) continue;
            (best, bestColumns, bestScore) = (schedule, columns, score);
        }
        return (best, bestColumns);
    }

    private static List<Revision> RevisionsOn(Document doc, ViewSheet sheet) =>
        sheet.GetAllRevisionIds().Select(doc.GetElement).OfType<Revision>().OrderBy(r => r.SequenceNumber).ToList();

    public static ApplyResult Apply(Document doc, IEnumerable<string> sheetUniqueIds, long revisionId, IssueColumn? column,
        long scheduleId, string note, string approver)
    {
        var result = new ApplyResult();
        if (doc.GetElement(new ElementId(revisionId)) is not Revision rev)
        {
            result.Errors.Add("Revisi tidak ditemukan.");
            return result;
        }

        using var tx = new Transaction(doc, "QA Revit - Issue Sheet");
        tx.Start();

        var sheets = new List<ViewSheet>();
        var added = new HashSet<ElementId>();
        foreach (var id in sheetUniqueIds)
        {
            if (doc.GetElement(id) is not ViewSheet sheet) { result.Errors.Add($"{id}: sheet tidak ditemukan."); continue; }
            try
            {
                if (!sheet.GetAllRevisionIds().Contains(rev.Id))
                {
                    var ids = sheet.GetAdditionalRevisionIds();
                    ids.Add(rev.Id);
                    sheet.SetAdditionalRevisionIds(ids);
                    added.Add(sheet.Id);
                }
                if (column is not null) sheet.LookupParameter(column.ParamName)?.Set(1);
                sheets.Add(sheet);
            }
            catch (Exception ex) { result.Errors.Add($"{sheet.SheetNumber}: {ex.Message}"); }
        }

        doc.Regenerate(); // nomor revisi per sheet (A, B, ...) baru tersedia setelah regenerate

        foreach (var sheet in sheets)
        {
            try
            {
                if (approver.Length > 0)
                {
                    var row = RevisionsOn(doc, sheet).FindIndex(r => r.Id == rev.Id) + 1;
                    if (row > ApproverRows || SetText(sheet, $"{row} APV", approver) == 0)
                        result.Errors.Add($"{sheet.SheetNumber}: parameter '{row} APV' tidak ada, APVD tidak diisi.");
                }
                // Baris atas kop menjelaskan revisi terakhir saja.
                if (sheet.GetCurrentRevision() == rev.Id)
                {
                    SetText(sheet, "SUB_REV", sheet.GetRevisionNumberOnSheet(rev.Id) ?? "");
                    if (note.Length > 0 || added.Contains(sheet.Id)) SetText(sheet, "REV_NOTE", note);
                }
                result.Applied++;
            }
            catch (Exception ex) { result.Errors.Add($"{sheet.SheetNumber}: {ex.Message}"); }
        }

        if (column is { IsFree: true } && doc.GetElement(new ElementId(scheduleId)) is ViewSchedule schedule)
        {
            var def = schedule.Definition;
            for (var i = 0; i < def.GetFieldCount(); i++)
                if (def.GetField(i).GetName() == column.FieldName)
                    def.GetField(i).ColumnHeading = rev.RevisionDate;
        }

        tx.Commit();
        return result;
    }

    /// <summary>
    /// Isi semua parameter teks bernama sama: model bisa punya dua shared parameter kembar (beda GUID),
    /// dan kop hanya membaca salah satunya.
    /// </summary>
    private static int SetText(ViewSheet sheet, string name, string value)
    {
        var targets = sheet.GetParameters(name).Where(p => !p.IsReadOnly && p.StorageType == StorageType.String).ToList();
        foreach (var p in targets) p.Set(value);
        return targets.Count;
    }
}
