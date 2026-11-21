using System.IO;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Tests;

public class StrataSnapshotTests
{
    private static StrataProvider Provider(Func<DateTime>? clock = null)
    {
        var config = new EngineConfig { Log = Path.Combine(Path.GetTempPath(), "does-not-exist.log") };
        return new StrataProvider(config, new FakeSource(), clock);
    }

    private static StrataMetrics Metrics(string live)
    {
        var json = @"{""engine"": {""model"": ""m"", ""max_context"": 98304},
         ""live"": " + live + @",
         ""requests"": [{""prompt_tokens"": 42385, ""reused"": 42297, ""prompt_read"": 88, ""prompt_ms"": 2444.2,
                       ""output_tokens"": 9, ""decode_tok_s"": 30.7, ""duration_s"": 2.7}],
         ""hardware"": {""ram_used"": 100, ""ram_total"": 200, ""gpu_util"": 90, ""cpu"": 10},
         ""conversation_cache"": {""budget_mib"": 7168, ""bytes"": 1048576, ""slots"": 4, ""parked"": 0, ""requests"": 10, ""requests_reused"": 9, ""evictions"": 1},
         ""totals"": {""requests"": 5, ""output_tokens"": 50, ""prompt_tokens"": 1000, ""reused"": 500, ""since"": 1700000000}}";
        return Json.Deserialize(json, Json.MetricsType)!;
    }

    [Theory]
    [InlineData(0, 17.0)]
    [InlineData(1, null)]
    public void OutputProgressRequiresAllActiveRequestsToBeInTheSlotTable(int outside, double? activeTokens)
    {
        var json = $$"""
            {"state":"generating","generated":17,"tok_s":32,"parallel":2,"running":{{1 + outside}},"outside_slots":{{outside}},
             "slots":[{"slot":0,"state":"decoding","generated":17},{"slot":1,"state":"idle","held_tokens":900}]}
            """;
        var snapshot = Provider().Translate(Metrics(json), "m");
        Assert.Equal(new OutputProgress(1700000000, 5, 50, activeTokens), snapshot.OutputProgress);
        Assert.Equal(32, new SpeedHistory().RatesBetweenPolls(123, snapshot).Decode);
    }

    [Fact]
    public void PrefillDoesNotImplyZeroOutputWhenOtherRequestsRunOutsideTheSlotTable()
    {
        var snapshot = Provider().Translate(Metrics("""
            {"state":"busy","phase":"prompt","parallel":2,"running":2,"outside_slots":2,
             "slots":[{"slot":0,"state":"idle"},{"slot":1,"state":"idle"}]}
            """), "m");
        Assert.True(double.IsNaN(new SpeedHistory().RatesBetweenPolls(123, snapshot).Decode));
    }

    [Fact]
    public void PrefillSpeedIsDerivedFromProgressBetweenPolls()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var provider = Provider(() => now);
        string Live(int read) => @"{""state"": ""busy"", ""phase"": ""prompt"", ""prompt_total"": 50000, ""prompt_read"": " + read + @", ""generated"": 0, ""elapsed_s"": 0.5}";
        Assert.Null(provider.Translate(Metrics(Live(1000)), "m").Live!.PrefillTokensPerSecond);
        now = now.AddSeconds(2);
        Assert.Equal(500, provider.Translate(Metrics(Live(2000)), "m").Live!.PrefillTokensPerSecond);
    }

    [Fact]
    public void TheRouterNameIsShownAndTheIdIsTheFallback()
    {
        // llama-swap's /running has the model id and, when its config gives one, the friendly name.
        var running = Json.Deserialize(@"{""running"": [{""model"": ""qwen-27b-q4"", ""state"": ""ready"", ""name"": "" Qwen 3.6 27B ""}, {""model"": ""gemma-4b"", ""state"": ""ready""}, {""model"": ""blank"", ""name"": ""  ""}]}", Json.RunningType)!;
        Assert.Equal("Qwen 3.6 27B", StrataProvider.Title(running.Running![0]!));
        Assert.Equal("gemma-4b", StrataProvider.Title(running.Running![1]!));
        Assert.Equal("blank", StrataProvider.Title(running.Running![2]!));
    }
}

/// <summary>Every recorded payload with an .expected.json goes through the translation and is compared field by field.</summary>
public class RecordedPayloadTests
{
    [Fact]
    public void EveryRecordedPayloadMatchesItsExpectedValues()
    {
        Assert.NotEmpty(Fixtures.Payloads());
        foreach (var name in Fixtures.Payloads()) MatchesTheExpectedValues(name);
    }

    private static void MatchesTheExpectedValues(string name)
    {
        var expected = Fixtures.Expected(name);
        var provider = new StrataProvider(new EngineConfig(), new FakeSource());
        var s = provider.Translate(Fixtures.Metrics(name), "m");

        Assert.Equal(expected.Status, StatusName(s.Status));
        Assert.Equal(expected.Title, Display.Title(s), ignoreCase: true);       // the Windows app starts every line with a capital letter
        Assert.Equal(expected.Queued, s.Queued);
        Assert.Equal(expected.Percent, s.Live?.Percent);
        Assert.Equal(expected.ContextUsed, s.Context.Used);
        Assert.Equal(expected.ContextMax, s.Context.Max);
        Assert.Equal(expected.Recent, s.Recent.Count);
        Assert.Equal(expected.LastRequestHit, s.Recent.Count > 0 ? HitName(s.Recent[0].Hit) : null);
        if (expected.RunningCacheHit != null)
            Assert.Equal(expected.RunningCacheHit, HitName(Display.Progress(s)?.Hit));
        Assert.Equal(expected.CacheSlots, s.Cache?.Slots);
        Assert.Equal(expected.CacheParked, s.Cache?.Parked);
        Assert.Equal(expected.BatchSlots, s.Slots?.Count);
        Assert.Equal(expected.HasSystem, s.System != null);
        Assert.Equal(expected.HasTotals, s.Totals != null);
    }

    private static string StatusName(EngineStatus s) => s.ToString().ToLowerInvariant();
    private static string? HitName(CacheHit? h) => h?.ToString().ToLowerInvariant();

    [Fact]
    public void NullValuesAreUnknownNotZeroAndASingleRequestIsLeftAsItIs()
    {
        // The real "generating" payload has "prompt_read": null.
        var s = new StrataProvider(new EngineConfig(), new FakeSource()).Translate(Fixtures.Metrics("strata-0.1.56-thinking.json"), "m");
        Assert.Null(s.Live!.PromptRead);
        Assert.Null(s.Live.PromptTotal);
        Assert.Null(s.Live.ReusedEstimate);
        var single = Translate("strata-0.1.56-decode.json");       // recorded: one request on the solo path, all four slots idle
        Assert.Null(single.Live!.Running);
        Assert.False(single.Live.IsParallel);
        Assert.Equal(164.7, single.Live.DecodeTokensPerSecond);
        var busy = Display.Readout(single);                         // the readout: the speed while busy, nothing otherwise
        Assert.Equal(("165", "t/s"), (busy.Value, busy.Unit));
        Assert.Same(ReadoutText.None, Display.Readout(Translate("strata-0.1.56-idle.json")));
    }

    private static EngineSnapshot Translate(string fixture) =>
        new StrataProvider(new EngineConfig(), new FakeSource()).Translate(Fixtures.Metrics(fixture), "m");


    /// <summary>One provider, a clock the test moves: the speeds of the slots come from the generated counts between two polls.</summary>
    private static (EngineSnapshot First, EngineSnapshot Later) TwoPolls(string first, string later, double seconds)
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var provider = new StrataProvider(new EngineConfig(), new FakeSource(), () => now);
        var a = provider.Translate(Fixtures.Metrics(first), "m");
        now = now.AddSeconds(seconds);
        return (a, provider.Translate(Fixtures.Metrics(later), "m"));
    }

    /// <summary>The slot entries are shaped after serve/server.py (slots_view); the files are hand-made, not recorded.</summary>
    [Fact]
    public void RequestsRunningTogetherShowTheEnginesTotalAndEverySlotItsOwnDerivedSpeed()
    {
        var (first, later) = TwoPolls("strata-parallel-decode.json", "strata-parallel-decode-later.json", 2);
        Assert.Equal(EngineStatus.Decode, later.Status);
        Assert.Equal(4, later.Live!.Running);
        Assert.Equal(420.2, later.Live.DecodeTokensPerSecond!.Value, 6);            // live.tok_s is the engine's sum over all generating requests
        Assert.Equal("420", Display.Readout(later).Value);
        Assert.Equal(776 + 638 + 554 + 492, later.Live.Generated);
        Assert.All(first.Slots!, s => Assert.Null(s.TokensPerSecond));              // one poll: no speed yet (a slot's own tok_s counts tokens from before it)
        Assert.Equal(new double[] { 128, 104, 97, 91 }, later.Slots!.Select(s => s.TokensPerSecond!.Value).ToArray());       // (generated now - 2 s ago) / 2 s
        Assert.Equal(1180 + 776, later.Slots![0].Tokens);                           // a running slot holds its prompt and what it has written
    }

    /// <summary>The slot payload of a live engine with 2 of 4 slots decoding (values as reported; the second decoding slot and the last idle slot filled in by hand).</summary>
    private static void TwoOfFourSlotsDecodingHaveNoSlotWithAMissingSize()
    {
        var s = Translate("strata-parallel-two-of-four.json");
        Assert.Equal(83.2, s.Live!.DecodeTokensPerSecond!.Value, 6);
        Assert.Equal(2, s.Live.Running);
        Assert.Equal(4, s.Slots!.Count);
        Assert.All(s.Slots, slot => Assert.True(slot.Tokens.HasValue));       // an idle slot holds held_tokens, a decoding one its prompt and its output
        Assert.Equal(52127 + 4729, s.Slots[1].Tokens);
        Assert.Equal(91473, s.Slots[0].Tokens);
    }

    [Fact]
    public void ReadingAndGeneratingTogetherAndTheCombinedBarsFollowTheRealNumbers()
    {
        TwoOfFourSlotsDecodingHaveNoSlotWithAMissingSize();
        SeveralReadingRequestsShowOneBarOverTheSumOfTheirPrompts();
        TheMainBarCombinesTheRunningRequestsWithRealLimitsWhereTheEngineHasThemAndOtherwiseDoesNotInventOne();
        ReadingAndGeneratingTogetherShowTheDecodeTotalFromTheSlotsAndTheDotNotTheBar();
    }

    private static void ReadingAndGeneratingTogetherShowTheDecodeTotalFromTheSlotsAndTheDotNotTheBar()
    {
        // The newest request reads, so live.tok_s is null: the total is the sum of the derived speeds of the generating slots.
        var (first, later) = TwoPolls("strata-parallel-mixed.json", "strata-parallel-mixed-later.json", 2);
        Assert.Null(first.Live!.DecodeTokensPerSecond);                              // no baseline yet: nothing is shown, no guess
        Assert.Equal(EngineStatus.Decode, later.Status);
        Assert.Equal(ServerState.Writing, Display.StateOf(later));
        Assert.Equal((756.0 - 520) / 2, later.Live!.DecodeTokensPerSecond!.Value, 6);
        Assert.Equal(3, later.Live.Running);
        Assert.Null(Display.Progress(later));                                        // no bar while any slot generates
        Assert.Equal(1, later.Queued);
        Assert.Equal(2200, later.Slots![3].Tokens);                                  // an idle slot holds what it kept for a next turn
    }

    private static void SeveralReadingRequestsShowOneBarOverTheSumOfTheirPrompts()
    {
        var s = Translate("strata-parallel-reading.json");
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(2048 + 1000, s.Live!.PromptRead);
        Assert.Equal(6771 + 3000, s.Live.PromptTotal);
        Assert.Equal(31, Display.Progress(s)!.Value.Fraction * 100, 0);        // (2048 + 1000) / (6771 + 3000)
    }

    private static void TheMainBarCombinesTheRunningRequestsWithRealLimitsWhereTheEngineHasThemAndOtherwiseDoesNotInventOne()
    {
        // Reading: all prompt tokens read over all prompt tokens (real).
        var reading = Translate("strata-parallel-reading.json").Slots!.Where(s => s.IsActive).ToList();
        var r = Display.Combined(reading);
        Assert.True(r.IsLimit);
        Assert.Equal((2048.0 + 1000) / (6771 + 3000), r.Fraction, 6);
        // Generating, as Strata reports today: no limit per slot, so each bar is its length against the longest and the main bar their mean.
        var decoding = Translate("strata-parallel-decode.json").Slots!;
        var longest = decoding.Max(s => s.Generated!.Value);
        Assert.Equal(1.0, Display.SlotFraction(decoding[0], longest));
        Assert.Equal(310.0 / 520, Display.SlotFraction(decoding[3], longest), 6);
        var mean = Display.Combined(decoding);
        Assert.False(mean.IsLimit);
        Assert.Equal(decoding.Average(s => s.Generated!.Value / longest), mean.Fraction, 6);
        // With a limit per slot (a newer engine): generated over limit, summed.
        foreach (var s in decoding) s.MaxTokens = 1000;
        var limited = Display.Combined(decoding);
        Assert.True(limited.IsLimit);
        Assert.Equal((520.0 + 430 + 360 + 310) / 4000, limited.Fraction, 6);
        Assert.Equal(0.52, Display.SlotFraction(decoding[0], longest), 6);
    }

}

public class StrataFlowTests
{
    private const string Router = "http://router:1";
    private const string Metrics = Router + "/upstream/demo-model/metrics";

    private static StrataProvider Via(FakeSource source, string? url = null) =>
        new StrataProvider(new EngineConfig { Router = Router, Url = url }, source);

    [Fact]
    public async Task ReadyModelIsReadThroughTheRouterWithReadOnlyRequestsAndNoModelIsAStateNotAnError()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json").Json(Metrics, "strata-0.1.56-idle.json");
        var s = await Via(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Idle, s!.Status);
        Assert.Equal("Demo Model (4 parallel)", s.Model);   // the router's friendly name (name in its /running), not the id demo-model
        Assert.Equal(new[] { Router + "/running", Metrics }, source.Requested);
        await NoModelLoadedIsAStateNotAnError();
        await OnlyGetRequestsAreMadeToKnownReadOnlyPaths();
    }

    private static async Task NoModelLoadedIsAStateNotAnError()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-empty.json");
        var provider = Via(source);
        var s = await provider.PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.NoModel, s!.Status);
        Assert.Null(provider.LastError);
        Assert.Equal(new[] { Router + "/running" }, source.Requested);   // nothing else is asked, nothing is woken
    }

    [Fact]
    public async Task RouterLimitKeepsTheLastData()
    {
        var limited = false;
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Route(Metrics, () => limited ? new FakeAnswer(429, null, FetchFailure.Status) : new FakeAnswer(200, Fixtures.Text("strata-0.1.56-decode.json")));
        var provider = Via(source);
        var first = await provider.PollAsync(CancellationToken.None);
        Assert.False(first!.RouterLimited);
        limited = true;
        var second = await provider.PollAsync(CancellationToken.None);
        Assert.True(second!.RouterLimited);
        Assert.Equal(EngineStatus.Decode, second.Status);                 // the last data, not a blank
        Assert.Equal(first.Live!.DecodeTokensPerSecond, second.Live!.DecodeTokensPerSecond);
        Assert.False(first.RouterLimited);                                 // the earlier snapshot is not changed
        limited = false;
        Assert.False((await provider.PollAsync(CancellationToken.None))!.RouterLimited);
    }

    [Fact]
    public async Task UnreachableRouterIsNullWithAReasonAndNoGuessingAtLocalhost()
    {
        var source = new FakeSource().Down(Router + "/running");
        var provider = Via(source);
        Assert.Null(await provider.PollAsync(CancellationToken.None));
        Assert.Contains("Connection refused at " + Router, provider.LastError);
        Assert.Equal(new[] { Router + "/running" }, source.Requested);    // 127.0.0.1:8095 is not probed
    }

    private static async Task OnlyGetRequestsAreMadeToKnownReadOnlyPaths()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json").Json(Metrics, "strata-0.1.56-prefill.json");
        await Via(source).PollAsync(CancellationToken.None);
        Assert.All(source.Requested, url => Assert.True(url.EndsWith("/running") || url.EndsWith("/metrics"), url));
    }
}
