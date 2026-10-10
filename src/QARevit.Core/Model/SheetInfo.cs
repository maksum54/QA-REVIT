namespace QARevit.Core.Model;

/// <summary>Snapshot satu sheet Revit (diisi oleh add-in, dipakai oleh rule engine).</summary>
public sealed class SheetInfo
{
    public string UniqueId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPlaceholder { get; set; }
    /// <summary>Jumlah viewport + schedule yang ditaruh di sheet.</summary>
    public int PlacedViewCount { get; set; }
    public string? CurrentRevision { get; set; }
    public string? CurrentRevisionDate { get; set; }
    public bool HasUnissuedRevisionClouds { get; set; }
    public List<string> ViewScales { get; set; } = new();
    /// <summary>Kop (title block) di sheet, format "Family : Type". Kosong = tidak ada kop.</summary>
    public string TitleBlock { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RegisterEntry
{
    public string? UniqueId { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Revision { get; set; }
    public string? Scale { get; set; }
    /// <summary>Kop yang diminta untuk sheet ini (kolom opsional KOP / TITLE BLOCK di Excel).</summary>
    public string? TitleBlock { get; set; }
    public int SourceRow { get; set; }
}
