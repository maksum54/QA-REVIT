using QARevit.Core.Config;
using QARevit.Core.Model;
using QARevit.Core.Rules;

namespace QARevit.Core.Tests;

public class RuleTests
{
    private readonly QaConfig _cfg = QaConfig.LoadDefault();
    private int _id;

    private SheetInfo Sheet(string number, string name, int views = 1) =>
        new() { UniqueId = $"id-{_id++}", Number = number, Name = name, PlacedViewCount = views };

    private QaReport Run(params SheetInfo[] sheets) => new RuleEngine().Run(sheets, _cfg);

    [Fact]
    public void Clean_set_passes()
    {
        var report = Run(
            Sheet("KRF-DWG-BD-ME-01.1-LP-1101", "LIGHTING LAYOUT OFFICE GROUND FLOOR"),
            Sheet("KRF-DWG-BD-ME-01.1-SO-1101", "RECEPTACLE LAYOUT OFFICE GROUND FLOOR"),
            Sheet("KRF-DWG-BD-ME-01.1-EG-1101", "GROUNDING LAYOUT OFFICE GROUND FLOOR"),
            Sheet("KRF-DWG-BD-ME-01.1-LP-3301", "SECTION 1 - LIGHTING OFFICE STAIRCASE AREA"),
            Sheet("KRF-DWG-BD-ME-00-LV-6001", "SINGLE LINE DIAGRAM"));
        Assert.Empty(report.Issues);
        Assert.Equal(1.0, report.PassRate);
    }

    [Fact]
    public void Double_space_and_typo_get_rename_fix()
    {
        var report = Run(Sheet("KRF-DWG-BD-ME-01.1-LP-4401", "ENLARGED -  LIGHTING LAYOUT PLATFROM FAT TANKS"));
        var issue = Assert.Single(report.Issues, i => i.RuleId == "TITLE-TEXT");
        Assert.Equal(FixKind.RenameSheet, issue.Fix);
        Assert.Equal("ENLARGED - LIGHTING LAYOUT PLATFORM FAT TANKS", issue.FixValue);
    }

    [Fact]
    public void System_code_must_match_title()
    {
        var report = Run(Sheet("KRF-DWG-BD-ME-02-LP-1101", "GROUNDING LAYOUT ELECTRICAL BUILDING"));
        var issue = Assert.Single(report.Issues, i => i.RuleId == "SYSTEM-TITLE");
        Assert.Equal("KRF-DWG-BD-ME-02-EG-1101", issue.FixValue);
    }

    [Fact]
    public void Lightning_protection_is_not_confused_with_lighting()
    {
        var report = Run(Sheet("KRF-DWG-BD-ME-00-AL-1101", "LIGHTNING PROTECTION LAYOUT SITE PLAN"));
        Assert.DoesNotContain(report.Issues, i => i.RuleId == "SYSTEM-TITLE");
    }

    [Fact]
    public void Section_title_on_layout_series_is_flagged()
    {
        var report = Run(Sheet("KRF-DWG-BD-ME-01.1-LP-1105", "SECTION 3 - LIGHTING OFFICE"));
        Assert.Contains(report.Issues, i => i.RuleId == "DRAWING-TYPE" && i.Severity == Severity.Warning);
    }

    [Fact]
    public void Minority_stage_is_flagged_with_renumber_fix()
    {
        var report = Run(
            Sheet("KRF-DWG-DD-ME-02-LP-1101", "LIGHTING LAYOUT A"),
            Sheet("KRF-DWG-DD-ME-02-SO-1101", "RECEPTACLE LAYOUT A"),
            Sheet("KRF-DWG-BD-ME-02-EG-1101", "GROUNDING LAYOUT A"));
        var issue = Assert.Single(report.Issues, i => i.RuleId == "CONSISTENCY");
        Assert.Equal("KRF-DWG-DD-ME-02-EG-1101", issue.FixValue);
    }

    [Fact]
    public void Construction_stage_requires_revision_and_signatures()
    {
        var s = Sheet("PGR-DWG-DC-ME-01-LP-1101", "LIGHTING LAYOUT OFFICE");
        s.HasUnissuedRevisionClouds = true;
        var report = Run(s);
        var dc = report.Issues.Where(i => i.RuleId == "DC-READY").ToList();
        Assert.Equal(2, dc.Count);
        Assert.Contains(dc, i => i.Message.Contains("Checked By"));

        var ok = Sheet("PGR-DWG-DC-ME-01-LP-1102", "LIGHTING LAYOUT OFFICE ROOF");
        ok.CurrentRevision = "0"; ok.CurrentRevisionDate = "2026-10-01";
        ok.Parameters["Checked By"] = "AB"; ok.Parameters["Approved By"] = "CD";
        Assert.DoesNotContain(Run(ok).Issues, i => i.RuleId == "DC-READY");
    }

    [Fact]
    public void Zone_without_grounding_is_reported_as_info()
    {
        var report = Run(
            Sheet("KRF-DWG-BD-ME-08-LP-1101", "LIGHTING LAYOUT CAR & MOTOR PARKING"));
        var issue = Assert.Single(report.Issues, i => i.RuleId == "ZONE-COMPLETE");
        Assert.Equal(Severity.Info, issue.Severity);
        Assert.Equal(1, report.Passed); // info tidak membuat gagal
    }

    [Fact]
    public void Empty_sheet_and_duplicates_fail()
    {
        var report = Run(
            Sheet("KRF-DWG-BD-ME-02-PA-1101", "PUBLIC ADDRESS LAYOUT", views: 0),
            Sheet("KRF-DWG-DD-ME-02-PA-1101", "PUBLIC ADDRESS LAYOUT"));
        Assert.Contains(report.Issues, i => i.RuleId == "EMPTY-SHEET");
        Assert.Equal(2, report.Issues.Count(i => i.RuleId == "DUPLICATE"));
        Assert.Equal(0, report.Passed);
    }
}
