using Autodesk.Revit.DB;
using QARevit.Core.Model;

namespace QARevit.Addin.Revit;

public static class SheetCollector
{
    public static List<SheetInfo> Collect(Document doc)
    {
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();

        // View → sheet (untuk revision cloud yang digambar di dalam view).
        var viewToSheet = new Dictionary<ElementId, ElementId>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            viewToSheet[vp.ViewId] = vp.SheetId;

        var sheetsWithUnissued = new HashSet<ElementId>();
        // Jumlah cloud per (sheet, revisi): cloud di sheet itu sendiri atau di view yang ditaruh di sheet.
        var clouds = new Dictionary<(ElementId Sheet, ElementId Revision), int>();
        foreach (var cloud in new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>())
        {
            if (doc.GetElement(cloud.RevisionId) is not Revision rev) continue;
            var owner = cloud.OwnerViewId;
            var sheetId = doc.GetElement(owner) is ViewSheet ? owner : viewToSheet.GetValueOrDefault(owner);
            if (sheetId is null) continue;
            clouds[(sheetId, rev.Id)] = clouds.GetValueOrDefault((sheetId, rev.Id)) + 1;
            if (!rev.Issued) sheetsWithUnissued.Add(sheetId);
        }

        var (_, issueColumns) = IssueService.FindColumns(doc,
            Revision.GetAllRevisionIds(doc).Select(doc.GetElement).OfType<Revision>().Select(r => r.RevisionDate ?? "").ToHashSet());
        var titleBlockCount = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().GroupBy(e => e.OwnerViewId).ToDictionary(g => g.Key, g => g.Count());

        var scheduleCount = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>().Where(s => !s.IsTitleblockRevisionSchedule)
            .GroupBy(s => s.OwnerViewId).ToDictionary(g => g.Key, g => g.Count());

        var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().OfType<FamilyInstance>()
            .GroupBy(f => f.OwnerViewId)
            .ToDictionary(g => g.Key, g => string.Join(" + ", g.Select(f => TitleBlockService.Label(f.Symbol)).Distinct()));

        var result = new List<SheetInfo>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var viewportIds = sheet.GetAllViewports();
            var info = new SheetInfo
            {
                UniqueId = sheet.UniqueId,
                Number = sheet.SheetNumber,
                Name = sheet.Name,
                IsPlaceholder = sheet.IsPlaceholder,
                PlacedViewCount = viewportIds.Count + scheduleCount.GetValueOrDefault(sheet.Id),
                CurrentRevision = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString(),
                CurrentRevisionDate = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_DATE)?.AsString(),
                HasUnissuedRevisionClouds = sheetsWithUnissued.Contains(sheet.Id),
                TitleBlock = titleBlocks.GetValueOrDefault(sheet.Id) ?? "",
                TitleBlockCount = titleBlockCount.GetValueOrDefault(sheet.Id),
                Revisions = IssueService.RevisionsOn(doc, sheet).Select(r => new SheetRevision(r.SequenceNumber,
                    sheet.GetRevisionNumberOnSheet(r.Id) ?? "", r.RevisionDate ?? "", r.Description ?? "",
                    clouds.GetValueOrDefault((sheet.Id, r.Id)))).ToList(),
                TickedIssueDates = issueColumns.Count == 0 ? null : issueColumns
                    .Where(c => sheet.LookupParameter(c.ParamName)?.AsInteger() == 1)
                    .Select(c => c.IsFree ? $"kolom {c.Number} tanpa tanggal" : c.Heading).ToList(),
                SubRevs = Texts(sheet.GetParameters("SUB_REV")),
                RevNote = Texts(sheet.GetParameters("REV_NOTE"))?.LastOrDefault(v => v.Length > 0) ?? (sheet.GetParameters("REV_NOTE").Count > 0 ? "" : null),
                Approvers = sheet.LookupParameter("1 APV") is null ? null
                    : Enumerable.Range(1, 6).Select(i => sheet.LookupParameter($"{i} APV")?.AsString()?.Trim() ?? "").ToList(),
            };
            foreach (var id in viewportIds)
                if (doc.GetElement(id) is Viewport vp && doc.GetElement(vp.ViewId) is View v && v.Scale > 0)
                    info.ViewScales.Add($"1 : {v.Scale}");

            foreach (Parameter p in sheet.Parameters)
            {
                var name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name) || info.Parameters.ContainsKey(name)) continue;
                var value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                if (!string.IsNullOrEmpty(value)) info.Parameters[name] = value;
            }
            result.Add(info);
        }
        return result.OrderBy(s => s.Number, StringComparer.Ordinal).ToList();
    }

    /// <summary>Isi semua parameter bernama sama; null bila parameternya tidak ada di sheet.</summary>
    private static List<string>? Texts(IList<Parameter> parameters) =>
        parameters.Count == 0 ? null : parameters.Select(p => p.AsString()?.Trim() ?? "").ToList();
}
