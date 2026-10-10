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
}
