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
        var clouds = new Dictionary<(ElementId Sheet, ElementId Revision), List<(long Id, string Place)>>();
        var cloudCategory = new ElementId(BuiltInCategory.OST_RevisionClouds);
        foreach (var cloud in new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>())
        {
            if (doc.GetElement(cloud.RevisionId) is not Revision rev || doc.GetElement(cloud.OwnerViewId) is not View view) continue;
            var sheetId = view is ViewSheet ? view.Id : viewToSheet.GetValueOrDefault(view.Id);
            if (sheetId is null) continue;
            if (!rev.Issued) sheetsWithUnissued.Add(sheetId);
            // Cloud yang tidak tercetak (disembunyikan di view, kategori mati, atau revisinya di-set Hidden) tidak dihitung.
            if (rev.Visibility == RevisionVisibility.Hidden || cloud.IsHidden(view) || view.GetCategoryHidden(cloudCategory)) continue;
            if (!clouds.TryGetValue((sheetId, rev.Id), out var list)) clouds[(sheetId, rev.Id)] = list = new();
            list.Add((cloud.Id.Value, view is ViewSheet ? "di sheet" : $"di view '{view.Name}'"));
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
                Revisions = IssueService.RevisionsOn(doc, sheet).Select(r =>
                {
                    var found = clouds.GetValueOrDefault((sheet.Id, r.Id)) ?? new();
                    return new SheetRevision(r.SequenceNumber, sheet.GetRevisionNumberOnSheet(r.Id) ?? "", r.RevisionDate ?? "",
                        r.Description ?? "", found.Count)
                    {
                        CloudIds = found.Select(c => c.Id).ToList(), CloudPlaces = found.Select(c => c.Place).Distinct().ToList(),
                    };
                }).ToList(),
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
            FindOutsideTitleBlock(doc, sheet, info);

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

    /// <summary>Catat view dan schedule yang kotaknya melewati batas luar kop (toleransi 1 mm).</summary>
    private static void FindOutsideTitleBlock(Document doc, ViewSheet sheet, SheetInfo info)
    {
        const double mm = 304.8, tolerance = 1.0;
        var frames = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().Select(tb => tb.get_BoundingBox(sheet)).Where(b => b is not null).ToList();
        if (frames.Count == 0) return;
        var (x0, y0, x1, y1) = (frames.Min(b => b.Min.X), frames.Min(b => b.Min.Y), frames.Max(b => b.Max.X), frames.Max(b => b.Max.Y));

        void Check(ElementId id, string label, XYZ min, XYZ max)
        {
            var sides = new[] { ("kiri", x0 - min.X), ("kanan", max.X - x1), ("bawah", y0 - min.Y), ("atas", max.Y - y1) }
                .Where(s => s.Item2 * mm > tolerance).Select(s => $"{Math.Round(s.Item2 * mm)} mm di {s.Item1}").ToList();
            if (sides.Count == 0) return;
            info.OutsideTitleBlock.Add($"{label} keluar {string.Join(", ", sides)}");
            info.OutsideTitleBlockIds.Add(id.Value);
        }

        foreach (var id in sheet.GetAllViewports())
            if (doc.GetElement(id) is Viewport vp)
            {
                var box = vp.GetBoxOutline();
                Check(id, $"view '{doc.GetElement(vp.ViewId)?.Name}'", box.MinimumPoint, box.MaximumPoint);
            }
        foreach (var schedule in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance))
                     .Cast<ScheduleSheetInstance>().Where(s => !s.IsTitleblockRevisionSchedule))
            if (schedule.get_BoundingBox(sheet) is { } box)
                Check(schedule.Id, $"schedule '{schedule.Name}'", box.Min, box.Max);
    }

    /// <summary>Isi semua parameter bernama sama; null bila parameternya tidak ada di sheet.</summary>
    private static List<string>? Texts(IList<Parameter> parameters) =>
        parameters.Count == 0 ? null : parameters.Select(p => p.AsString()?.Trim() ?? "").ToList();
}
