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
        foreach (var cloud in new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>())
        {
            if (doc.GetElement(cloud.RevisionId) is not Revision rev || rev.Issued) continue;
            var owner = cloud.OwnerViewId;
            if (doc.GetElement(owner) is ViewSheet) sheetsWithUnissued.Add(owner);
            else if (viewToSheet.TryGetValue(owner, out var sheetId)) sheetsWithUnissued.Add(sheetId);
        }

        var scheduleCount = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>().Where(s => !s.IsTitleblockRevisionSchedule)
            .GroupBy(s => s.OwnerViewId).ToDictionary(g => g.Key, g => g.Count());

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
}
