namespace QARevit.Core.Model;

public enum Severity { Info, Warning, Error }

public enum FixKind
{
    None, RenameSheet, RenumberSheet, CreateSheet,
    /// <summary>Centang kolom sheet list mengikuti revisi di sheet.</summary>
    SyncSheetList,
    /// <summary>SUB_REV dan APVD di kop mengikuti revisi di sheet.</summary>
    SyncTitleBlock,
    /// <summary>Pindahkan revision cloud dari revisi lama ke revisi terakhir sheet.</summary>
    MoveClouds,
}

/// <summary>Satu temuan QA, lengkap dengan saran (advice) dan perbaikan otomatis bila aman.</summary>
public sealed class QaIssue
{
    public string RuleId { get; init; } = "";
    public Severity Severity { get; init; }
    public string SheetNumber { get; init; } = "";
    public string? UniqueId { get; init; }
    public string Message { get; init; } = "";
    public string Advice { get; init; } = "";
    public FixKind Fix { get; init; }
    /// <summary>Nilai baru untuk perbaikan (nama atau nomor baru).</summary>
    public string? FixValue { get; init; }

    public bool CanFix => Fix != FixKind.None && !string.IsNullOrEmpty(FixValue);
}

public sealed class QaReport
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<SheetInfo> Sheets { get; init; } = Array.Empty<SheetInfo>();
    public IReadOnlyList<QaIssue> Issues { get; init; } = Array.Empty<QaIssue>();

    public int Total => Sheets.Count;
    public int Failed => Sheets.Count(s => Issues.Any(i => i.Severity >= Severity.Warning && i.UniqueId == s.UniqueId));
    public int Passed => Total - Failed;
    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;
}
