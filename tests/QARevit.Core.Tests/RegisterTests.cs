using QARevit.Core.Config;
using QARevit.Core.Excel;
using QARevit.Core.Model;
using QARevit.Core.Register;
using QARevit.Core.Report;

namespace QARevit.Core.Tests;

public class RegisterTests
{
    private readonly QaConfig _cfg = QaConfig.LoadDefault();

    private static List<string[]> WriteAndRead(params IReadOnlyList<object?>[] rows)
    {
        using var ms = new MemoryStream();
        SimpleXlsx.Write(ms, new[] { ("Sheet", (IEnumerable<IReadOnlyList<object?>>)rows) });
        ms.Position = 0;
        return SimpleXlsx.ReadFirstSheet(ms);
    }

    [Fact]
    public void Xlsx_roundtrip_keeps_text_and_numbers()
    {
        var rows = WriteAndRead(new object?[] { "A", "B & <C>" }, new object?[] { 12, null, "x" });
        Assert.Equal("B & <C>", rows[0][1]);
        Assert.Equal("12", rows[1][0]);
        Assert.Equal("", rows[1][1]);
        Assert.Equal("x", rows[1][2]);
    }

    [Fact]
    public void Reads_sheetlink_export_with_guid_metadata_row()
    {
        // Sama dengan struktur ACT SHEET LIST - DRAWING ELECTRICAL.xlsx: header, baris JSON metadata, baris kosong, data.
        var rows = WriteAndRead(
            new object?[] { null, "DRAWING NUMBER", "NAMA DRAWING" },
            new object?[]
            {
                "{\"UniqueId\":\"1f41\",\"Name\":\"GUID\",\"OtherParamType\":3}",
                "{\"UniqueId\":\"45eb\",\"Name\":\"Sheet Number\",\"OtherParamType\":1}",
                "{\"UniqueId\":\"77aa\",\"Name\":\"Sheet Name\",\"OtherParamType\":1}",
            },
            new object?[] { null },
            new object?[] { "guid-1", "KRF-DWG-BD-ME-00-EL-1101", "DRAWING LIST ELECTRICAL & ELECTRONIC" },
            new object?[] { "guid-2", "KRF-DWG-BD-ME-05-PA-1101", "PUBLIC ADDRESS  LAYOUT ETP AREA" });

        var result = RegisterReader.Read(rows);
        Assert.Equal(RegisterFormat.SheetLink, result.Format);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("guid-1", result.Entries[0].UniqueId);
        Assert.Equal("KRF-DWG-BD-ME-05-PA-1101", result.Entries[1].Number);
    }

    [Fact]
    public void Reads_manual_register()
    {
        var rows = WriteAndRead(
            new object?[] { "DRAWING LIST ELECTRICAL" },
            new object?[] { "NO", "DRAWING NO.", "DRAWING TITLE", "SCALE", "REV" },
            new object?[] { 1, "PGR-DWG-DC-ME-00-EP-1101.2", "MAIN CABLE ROUTE LAYOUT SITE PLAN", "1 : 400", "0" });
        var result = RegisterReader.Read(rows);
        Assert.Equal(RegisterFormat.Manual, result.Format);
        var e = Assert.Single(result.Entries);
        Assert.Equal("PGR-DWG-DC-ME-00-EP-1101.2", e.Number);
        Assert.Equal("1 : 400", e.Scale);
        Assert.Equal("0", e.Revision);
    }

    [Fact]
    public void Exported_register_can_be_imported_again()
    {
        var sheets = new List<SheetInfo> { new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101", Name = "LIGHTING" } };
        var path = Path.Combine(Path.GetTempPath(), $"qa-reg-{Guid.NewGuid():N}.xlsx");
        try
        {
            ReportExporter.ExportRegister(path, sheets);
            var e = Assert.Single(RegisterReader.Read(path).Entries);
            Assert.Equal("u1", e.UniqueId);
            Assert.Equal("KRF-DWG-BD-ME-02-LP-1101", e.Number);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compare_detects_create_rename_renumber_missing_and_stage()
    {
        var sheets = new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-DD-ME-02-LP-1101", Name = "LIGHTING LAYOUT A" },
            new() { UniqueId = "u2", Number = "KRF-DWG-DD-ME-02-SO-1101", Name = "RECEPTACLE OLD" },
            new() { UniqueId = "u3", Number = "KRF-DWG-DD-ME-02-EG-1101", Name = "GROUNDING LAYOUT A" },
        };
        var register = new List<RegisterEntry>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101", Name = "LIGHTING LAYOUT A", SourceRow = 4 },
            new() { Number = "KRF-DWG-DD-ME-02-SO-1101", Name = "RECEPTACLE LAYOUT A", SourceRow = 5 },
            new() { Number = "KRF-DWG-BD-ME-02-FA-1101", Name = "FIRE ALARM LAYOUT A", SourceRow = 6 },
        };

        var changes = RegisterComparer.Compare(register, sheets, _cfg);
        Assert.Contains(changes, c => c.Kind == ChangeKind.RenumberSheet && c.UniqueId == "u1" && c.Message.Contains("stage mundur"));
        Assert.Contains(changes, c => c.Kind == ChangeKind.RenameSheet && c.UniqueId == "u2" && c.NewName == "RECEPTACLE LAYOUT A");
        Assert.Contains(changes, c => c.Kind == ChangeKind.CreateSheet && c.Number == "KRF-DWG-BD-ME-02-FA-1101");
        Assert.Contains(changes, c => c.Kind == ChangeKind.MissingInRegister && c.UniqueId == "u3");
        Assert.Contains(changes, c => c.Kind == ChangeKind.StageMismatch && c.Message.Contains("Register masih BD"));
    }

    [Fact]
    public void Plan_stage_change_renumbers_all()
    {
        var sheets = new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101" },
            new() { UniqueId = "u2", Number = "KRF-DWG-DD-ME-02-SO-1101" },
        };
        var plan = RegisterComparer.PlanStageChange(sheets, "DD");
        var c = Assert.Single(plan);
        Assert.Equal("KRF-DWG-DD-ME-02-LP-1101", c.NewNumber);
    }

    [Fact]
    public void Qa_report_export_writes_all_sheets()
    {
        var report = new Rules.RuleEngine().Run(new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "BAD", Name = "X", PlacedViewCount = 1 },
        }, _cfg);
        var path = Path.Combine(Path.GetTempPath(), $"qa-rep-{Guid.NewGuid():N}.xlsx");
        try
        {
            ReportExporter.ExportQa(path, report, aiAdvice: "baris 1\nbaris 2");
            var summary = SimpleXlsx.ReadFirstSheet(path);
            Assert.Equal("Total sheet", summary[1][0]);
            Assert.Equal("1", summary[1][1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Drawing_list_with_note_and_revisions_roundtrips_and_detects_edits()
    {
        var sheets = new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "ARW-DWG-BD-ME-02-EP-4103", Name = "CABLE TRAY #3", CurrentRevision = "B" },
            new() { UniqueId = "u2", Number = "ARW-DWG-BD-ME-02-EP-4104", Name = "CABLE TRAY #4", CurrentRevision = "A" },
        };
        var states = new List<SheetRevState>
        {
            new("u1", sheets[0].Number, "CHANGE ROUTE", new[] { 3, 5 }),
            new("u2", sheets[1].Number, "", new[] { 3 }),
        };
        var revisions = new List<RevisionColumn> { new(3, "30-09-2026", "FIRST ISSUE"), new(4, "30-09-2026", "REVISION"), new(5, "12-10-2026", "REVISION") };

        sheets[0].Parameters["ACT SHEET DISCIPLINE"] = sheets[1].Parameters["ACT SHEET DISCIPLINE"] = "ELECTRICAL & ELECTRONICS";
        var path = Path.Combine(Path.GetTempPath(), $"qa-list-{Guid.NewGuid():N}.xlsx");
        ReportExporter.ExportRegister(path, sheets, states, revisions);
        var rows = SimpleXlsx.ReadFirstSheet(path);
        File.Delete(path);
        var h = rows.FindIndex(r => r.Contains("NOTE")) + 1; // baris data pertama: judul, header dan baris grup ada di atasnya
        while (rows[h].Length < 3 || rows[h][2].Length == 0) h++;
        var entries = RegisterReader.Read(rows).Entries;

        Assert.Equal("B", entries[0].Revision); // kolom "Seq n · … · REVISION" tidak boleh terbaca sebagai kolom REV
        Assert.Equal(new[] { 3, 5 }, entries[0].RevisionSeqs!.OrderBy(x => x));
        Assert.Empty(RegisterComparer.PlanExtras(entries, states));

        // Edit di Excel: sheet 1 batal revisi Seq 5, sheet 2 dapat Seq 5 dan note baru.
        var seq5 = Array.IndexOf(rows.First(r => r.Contains("NOTE")), revisions[2].Header);
        var note = Array.IndexOf(rows.First(r => r.Contains("NOTE")), "NOTE");
        rows[h] = Pad(rows[h], seq5); rows[h + 1] = Pad(rows[h + 1], seq5);
        rows[h][seq5] = "";
        rows[h + 1][seq5] = "x";
        rows[h + 1][note] = "UPDATE CABLE TRAY";

        static string[] Pad(string[] row, int index) => row.Length > index ? row : row.Concat(Enumerable.Repeat("", index + 1 - row.Length)).ToArray();

        var extras = RegisterComparer.PlanExtras(RegisterReader.Read(rows).Entries, states);
        Assert.Equal(2, extras.Count);
        Assert.Equal(new[] { 3 }, extras[0].NewRevisionSeqs);
        Assert.Null(extras[0].NewNote);
        Assert.Equal(new[] { 3, 5 }, extras[1].NewRevisionSeqs!.OrderBy(x => x));
        Assert.Equal("UPDATE CABLE TRAY", extras[1].NewNote);
    }
}
