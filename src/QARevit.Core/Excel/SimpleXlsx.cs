using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace QARevit.Core.Excel;

/// <summary>
/// Pembaca/penulis .xlsx minimal (hanya nilai sel, tanpa format). Sengaja tanpa library pihak ketiga
/// supaya tidak bentrok dengan DLL yang sudah dimuat Revit.
/// </summary>
public static class SimpleXlsx
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Baca worksheet pertama sebagai baris berisi teks. Sel kosong = "".</summary>
    public static List<string[]> ReadFirstSheet(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadFirstSheet(fs);
    }

    public static List<string[]> ReadFirstSheet(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var shared = ReadSharedStrings(zip);
        var sheetPath = FirstSheetPath(zip);
        var sheet = Load(zip, sheetPath) ?? throw new InvalidDataException("Worksheet tidak ditemukan.");

        var rows = new SortedDictionary<int, Dictionary<int, string>>();
        var maxCol = 0;
        var implicitRow = 0;
        foreach (var row in sheet.Descendants(S + "row"))
        {
            var rowIndex = int.TryParse((string?)row.Attribute("r"), out var ri) ? ri : implicitRow + 1;
            implicitRow = rowIndex;
            var cells = new Dictionary<int, string>();
            var implicitCol = 0;
            foreach (var c in row.Elements(S + "c"))
            {
                var col = ColumnIndex((string?)c.Attribute("r")) ?? implicitCol + 1;
                implicitCol = col;
                var value = CellValue(c, shared);
                if (value.Length == 0) continue;
                cells[col] = value;
                maxCol = Math.Max(maxCol, col);
            }
            rows[rowIndex] = cells;
        }

        var result = new List<string[]>();
        if (rows.Count == 0) return result;
        for (var r = 1; r <= rows.Keys.Max(); r++)
        {
            var arr = new string[maxCol];
            for (var c = 1; c <= maxCol; c++)
                arr[c - 1] = rows.TryGetValue(r, out var cells) && cells.TryGetValue(c, out var v) ? v : "";
            result.Add(arr);
        }
        return result;
    }

    private static string CellValue(XElement c, IReadOnlyList<string> shared)
    {
        var type = (string?)c.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(c.Descendants(S + "t").Select(t => t.Value));
        var v = c.Element(S + "v")?.Value ?? "";
        return type switch
        {
            "s" when int.TryParse(v, out var idx) && idx < shared.Count => shared[idx],
            "b" => v == "1" ? "TRUE" : "FALSE",
            _ => v,
        };
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var doc = Load(zip, "xl/sharedStrings.xml");
        if (doc is null) return new List<string>();
        // Rich text: gabungkan semua <t> di dalam <si>, abaikan teks fonetik <rPh>.
        return doc.Root!.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t")
                .Where(t => t.Parent?.Name != S + "rPh" && t.Ancestors(S + "rPh").FirstOrDefault() is null)
                .Select(t => t.Value)))
            .ToList();
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var firstSheet = workbook?.Root?.Element(S + "sheets")?.Elements(S + "sheet").FirstOrDefault();
        var relId = (string?)firstSheet?.Attribute(R + "id");
        var target = rels?.Root?.Elements(Pr + "Relationship")
            .FirstOrDefault(e => (string?)e.Attribute("Id") == relId)?.Attribute("Target")?.Value;
        if (target is null) return "xl/worksheets/sheet1.xml";
        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>"C12" → 3.</summary>
    public static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return n == 0 ? null : n;
    }

    public static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>Tulis workbook dengan beberapa sheet. Baris pertama tiap sheet ditebalkan (header).</summary>
    public static void Write(string path, IEnumerable<(string Name, IEnumerable<IReadOnlyList<object?>> Rows)> sheets) =>
        Write(path, sheets.Select(s => (s.Name, s.Rows, (SheetFormat?)null)));

    public static void Write(Stream stream, IEnumerable<(string Name, IEnumerable<IReadOnlyList<object?>> Rows)> sheets) =>
        Write(stream, sheets.Select(s => (s.Name, s.Rows, (SheetFormat?)null)));

    /// <summary>Tulis workbook; sheet yang punya <see cref="SheetFormat"/> ditata (judul, filter, lebar kolom, warna).</summary>
    public static void Write(string path, IEnumerable<(string Name, IEnumerable<IReadOnlyList<object?>> Rows, SheetFormat? Format)> sheets)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(fs, sheets);
    }

    public static void Write(Stream stream, IEnumerable<(string Name, IEnumerable<IReadOnlyList<object?>> Rows, SheetFormat? Format)> sheets)
    {
        var list = sheets.ToList();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var sheetEntries = new StringBuilder();
        var relEntries = new StringBuilder();
        var ctEntries = new StringBuilder();
        var fills = new List<string>(); // warna conditional format, dipakai bersama semua sheet
        for (var i = 0; i < list.Count; i++)
        {
            var id = i + 1;
            var safeName = SafeSheetName(list[i].Name, list.Take(i).Select(x => SafeSheetName(x.Name, Array.Empty<string>())));
            sheetEntries.Append($"<sheet name=\"{SecurityElement.Escape(safeName)}\" sheetId=\"{id}\" r:id=\"rId{id}\"/>");
            relEntries.Append($"<Relationship Id=\"rId{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{id}.xml\"/>");
            ctEntries.Append($"<Override PartName=\"/xl/worksheets/sheet{id}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            WriteEntry(zip, $"xl/worksheets/sheet{id}.xml", SheetXml(list[i].Rows, list[i].Format, fills));
        }
        var stylesId = list.Count + 1;
        relEntries.Append($"<Relationship Id=\"rId{stylesId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");

        WriteEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            ctEntries + "</Types>");
        WriteEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        WriteEntry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<bookViews><workbookView/></bookViews><sheets>" + sheetEntries + "</sheets></workbook>");
        WriteEntry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + relEntries + "</Relationships>");

        // cellXfs: 0 biasa, 1 tebal, 2 judul, 3 header tabel, 4 baris grup, 5 rata tengah.
        var dxfs = string.Concat(fills.Select(rgb => $"<dxf><fill><patternFill><bgColor rgb=\"{rgb}\"/></patternFill></fill></dxf>"));
        WriteEntry(zip, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"16\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E6E6\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF808080\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf></cellXfs>" +
            (fills.Count > 0 ? $"<dxfs count=\"{fills.Count}\">{dxfs}</dxfs>" : "") +
            "</styleSheet>");
    }

    private static string SheetXml(IEnumerable<IReadOnlyList<object?>> source, SheetFormat? f, List<string> fills)
    {
        var rows = source.ToList();
        var lastCol = ColumnName(Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Count)));
        var width = rows.Count == 0 ? 1 : rows.Max(r => r.Count);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (f is not null)
        {
            // Judul dan header tetap terlihat saat scroll.
            sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{f.HeaderRow}\" topLeftCell=\"A{f.HeaderRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            if (f.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                for (var c = 1; c <= f.ColumnWidths.Count; c++)
                    sb.Append($"<col min=\"{c}\" max=\"{c}\" width=\"{f.ColumnWidths[c - 1].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"" +
                              (f.HiddenColumns.Contains(c) ? " hidden=\"1\"" : "") + "/>");
                sb.Append("</cols>");
            }
        }
        sb.Append("<sheetData>");
        for (var r = 1; r <= rows.Count; r++)
        {
            var row = rows[r - 1];
            var isGroup = f is not null && f.GroupRows.Contains(r);
            var height = f is null ? "" : r == f.HeaderRow ? " ht=\"48\" customHeight=\"1\"" : f.TitleRows.Contains(r) ? " ht=\"28\" customHeight=\"1\"" : "";
            sb.Append($"<row r=\"{r}\"{height}>");
            for (var c = 0; c < (isGroup ? width : row.Count); c++)
            {
                var value = c < row.Count ? row[c] : null;
                var styleId = f is null ? (r == 1 ? 1 : 0)
                    : r == f.HeaderRow ? 3 : f.TitleRows.Contains(r) ? 2 : isGroup ? 4
                    : r > f.HeaderRow && f.CenteredColumns.Contains(c + 1) ? 5 : 0;
                var style = styleId == 0 ? "" : $" s=\"{styleId}\"";
                var cellRef = ColumnName(c + 1) + r;
                switch (value)
                {
                    case null:
                        if (isGroup) sb.Append($"<c r=\"{cellRef}\"{style}/>"); // arsir baris grup sampai kolom terakhir
                        break;
                    case int or long or double or float or decimal:
                        sb.Append($"<c r=\"{cellRef}\"{style}><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    default:
                        var text = SecurityElement.Escape(CleanXmlText(value.ToString() ?? ""));
                        sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{text}</t></is></c>");
                        break;
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (f is not null)
        {
            if (f.AutoFilter && rows.Count > f.HeaderRow)
                sb.Append($"<autoFilter ref=\"A{f.HeaderRow}:{lastCol}{rows.Count}\"/>");
            if (f.TitleRows.Count > 0)
                sb.Append($"<mergeCells count=\"{f.TitleRows.Count}\">" +
                          string.Concat(f.TitleRows.Select(t => $"<mergeCell ref=\"{ColumnName(f.TitleColumn)}{t}:{lastCol}{t}\"/>")) + "</mergeCells>");
            // Sel terisi di kolom ini diberi warna, termasuk yang diketik belakangan di Excel.
            var priority = 1;
            foreach (var (column, rgb) in f.ColumnFills.OrderBy(kv => kv.Key))
            {
                var dxf = fills.IndexOf(rgb);
                if (dxf < 0) { fills.Add(rgb); dxf = fills.Count - 1; }
                var name = ColumnName(column);
                var first = f.HeaderRow + 1;
                sb.Append($"<conditionalFormatting sqref=\"{name}{first}:{name}{rows.Count + 500}\"><cfRule type=\"expression\" dxfId=\"{dxf}\" priority=\"{priority++}\">" +
                          $"<formula>LEN(TRIM({name}{first}))&gt;0</formula></cfRule></conditionalFormatting>");
            }
        }
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string CleanXmlText(string s) =>
        new(s.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());

    private static string SafeSheetName(string name, IEnumerable<string> existing)
    {
        var cleaned = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Sheet";
        if (cleaned.Length > 31) cleaned = cleaned[..31];
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = cleaned;
        for (var n = 2; used.Contains(candidate); n++)
            candidate = cleaned[..Math.Min(cleaned.Length, 28)] + n;
        return candidate;
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}

/// <summary>Tata letak satu sheet. Nomor baris dan kolom mulai dari 1.</summary>
public sealed class SheetFormat
{
    /// <summary>Baris header tabel: ditebalkan, dibekukan, dan diberi filter.</summary>
    public int HeaderRow { get; init; } = 1;
    public bool AutoFilter { get; init; }
    public IReadOnlyList<double> ColumnWidths { get; init; } = Array.Empty<double>();
    public IReadOnlySet<int> HiddenColumns { get; init; } = new HashSet<int>();
    public IReadOnlySet<int> CenteredColumns { get; init; } = new HashSet<int>();
    /// <summary>Baris judul besar, digabung dari <see cref="TitleColumn"/> sampai kolom terakhir.</summary>
    public IReadOnlySet<int> TitleRows { get; init; } = new HashSet<int>();
    public int TitleColumn { get; init; } = 1;
    /// <summary>Baris pemisah grup (tebal, diarsir).</summary>
    public IReadOnlySet<int> GroupRows { get; init; } = new HashSet<int>();
    /// <summary>Kolom → warna ARGB untuk sel yang terisi.</summary>
    public IReadOnlyDictionary<int, string> ColumnFills { get; init; } = new Dictionary<int, string>();
}
