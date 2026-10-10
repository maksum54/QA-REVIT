using QARevit.Core.Config;
using QARevit.Core.Excel;
using QARevit.Core.Model;
using QARevit.Core.Register;

namespace QARevit.Core.Tests;

public class TitleBlockTests
{
    private sealed record Tb(string Family, string Type);

    private static readonly Tb[] Options =
    {
        new("KOP A1 ME", "Standard"),
        new("KOP A1 ME", "Construction"),
        new("KOP A3", "Standard"),
    };

    [Theory]
    [InlineData("KOP A1 ME : Construction", "KOP A1 ME", "Construction")]
    [InlineData("kop a1 me:construction", "KOP A1 ME", "Construction")]
    [InlineData("Construction", "KOP A1 ME", "Construction")]
    [InlineData("KOP A3", "KOP A3", "Standard")]
    public void Matches_family_type_text(string text, string family, string type)
    {
        var m = TitleBlockMatcher.Find(text, Options, o => o.Family, o => o.Type);
        Assert.Equal(new Tb(family, type), m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KOP TIDAK ADA")]
    public void Unknown_or_empty_returns_null(string? text) =>
        Assert.Null(TitleBlockMatcher.Find(text, Options, o => o.Family, o => o.Type));

    [Fact]
    public void Manual_register_reads_kop_column_into_create_change()
    {
        using var ms = new MemoryStream();
        SimpleXlsx.Write(ms, new[] { ("S", (IEnumerable<IReadOnlyList<object?>>)new IReadOnlyList<object?>[]
        {
            new object?[] { "DRAWING NUMBER", "NAMA DRAWING", "KOP" },
            new object?[] { "KRF-DWG-BD-ME-02-LP-1101", "LIGHTING LAYOUT", "KOP A1 ME : Standard" },
        }) });
        ms.Position = 0;
        var entry = Assert.Single(RegisterReader.Read(SimpleXlsx.ReadFirstSheet(ms)).Entries);
        Assert.Equal("KOP A1 ME : Standard", entry.TitleBlock);
        Assert.Equal("LIGHTING LAYOUT", entry.Name);

        var change = Assert.Single(RegisterComparer.Compare(new[] { entry }, Array.Empty<SheetInfo>(), QaConfig.LoadDefault()));
        Assert.Equal(ChangeKind.CreateSheet, change.Kind);
        Assert.Equal("KOP A1 ME : Standard", change.TitleBlock);
    }
}
