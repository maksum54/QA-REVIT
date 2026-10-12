using QARevit.Core.Config;
using QARevit.Core.Model;

namespace QARevit.Core.Rules;

public interface IQaRule
{
    string Id { get; }
    IEnumerable<QaIssue> Check(IReadOnlyList<SheetInfo> sheets, QaConfig cfg);
}

public sealed class RuleEngine
{
    public IReadOnlyList<IQaRule> Rules { get; }

    public RuleEngine(IEnumerable<IQaRule>? rules = null) =>
        Rules = (rules ?? DefaultRules()).ToList();

    public static IEnumerable<IQaRule> DefaultRules() => new IQaRule[]
    {
        new CodeFormatRule(),
        new DuplicateNumberRule(),
        new ConsistencyRule(),
        new SystemTitleRule(),
        new DrawingTypeTitleRule(),
        new TitleTextRule(),
        new EmptySheetRule(),
        new ConstructionStageRule(),
        new ZoneCompletenessRule(),
        new SheetListSyncRule(),
        new RevisionCloudRule(),
        new TitleBlockRevisionRule(),
        new RevisionDateRule(),
        new SheetSetupRule(),
        new DuplicateTitleRule(),
        new OutsideTitleBlockRule(),
    };

    public QaReport Run(IReadOnlyList<SheetInfo> sheets, QaConfig cfg)
    {
        var issues = Rules.SelectMany(r => r.Check(sheets, cfg))
            .OrderByDescending(i => i.Severity).ThenBy(i => i.SheetNumber, StringComparer.Ordinal)
            .ToList();
        return new QaReport { Sheets = sheets, Issues = issues };
    }
}
