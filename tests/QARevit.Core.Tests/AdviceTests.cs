using QARevit.Core.Ai;
using QARevit.Core.Config;
using QARevit.Core.Model;
using QARevit.Core.Rules;

namespace QARevit.Core.Tests;

public class AdviceTests
{
    private sealed class FakeClient : IAiClient
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        {
            Calls.Add(messages.ToList());
            return Task.FromResult($"jawaban {Calls.Count}");
        }
    }

    [Fact]
    public async Task Advice_sends_qa_context_and_keeps_chat_history()
    {
        var cfg = QaConfig.LoadDefault();
        var report = new RuleEngine().Run(new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101", Name = "LIGHTING  LAYOUT", PlacedViewCount = 1 },
        }, cfg);
        var client = new FakeClient();
        var svc = new AdviceService(client);

        Assert.Equal("jawaban 1", await svc.GenerateAdviceAsync(report, cfg, null));
        Assert.Contains("KRF-DWG-BD-ME-02-LP-1101", client.Calls[0][0].Content);

        await svc.ChatAsync("sheet mana dulu?");
        Assert.Equal(3, client.Calls[1].Count); // user, assistant, user
    }

    [Fact]
    public void Rule_advice_summarises_by_rule()
    {
        var cfg = QaConfig.LoadDefault();
        var report = new RuleEngine().Run(new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101", Name = "LIGHTING  LAYOUT", PlacedViewCount = 1 },
        }, cfg);
        var text = RuleAdvice.Build(report, null);
        Assert.Contains("TITLE-TEXT", text);
        Assert.Contains("Fix otomatis", text);
    }

    [Fact]
    public void Ai_fix_proposals_are_validated_before_becoming_changes()
    {
        var cfg = QaConfig.LoadDefault();
        var a = new SheetRevision(3, "A", "30-09-2026", "FIRST ISSUE", 0);
        var b = new SheetRevision(5, "B", "12-10-2026", "REVISION", 1);
        var sheets = new List<SheetInfo>
        {
            new() { UniqueId = "u1", Number = "KRF-DWG-BD-ME-02-LP-1101", Name = "LIGHTING LAYOUT", PlacedViewCount = 1, Revisions = { a, b }, RevNote = "" },
            new() { UniqueId = "u2", Number = "KRF-DWG-BD-ME-02-LP-1102", Name = "LIGHTING LAYOUT 2", PlacedViewCount = 1, Revisions = { a } },
        };
        var report = new RuleEngine().Run(sheets, cfg);

        // REV_NOTE kosong tidak punya Fix otomatis, jadi masuk ke prompt AI.
        Assert.Contains("REV_NOTE kosong", AiFixPlanner.BuildPrompt(report));

        var answer = """
            ```json
            [
              {"sheet":"KRF-DWG-BD-ME-02-LP-1101","action":"set_note","value":"UPDATE LIGHTING","reason":"sama dengan sheet LP lain"},
              {"sheet":"KRF-DWG-BD-ME-02-LP-1102","action":"apply_revision","value":"Seq 5","reason":""},
              {"sheet":"KRF-DWG-BD-ME-02-LP-1102","action":"apply_revision","value":"9","reason":"revisi karangan"},
              {"sheet":"KRF-DWG-BD-ME-02-LP-1102","action":"renumber","value":"KRF-DWG-BD-ME-02-LP-1101","reason":"bentrok"},
              {"sheet":"KRF-DWG-BD-ME-02-LP-1102","action":"delete_sheet","value":"","reason":"aksi di luar daftar"},
              {"sheet":"TIDAK-ADA","action":"rename","value":"X","reason":""},
              {"sheet":"KRF-DWG-BD-ME-02-LP-1101","action":"manual","value":"","reason":"taruh view di sheet"}
            ]
            ```
            """;
        var changes = AiFixPlanner.ToChanges(AiFixPlanner.Parse(answer), sheets, cfg, out var rejected);

        Assert.Equal(3, changes.Count);
        Assert.Equal("UPDATE LIGHTING", changes[0].Extra!.NewNote);
        Assert.Equal(new[] { 3, 5 }, changes[1].Extra!.NewRevisionSeqs);
        Assert.False(changes[2].Applicable); // "manual" hanya ditampilkan
        Assert.Equal(4, rejected.Count);
    }
}
