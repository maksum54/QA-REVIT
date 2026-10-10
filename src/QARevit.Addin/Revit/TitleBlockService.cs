using Autodesk.Revit.DB;

namespace QARevit.Addin.Revit;

/// <summary>Satu type kop (title block) di model.</summary>
public sealed record TitleBlockOption(long Id, string Family, string Type)
{
    public string Label => $"{Family} : {Type}";
}

public static class TitleBlockService
{
    public static string Label(FamilySymbol s) => $"{s.FamilyName} : {s.Name}";

    public static List<TitleBlockOption> GetTypes(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType().OfType<FamilySymbol>()
            .Select(s => new TitleBlockOption(s.Id.Value, s.FamilyName, s.Name))
            .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Ganti kop di sheet-sheet terpilih. Kop lama diganti type-nya (posisi dan isi parameter instance tetap);
    /// sheet tanpa kop diberi kop baru di titik 0,0.
    /// </summary>
    public static ApplyResult Change(Document doc, IEnumerable<string> sheetUniqueIds, long titleBlockTypeId)
    {
        var result = new ApplyResult();
        if (doc.GetElement(new ElementId(titleBlockTypeId)) is not FamilySymbol symbol)
        {
            result.Errors.Add("Type kop tidak ditemukan.");
            return result;
        }

        var bySheet = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().OfType<FamilyInstance>()
            .GroupBy(f => f.OwnerViewId).ToDictionary(g => g.Key, g => g.ToList());

        using var tx = new Transaction(doc, "QA Revit - Ganti Kop");
        tx.Start();
        if (!symbol.IsActive) symbol.Activate();

        foreach (var id in sheetUniqueIds)
        {
            if (doc.GetElement(id) is not ViewSheet sheet) { result.Errors.Add($"{id}: sheet tidak ditemukan."); continue; }
            try
            {
                if (bySheet.TryGetValue(sheet.Id, out var existing) && existing.Count > 0)
                {
                    foreach (var tb in existing.Where(tb => tb.GetTypeId() != symbol.Id))
                        tb.ChangeTypeId(symbol.Id);
                }
                else
                {
                    doc.Create.NewFamilyInstance(XYZ.Zero, symbol, sheet);
                }
                result.Applied++;
            }
            catch (Exception ex) { result.Errors.Add($"{sheet.SheetNumber}: {ex.Message}"); }
        }
        tx.Commit();
        return result;
    }
}
