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

    // Data revisi untuk aturan REV-*. Null = model tidak punya parameter/kolom itu, aturannya dilewati.
    /// <summary>Revisi yang menempel di sheet, urut nomor urut (yang terakhir = revisi sekarang).</summary>
    public List<SheetRevision> Revisions { get; set; } = new();
    /// <summary>Judul kolom issue (tanggal) yang dicentang untuk sheet ini di sheet list.</summary>
    public List<string>? TickedIssueDates { get; set; }
    /// <summary>Isi semua parameter SUB_REV (bisa lebih dari satu parameter bernama sama).</summary>
    public List<string>? SubRevs { get; set; }
    public string? RevNote { get; set; }
    /// <summary>Isi "1 APV" .. "6 APV", urut baris revisi di kop.</summary>
    public List<string>? Approvers { get; set; }
    public int? TitleBlockCount { get; set; }
}

/// <summary>Satu revisi di sheet: nomor urut proyek, huruf di sheet, tanggal, dan jumlah revision cloud-nya di sheet + view di dalamnya.</summary>
public sealed record SheetRevision(int Sequence, string Number, string Date, string Description, int Clouds);

public sealed class RegisterEntry
{
    public string? UniqueId { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Revision { get; set; }
    public string? Scale { get; set; }
    /// <summary>Kop yang diminta untuk sheet ini (kolom opsional KOP / TITLE BLOCK di Excel).</summary>
    public string? TitleBlock { get; set; }
    /// <summary>REV_NOTE dari kolom NOTE. Null = Excel tidak punya kolom itu (tidak diubah).</summary>
    public string? Note { get; set; }
    /// <summary>Nomor urut revisi yang dicentang di kolom "Seq N ...". Null = Excel tidak punya kolom revisi.</summary>
    public HashSet<int>? RevisionSeqs { get; set; }
    public int SourceRow { get; set; }
}
