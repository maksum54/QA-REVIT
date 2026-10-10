using System.Text.Json;
using QARevit.Core.Excel;
using QARevit.Core.Model;

namespace QARevit.Core.Register;

public enum RegisterFormat { SheetLink, Manual }

public sealed class RegisterReadResult
{
    public RegisterFormat Format { get; init; }
    public List<RegisterEntry> Entries { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Baca drawing list Excel. Mendukung dua format:
/// 1) Export schedule Revit (gaya SheetLink): baris metadata JSON berisi "UniqueId"/"Name" per kolom, kolom GUID = UniqueId sheet.
/// 2) Manual: cari baris header yang berisi kolom nomor dan judul.
/// </summary>
public static class RegisterReader
{
    private static readonly string[] NumberHeaders = { "SHEET NUMBER", "DRAWING NUMBER", "DRAWING NO", "DWG NO", "NO. DRAWING", "NOMOR", "NUMBER" };
    private static readonly string[] NameHeaders = { "SHEET NAME", "DRAWING NAME", "NAMA DRAWING", "DRAWING TITLE", "JUDUL", "TITLE", "NAME", "NAMA" };
    private static readonly string[] RevHeaders = { "CURRENT REVISION", "CURRENT REV", "REVISION", "REV" };
    private static readonly string[] ScaleHeaders = { "SCALE", "SKALA" };
    private static readonly string[] TitleBlockHeaders = { "TITLE BLOCK", "TITLEBLOCK", "FAMILY KOP", "KOP" };
    private static readonly string[] GuidHeaders = { "GUID", "UNIQUEID", "UNIQUE ID", "ELEMENT ID" };
    private static readonly string[] NoteHeaders = { "REV NOTE", "REV_NOTE", "NOTE", "CATATAN" };
    private static readonly System.Text.RegularExpressions.Regex SeqHeader = new(@"^SEQ\s*(\d+)");
    private static readonly string[] Unticked = { "", "-", "0", "NO", "TIDAK", "FALSE" };

    public static RegisterReadResult Read(string path) => Read(SimpleXlsx.ReadFirstSheet(path));

    public static RegisterReadResult Read(List<string[]> rows)
    {
        var meta = FindSheetLinkMetaRow(rows);
        return meta is { } m ? ReadSheetLink(rows, m.Row, m.Columns) : ReadManual(rows);
    }

    private static (int Row, Dictionary<int, string> Columns)? FindSheetLinkMetaRow(List<string[]> rows)
    {
        for (var r = 0; r < Math.Min(rows.Count, 10); r++)
        {
            var cols = new Dictionary<int, string>();
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = rows[r][c].Trim();
                if (!cell.StartsWith('{') || !cell.Contains("\"UniqueId\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(cell);
                    if (doc.RootElement.TryGetProperty("Name", out var name))
                        cols[c] = name.GetString() ?? "";
                }
                catch (JsonException) { }
            }
            if (cols.Count >= 2) return (r, cols);
        }
        return null;
    }

    private static RegisterReadResult ReadSheetLink(List<string[]> rows, int metaRow, Dictionary<int, string> cols)
    {
        int? Find(params string[] names) => cols.Where(kv => names.Any(n => string.Equals(kv.Value, n, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => (int?)kv.Key).FirstOrDefault();

        var guid = Find("GUID", "UniqueId");
        var number = Find("Sheet Number");
        var name = Find("Sheet Name");
        var rev = Find("Current Revision");
        var kop = Find("Title Block", "Titleblock", "Kop");
        var result = new RegisterReadResult { Format = RegisterFormat.SheetLink };
        if (number is null)
        {
            result.Warnings.Add("Kolom 'Sheet Number' tidak ditemukan di metadata; dibaca sebagai format manual.");
            var manual = ReadManual(rows);
            manual.Warnings.InsertRange(0, result.Warnings);
            return manual;
        }

        for (var r = metaRow + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var num = Cell(row, number).Trim();
            if (num.Length == 0) continue;
            result.Entries.Add(new RegisterEntry
            {
                UniqueId = NullIfEmpty(Cell(row, guid)),
                Number = num,
                Name = Cell(row, name),
                Revision = NullIfEmpty(Cell(row, rev)),
                TitleBlock = NullIfEmpty(Cell(row, kop)),
                SourceRow = r + 1,
            });
        }
        return result;
    }

    private static RegisterReadResult ReadManual(List<string[]> rows)
    {
        var result = new RegisterReadResult { Format = RegisterFormat.Manual };
        for (var r = 0; r < Math.Min(rows.Count, 30); r++)
        {
            var header = rows[r].Select(h => h.Trim().ToUpperInvariant()).ToArray();
            var number = Match(header, NumberHeaders);
            var name = Match(header, NameHeaders, exclude: number);
            if (number is null || name is null) continue;

            var rev = Match(header, RevHeaders);
            var scale = Match(header, ScaleHeaders);
            var guid = Match(header, GuidHeaders);
            var kop = Match(header, TitleBlockHeaders);
            var note = Match(header, NoteHeaders);
            // Satu kolom per revisi ("Seq 3 · 30-09-2026 · FIRST ISSUE"): sel terisi = revisi ada di sheet.
            var seqs = header.Select((h, c) => (Match: SeqHeader.Match(h), Column: c)).Where(x => x.Match.Success)
                .Select(x => (Seq: int.Parse(x.Match.Groups[1].Value), x.Column)).ToList();
            for (var i = r + 1; i < rows.Count; i++)
            {
                var num = Cell(rows[i], number).Trim();
                if (num.Length == 0 || num.StartsWith('{')) continue;
                result.Entries.Add(new RegisterEntry
                {
                    UniqueId = NullIfEmpty(Cell(rows[i], guid)),
                    Number = num,
                    Name = Cell(rows[i], name),
                    Revision = NullIfEmpty(Cell(rows[i], rev)),
                    Scale = NullIfEmpty(Cell(rows[i], scale)),
                    TitleBlock = NullIfEmpty(Cell(rows[i], kop)),
                    Note = note is null ? null : Cell(rows[i], note).Trim(),
                    RevisionSeqs = seqs.Count == 0 ? null : seqs
                        .Where(s => !Unticked.Contains(Cell(rows[i], s.Column).Trim().ToUpperInvariant())).Select(s => s.Seq).ToHashSet(),
                    SourceRow = i + 1,
                });
            }
            return result;
        }
        result.Warnings.Add("Header kolom nomor dan judul drawing tidak ditemukan (contoh: 'DRAWING NUMBER' dan 'NAMA DRAWING').");
        return result;
    }

    private static int? Match(string[] header, string[] candidates, int? exclude = null)
    {
        // Prioritas: kandidat yang lebih spesifik dulu, cocok persis lalu mengandung.
        foreach (var cand in candidates)
            for (var c = 0; c < header.Length; c++)
                if (c != exclude && header[c] == cand) return c;
        foreach (var cand in candidates)
            for (var c = 0; c < header.Length; c++)
                if (c != exclude && header[c].Length > 0 && header[c].Contains(cand)) return c;
        return null;
    }

    private static string Cell(string[] row, int? col) => col is { } c && c < row.Length ? row[c] : "";
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
