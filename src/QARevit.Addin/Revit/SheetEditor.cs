using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using QARevit.Core.Model;
using QARevit.Core.Register;


namespace QARevit.Addin.Revit;

public sealed class ApplyResult
{
    public int Applied { get; set; }
    public List<string> Errors { get; } = new();
}

public static class SheetEditor
{
    public static void GoTo(UIApplication app, string uniqueId)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc.Document.GetElement(uniqueId) is ViewSheet sheet)
            uidoc.ActiveView = sheet;
    }

    /// <summary>Terapkan perbaikan otomatis dari temuan QA.</summary>
    public static ApplyResult ApplyFixes(Document doc, IEnumerable<QaIssue> issues)
    {
        var changes = issues.Where(i => i.CanFix && i.UniqueId is not null && i.Fix is FixKind.RenameSheet or FixKind.RenumberSheet).Select(i => new SheetChange
        {
            Kind = i.Fix == FixKind.RenameSheet ? ChangeKind.RenameSheet : ChangeKind.RenumberSheet,
            UniqueId = i.UniqueId,
            Number = i.SheetNumber,
            NewNumber = i.Fix == FixKind.RenumberSheet ? i.FixValue : null,
            NewName = i.Fix == FixKind.RenameSheet ? i.FixValue : null,
        });
        return Apply(doc, changes, "QA Revit - Fix");
    }

    /// <summary>Terapkan perubahan register (buat sheet, rename, renumber) dalam satu transaksi.</summary>
    /// <param name="defaultTitleBlockTypeId">Kop untuk sheet baru bila Excel tidak menyebut kop. Null = kop yang paling banyak dipakai.</param>
    public static ApplyResult Apply(Document doc, IEnumerable<SheetChange> changes, string transactionName = "QA Revit - Sync Register",
        long? defaultTitleBlockTypeId = null)
    {
        var result = new ApplyResult();
        var list = changes.Where(c => c.Applicable).ToList();
        if (list.Count == 0) return result;

        using var tx = new Transaction(doc, transactionName);
        tx.Start();

        // Renumber dua tahap (nomor sementara dulu) supaya tukar-nomor antar sheet tidak bentrok.
        var renumbers = list.Where(c => c.Kind == ChangeKind.RenumberSheet && !string.IsNullOrWhiteSpace(c.NewNumber))
            .Select(c => (Change: c, Sheet: doc.GetElement(c.UniqueId) as ViewSheet))
            .ToList();
        var temp = 0;
        foreach (var (change, sheet) in renumbers)
        {
            if (sheet is null) { result.Errors.Add($"{change.Number}: sheet tidak ditemukan."); continue; }
            try { sheet.SheetNumber = $"__QA_TMP_{temp++}_{Guid.NewGuid():N}"; }
            catch (Exception ex) { result.Errors.Add($"{change.Number}: {ex.Message}"); }
        }
        foreach (var (change, sheet) in renumbers)
        {
            if (sheet is null || !sheet.SheetNumber.StartsWith("__QA_TMP_")) continue;
            try { sheet.SheetNumber = change.NewNumber!; result.Applied++; }
            catch (Exception ex)
            {
                sheet.SheetNumber = change.Number; // kembalikan nomor lama
                result.Errors.Add($"{change.Number} → {change.NewNumber}: {ex.Message}");
            }
        }

        foreach (var c in list.Where(c => c.Kind == ChangeKind.RenameSheet && !string.IsNullOrWhiteSpace(c.NewName)))
        {
            if (doc.GetElement(c.UniqueId) is not ViewSheet sheet) { result.Errors.Add($"{c.Number}: sheet tidak ditemukan."); continue; }
            try { sheet.Name = c.NewName!; result.Applied++; }
            catch (Exception ex) { result.Errors.Add($"{c.Number}: {ex.Message}"); }
        }

        var creates = list.Where(c => c.Kind == ChangeKind.CreateSheet).ToList();
        if (creates.Count > 0)
        {
            var fallback = defaultTitleBlockTypeId is { } chosen ? new ElementId(chosen) : PreferredTitleBlock(doc);
            var types = TitleBlockService.GetTypes(doc);
            foreach (var c in creates)
            {
                try
                {
                    var requested = TitleBlockMatcher.Find(c.TitleBlock, types, t => t.Family, t => t.Type);
                    if (!string.IsNullOrWhiteSpace(c.TitleBlock) && requested is null)
                        result.Errors.Add($"{c.Number}: kop '{c.TitleBlock}' tidak ada di model, dipakai kop default.");
                    var titleBlock = requested is null ? fallback : new ElementId(requested.Id);
                    var sheet = titleBlock == ElementId.InvalidElementId
                        ? ViewSheet.CreatePlaceholder(doc)
                        : ViewSheet.Create(doc, titleBlock);
                    sheet.SheetNumber = c.NewNumber ?? c.Number;
                    sheet.Name = c.NewName ?? c.Name;
                    result.Applied++;
                }
                catch (Exception ex) { result.Errors.Add($"Buat {c.Number}: {ex.Message}"); }
            }
        }

        tx.Commit();
        return result;
    }

    /// <summary>Title block yang paling banyak dipakai di sheet yang sudah ada.</summary>
    private static ElementId PreferredTitleBlock(Document doc)
    {
        var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().Select(e => e.GetTypeId())
            .GroupBy(id => id).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return used ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType().FirstElementId();
    }
}
