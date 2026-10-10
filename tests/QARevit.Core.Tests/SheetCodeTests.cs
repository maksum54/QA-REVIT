using QARevit.Core.Config;
using QARevit.Core.Model;

namespace QARevit.Core.Tests;

public class SheetCodeTests
{
    private readonly QaConfig _cfg = QaConfig.LoadDefault();

    [Theory]
    [InlineData("KRF-DWG-BD-ME-01.1-LP-4401", "KRF", "BD", "01.1", "LP", '4', null)]
    [InlineData("PGR-DWG-DC-ME-00-EP-1101.2", "PGR", "DC", "00", "EP", '1', "2")]
    [InlineData("ARW-DWG-BD-ME-02-FA-4108", "ARW", "BD", "02", "FA", '4', null)]
    public void Parses_project_numbers(string number, string project, string stage, string zone, string system, char type, string? phase)
    {
        var code = SheetCode.TryParse(number);
        Assert.NotNull(code);
        Assert.Equal(project, code!.Project);
        Assert.Equal(stage, code.Stage);
        Assert.Equal(zone, code.Zone);
        Assert.Equal(system, code.System);
        Assert.Equal(type, code.DrawingType);
        Assert.Equal(phase, code.Phase);
        Assert.Equal(number, code.Format());
        Assert.Empty(code.Validate(_cfg));
    }

    [Theory]
    [InlineData("A101")]
    [InlineData("KRF-DWG-BD-ME-LP-1101")]
    [InlineData("")]
    public void Rejects_bad_structure(string number) => Assert.Null(SheetCode.TryParse(number));

    [Fact]
    public void Validate_flags_unknown_stage_and_system()
    {
        var problems = SheetCode.TryParse("KRF-DWG-XX-ME-00-ZZ-1101")!.Validate(_cfg).ToList();
        Assert.Contains(problems, p => p.Contains("stage"));
        Assert.Contains(problems, p => p.Contains("sistem"));
    }

    [Fact]
    public void Stage_order_is_CD_BD_DD_DC()
    {
        Assert.Equal(new[] { "CD", "BD", "DD", "DC" }, _cfg.Stages.Select(s => s.Code));
        Assert.True(_cfg.StageIndex("BD") < _cfg.StageIndex("DC"));
        Assert.Equal("Detail Construction", _cfg.StageName("DC"));
    }
}
