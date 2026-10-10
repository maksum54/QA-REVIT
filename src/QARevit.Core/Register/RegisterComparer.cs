using QARevit.Core.Config;
using QARevit.Core.Model;

namespace QARevit.Core.Register;

public enum ChangeKind { CreateSheet, RenameSheet, RenumberSheet, MissingInRegister, StageMismatch, UpdateRevision }

/// <summary>Satu baris perbedaan register vs model; yang Applicable bisa diterapkan ke Revit.</summary>
public sealed class SheetChange
{
    public ChangeKind Kind { get; init; }
    public string? UniqueId { get; init; }
    public string Number { get; init; } = "";
    public string? NewNumber { get; init; }
    public string Name { get; init; } = "";
    public string? NewName { get; init; }
    /// <summary>Kop untuk sheet baru (dari Excel). Kosong = pakai kop default yang dipilih di dashboard.</summary>
    public string? TitleBlock { get; init; }
    public string Message { get; init; } = "";
    public bool Selected { get; set; }
    /// <summary>Untuk <see cref="ChangeKind.UpdateRevision"/>: revisi dan NOTE yang diminta Excel.</summary>
    public ExtraChange? Extra { get; init; }

    public bool Applicable => Kind is ChangeKind.CreateSheet or ChangeKind.RenameSheet or ChangeKind.RenumberSheet or ChangeKind.UpdateRevision;
}

/// <summary>REV_NOTE dan revisi (nomor urut) yang sekarang ada di satu sheet.</summary>
public sealed record SheetRevState(string UniqueId, string Number, string Note, IReadOnlyCollection<int> RevisionSeqs);

/// <summary>Perubahan NOTE / revisi satu sheet supaya sama dengan Excel. Null = bagian itu tidak berubah.</summary>
public sealed record ExtraChange(string UniqueId, string Number, string? NewNote, IReadOnlyCollection<int>? NewRevisionSeqs, string Message);

public static class RegisterComparer
{
    /// <summary>Cocokkan per UniqueId dulu (deteksi nomor berubah), lalu per nomor sheet.</summary>
    public static List<SheetChange> Compare(IReadOnlyList<RegisterEntry> register, IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var changes = new List<SheetChange>();
        var byId = sheets.Where(s => s.UniqueId.Length > 0).ToDictionary(s => s.UniqueId, StringComparer.OrdinalIgnoreCase);
        var byNumber = sheets.GroupBy(s => Key(s.Number)).ToDictionary(g => g.Key, g => g.First());
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in register)
        {
            SheetInfo? sheet = null;
            if (e.UniqueId is not null) byId.TryGetValue(e.UniqueId, out sheet);
            sheet ??= byNumber.GetValueOrDefault(Key(e.Number));

            if (sheet is null || matched.Contains(sheet.UniqueId))
            {
                changes.Add(new SheetChange
                {
                    Kind = ChangeKind.CreateSheet, Number = e.Number, Name = e.Name, NewNumber = e.Number, NewName = e.Name,
                    TitleBlock = e.TitleBlock,
                    Message = $"Ada di register (baris {e.SourceRow}) tetapi belum ada di model.", Selected = true,
                });
                continue;
            }
            matched.Add(sheet.UniqueId);

            if (Key(sheet.Number) != Key(e.Number))
            {
                var stageMsg = StageNote(sheet.Number, e.Number, cfg);
                changes.Add(new SheetChange
                {
                    Kind = ChangeKind.RenumberSheet, UniqueId = sheet.UniqueId, Number = sheet.Number, NewNumber = e.Number.Trim(),
                    Name = sheet.Name, Message = "Nomor di register berbeda dengan model." + stageMsg, Selected = true,
                });
            }
            if (Normalize(sheet.Name) != Normalize(e.Name) && e.Name.Trim().Length > 0)
                changes.Add(new SheetChange
                {
                    Kind = ChangeKind.RenameSheet, UniqueId = sheet.UniqueId, Number = sheet.Number, Name = sheet.Name,
                    NewName = e.Name.Trim(), Message = "Judul di register berbeda dengan model.", Selected = true,
                });
        }

        foreach (var s in sheets.Where(s => !matched.Contains(s.UniqueId)))
            changes.Add(new SheetChange
            {
                Kind = ChangeKind.MissingInRegister, UniqueId = s.UniqueId, Number = s.Number, Name = s.Name,
                Message = "Ada di model tetapi tidak ada di register.",
            });

        // Stage register vs model secara keseluruhan.
        var regStage = Majority(register.Select(r => SheetCode.TryParse(r.Number)?.Stage));
        var modelStage = Majority(sheets.Select(s => SheetCode.TryParse(s.Number)?.Stage));
        if (regStage is not null && modelStage is not null && regStage != modelStage)
        {
            var behind = cfg.StageIndex(regStage) < cfg.StageIndex(modelStage);
            changes.Insert(0, new SheetChange
            {
                Kind = ChangeKind.StageMismatch, Number = "(semua)", Name = "",
                Message = behind
                    ? $"Register masih {regStage} ({cfg.StageName(regStage)}) sedangkan model sudah {modelStage} ({cfg.StageName(modelStage)}). Register perlu di-update."
                    : $"Register sudah {regStage} ({cfg.StageName(regStage)}) sedangkan model masih {modelStage} ({cfg.StageName(modelStage)}). Gunakan 'Ganti Stage' di model.",
            });
        }
        return changes;
    }

    /// <summary>Buat daftar renumber untuk mengganti stage seluruh sheet (mis. BD → DD).</summary>
    public static List<SheetChange> PlanStageChange(IReadOnlyList<SheetInfo> sheets, string newStage)
    {
        var list = new List<SheetChange>();
        foreach (var s in sheets)
        {
            var code = SheetCode.TryParse(s.Number);
            if (code is null || code.Stage == newStage) continue;
            list.Add(new SheetChange
            {
                Kind = ChangeKind.RenumberSheet, UniqueId = s.UniqueId, Number = s.Number, Name = s.Name,
                NewNumber = code.WithStage(newStage).Format(), Message = $"Ganti stage {code.Stage} → {newStage}.", Selected = true,
            });
        }
        return list;
    }

    private static string StageNote(string modelNumber, string registerNumber, QaConfig cfg)
    {
        var a = SheetCode.TryParse(modelNumber);
        var b = SheetCode.TryParse(registerNumber);
        if (a is null || b is null || a.Stage == b.Stage) return "";
        return cfg.StageIndex(b.Stage) < cfg.StageIndex(a.Stage)
            ? $" Perhatian: register stage {b.Stage} lebih awal dari model {a.Stage} (stage mundur)."
            : $" Stage naik {a.Stage} → {b.Stage}.";
    }

    private static string? Majority(IEnumerable<string?> values) =>
        values.Where(v => v is not null).GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    /// <summary>Beda NOTE dan revisi per sheet antara Excel dan model. Sheet dicocokkan per UniqueId, lalu per nomor.</summary>
    public static List<ExtraChange> PlanExtras(IReadOnlyList<RegisterEntry> register, IReadOnlyList<SheetRevState> sheets)
    {
        var byId = sheets.ToDictionary(s => s.UniqueId, StringComparer.OrdinalIgnoreCase);
        var byNumber = sheets.GroupBy(s => Key(s.Number)).ToDictionary(g => g.Key, g => g.First());
        var list = new List<ExtraChange>();
        foreach (var e in register)
        {
            SheetRevState? sheet = null;
            if (e.UniqueId is not null) byId.TryGetValue(e.UniqueId, out sheet);
            sheet ??= byNumber.GetValueOrDefault(Key(e.Number));
            if (sheet is null) continue;

            var note = e.Note is not null && e.Note != sheet.Note.Trim() ? e.Note : null;
            var seqs = e.RevisionSeqs is not null && !e.RevisionSeqs.SetEquals(sheet.RevisionSeqs) ? e.RevisionSeqs : null;
            if (note is null && seqs is null) continue;

            var parts = new List<string>();
            if (seqs is not null)
                parts.Add("revisi " + string.Join(" ", seqs.Except(sheet.RevisionSeqs).OrderBy(x => x).Select(x => $"+Seq {x}")
                    .Concat(sheet.RevisionSeqs.Except(seqs).OrderBy(x => x).Select(x => $"−Seq {x}"))));
            if (note is not null) parts.Add($"note '{sheet.Note}' → '{note}'");
            list.Add(new ExtraChange(sheet.UniqueId, sheet.Number, note, seqs, string.Join(", ", parts)));
        }
        return list;
    }

    private static string Key(string number) => number.Trim().ToUpperInvariant();
    private static string Normalize(string name) => Rules.TitleText.NormalizeWhitespace(name).ToUpperInvariant();
}
