using System.IO;
using Stratoscope.Core.Engines;
using Stratoscope.Core.Engines.Basalt;

namespace Stratoscope.Tests;

// Basalt 0.3.2 (CUDA) behind a llama-swap router, recorded read-only with GET requests while a long coding session ran. The recordings are the
// unpatched engine's shape: the video card is only in hardware.gpus[], without the flat gpu_* fields that a patched engine adds.

public class BasaltSnapshotTests
{
    private static BasaltProvider Provider() => new(new EngineConfig(), new FakeSource());

    private static BasaltMetrics Metrics(string fixture) =>
        Json.Deserialize(Fixtures.Text(fixture), Json.BasaltMetricsType) ?? throw new InvalidDataException(fixture);

    private static List<BasaltSlotDto?>? Slots(string fixture) => Json.Deserialize(Fixtures.Text(fixture), Json.BasaltSlotsType);

    [Fact]
    public void CompletedAndActiveCountsRemainSeparateForReconciliation()
    {
        var metrics = Json.Deserialize("""
            {
              "live":{"state":"generating","slots":{"total":2,"active":1}},
              "totals":{"since":100,"requests":4,"written":1234}
            }
            """, Json.BasaltMetricsType)!;
        List<BasaltSlotDto?> slots = [new() { State = "decoding", Generated = 56 }, new() { State = "idle", Generated = 900 }];
        var snapshot = Provider().Translate(metrics, slots, "m");
        Assert.Equal(new OutputProgress(100, 4, 1234, 56), snapshot.OutputProgress);
        Assert.Null(snapshot.Totals);
        Assert.Null(Provider().Translate(metrics, [slots[0]], "m").OutputProgress!.Value.ActiveTokens);
    }

    [Fact]
    public void PrefillShowsTheProgressFromSlotsAndTheEnginesOwnSpeed()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-prefill.json"), Slots("basalt-0.3.2-prefill.slots.json"), "m");
        Assert.Equal("Basalt", s.Engine);
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(2, s.Queued);
        Assert.Equal(56967, s.Live!.PromptTotal);
        Assert.Equal(33771, s.Live.PromptRead);
        Assert.Equal(59, s.Live.Percent);
        Assert.Equal(4706.7, s.Live.PrefillTokensPerSecond);
        Assert.Equal(7.9, s.Live.ElapsedSeconds);
        Assert.Null(s.Live.DecodeTokensPerSecond);          // live.tok_s is null while reading: not 0
        Assert.Equal(32000, s.Live.MaxTokens);
        Assert.Null(s.Live.ReusedEstimate);                 // the engine has not said yet
        Assert.Equal(56967, s.Context.Used);
        Assert.Equal(131072, s.Context.Max);
    }

    [Fact]
    public void PrefillWithoutSlotsHasTheSizeButNoProgress()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-prefill.json"), null, "m");
        Assert.Equal(56967, s.Live!.PromptTotal);
        Assert.Null(s.Live.PromptRead);
        Assert.Null(s.Live.Percent);
    }

    [Fact]
    public void TheRunningRequestComesFromTheSlotWhenMetricsDescribeAnotherOne()
    {
        // At the start of a read /metrics still names the newest request (41,198 tokens); the slot holds the one that is read (56,967).
        var s = Provider().Translate(Metrics("basalt-0.3.2-prefill-start.json"), Slots("basalt-0.3.2-prefill-start.slots.json"), "m");
        Assert.Equal(56967, s.Live!.PromptTotal);
        Assert.Equal(404, s.Live.PromptRead);
        Assert.Null(s.Live.PrefillTokensPerSecond);         // the engine has no mean yet
    }

    [Fact]
    public void KvResidentTokensComeFromTheEnginesOwnKeyAndNoSlotTimingOrTotalsExist()
    {
        // /metrics engine.kv_resident is 32768 in basalt-0.3.2-parallel-idle.json and 0 in basalt-0.3.2-idle.json.
        var parallel = Provider().Translate(Metrics("basalt-0.3.2-parallel-idle.json"), Slots("basalt-0.3.2-parallel-idle.slots.json"), "m");
        Assert.Equal(32768, parallel.Info!.KvResidentTokens);
        Assert.Equal(0, Provider().Translate(Metrics("basalt-0.3.2-idle.json"), null, "m").Info!.KvResidentTokens);
        // The /slots rows carry no per-slot elapsed time and /metrics carries no totals: both stay unknown.
        var decode = Provider().Translate(Metrics("basalt-0.3.2-decode.json"), Slots("basalt-0.3.2-decode.slots.json"), "m");
        Assert.Null(decode.Totals);
    }

    [Fact]
    public void DecodeShowsSpeedGeneratedCountAndTheContextOfTheSlot()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-decode.json"), Slots("basalt-0.3.2-decode.slots.json"), "m");
        Assert.Equal(EngineStatus.Decode, s.Status);
        Assert.Equal(147.2, s.Live!.DecodeTokensPerSecond);
        Assert.Equal(1002, s.Live.Generated);
        Assert.Null(s.Live.PrefillTokensPerSecond);         // Basalt keeps sending the last read's mean while it generates; it is not a speed now
        Assert.Equal(57969, s.Context.Used);                // the slot's prompt plus what it has generated
        Assert.Equal(0, s.Live.ReusedEstimate);
    }

    // Four slots (--parallel 4), recorded while requests of about 20,000 tokens ran together with a long conversation in slot 1.

    [Fact]
    public void SlotsAreListedOneEntryEachWithTheirStateAndNumbers()
    {
        var m = Metrics("basalt-0.3.2-parallel-mixed.json");
        var rows = Slots("basalt-0.3.2-parallel-mixed.slots.json")!;
        var s = Provider().Translate(m, rows, "m");
        Assert.Equal(new[] { "decoding", "decoding", "reading", "idle" }, s.Slots!.Select(x => x.State));
        Assert.Equal(new[] { 0, 1, 2, 3 }, s.Slots!.Select(x => x.Index));
        var reading = s.Slots[2];
        Assert.Equal(rows[2]!.PromptTokens, reading.PromptTokens);
        Assert.Equal(rows[2]!.PrefillDone, reading.PromptRead);
        Assert.Equal(rows[2]!.MaxTokens, reading.MaxTokens);
        Assert.Null(reading.TokensPerSecond);                       // not generating: no speed
        var decoding = s.Slots[1];
        Assert.Equal(rows[1]!.Generated, decoding.Generated);
        Assert.Equal(rows[1]!.TokensPerSecond, decoding.TokensPerSecond);
        Assert.Null(decoding.HeldTokens);
        Assert.Equal(decoding.PromptTokens + decoding.Generated, decoding.Tokens);
        var idle = s.Slots[3];
        Assert.Equal(rows[3]!.CtxUsed, idle.HeldTokens);            // an idle slot says what it still holds
        Assert.False(idle.IsActive);
        Assert.Equal(4, s.Info!.BatchSlots);
    }

    [Fact]
    public void RequestsRunningTogetherGetTheirTotalInTheHeadline()
    {
        var rows = Slots("basalt-0.3.2-parallel-mixed.slots.json")!;
        var m = Metrics("basalt-0.3.2-parallel-mixed.json");
        var s = Provider().Translate(m, rows, "m");
        Assert.Equal(EngineStatus.Decode, s.Status);                // generating while any slot decodes
        Assert.Equal(3, s.Live!.Running);
        Assert.True(s.Live.IsParallel);
        Assert.Equal(rows[0]!.Generated + rows[1]!.Generated, s.Live.Generated);
        Assert.Equal(m.Live!.TokensPerSecond, s.Live.DecodeTokensPerSecond);     // the engine's own total over the slots
        Assert.Null(s.Live.MaxTokens);
        Assert.Equal(rows.Max(r => r!.CtxUsed), s.Context.Used);    // the fullest slot
        Assert.Equal(2, s.Queued);
    }

    [Fact]
    public void ADecodingSlotAndAReadingOneAreBothShown()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-parallel-reading.json"), Slots("basalt-0.3.2-parallel-reading.slots.json"), "m");
        Assert.Equal(new[] { "decoding", "reading", "idle", "idle" }, s.Slots!.Select(x => x.State));
        Assert.Equal(2, s.Live!.Running);
        Assert.True(s.Slots[1].IsReading);
        Assert.True(s.Slots[1].PromptRead < s.Slots[1].PromptTokens);
    }

    [Fact]
    public void SeveralReadingSlotsAddUpTheirPromptsAndSayNothingWhenBasaltSaysNoProgress()
    {
        var m = Json.Deserialize(@"{""engine"":{""max_context"":8192},""live"":{""state"":""reading"",""slots"":{""total"":4,""active"":2,""queue_depth"":0},""prefill_tok_s_mean"":900}}", Json.BasaltMetricsType)!;
        var rows = Json.Deserialize(@"[{""id"":0,""state"":""prefill"",""prompt_tokens"":1000,""prefill_done"":null},{""id"":1,""state"":""prefill"",""prompt_tokens"":3000,""prefill_done"":null},{""id"":2,""state"":""idle"",""ctx_used"":0},{""id"":3,""state"":""idle"",""ctx_used"":0}]", Json.BasaltSlotsType)!;
        var s = Provider().Translate(m, rows, "m");
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(4000, s.Live!.PromptTotal);
        Assert.Null(s.Live.PromptRead);                             // never 0: the engine does not say how far each has read
        Assert.Null(s.Live.Percent);
        Assert.Equal(900, s.Live.PrefillTokensPerSecond);
        Assert.Equal(0, s.Slots![2].HeldTokens);
    }

    [Fact]
    public void ASingleSlotHasNoSlotList()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-decode.json"), Slots("basalt-0.3.2-decode.slots.json"), "m");
        Assert.Null(s.Slots);
        Assert.Null(s.Live!.Running);
    }

    [Fact]
    public void IdleWithFourSlotsListsWhatEachHolds()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-parallel-idle.json"), Slots("basalt-0.3.2-parallel-idle.slots.json"), "m");
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Null(s.Live);
        Assert.Equal(4, s.Slots!.Count);
        Assert.All(s.Slots, x => { Assert.Equal("idle", x.State); Assert.NotNull(x.HeldTokens); });
    }

    [Fact]
    public void TheEngineNameAndVersionComeFromTheEngineAndAreHiddenWhenUnknown()
    {
        // The Engine block of the panel draws Engine + " " + Info.Version, and nothing when the version is null.
        var basalt = Provider().Translate(Metrics("basalt-0.3.2-idle.json"), null, "m");
        Assert.Equal("Basalt 0.3.2", basalt.Engine + " " + basalt.Info!.Version);
        var strata = new Stratoscope.Core.Engines.Strata.StrataProvider(new EngineConfig(), new FakeSource()).Translate(Fixtures.Metrics("strata-0.1.56-idle.json"), "m");
        Assert.Equal("Strata 0.1.56", strata.Engine + " " + strata.Info!.Version);
        var unknown = Json.Deserialize(@"{""engine"":{""max_context"":8192},""live"":{""state"":""idle""}}", Json.BasaltMetricsType)!;
        Assert.Null(Provider().Translate(unknown, null, "m").Info!.Version);
    }

    [Fact]
    public void IdleHasNoRunningRequestAndTheContextOfTheNewestFinishedOne()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-idle.json"), Slots("basalt-0.3.2-idle.slots.json"), "m");
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Equal(0, s.Queued);
        Assert.Null(s.Live);
        Assert.NotEmpty(s.Recent);
        Assert.Equal(s.Recent[0].PromptTokens + s.Recent[0].OutputTokens, s.Context.Used);
        Assert.Equal(131072, s.Context.Max);
        Assert.NotNull(s.System!.GpuMemoryTotalBytes);
    }

    [Fact]
    public void SystemIsReadFromTheNestedVideoCardOfTheUnpatchedEngine()
    {
        var m = Metrics("basalt-0.3.2-decode.json");
        Assert.DoesNotContain("gpu_util", Fixtures.Text("basalt-0.3.2-decode.json"));     // the premise: no flat fields
        var system = Provider().Translate(m, null, "m").System!;
        var gpu = m.Hardware!.Gpus![0]!;
        Assert.Equal(gpu.Util, system.GpuPercent);
        Assert.Equal(gpu.Temp, system.GpuTemperatureCelsius);
        Assert.Equal(gpu.Power, system.GpuPowerWatts);
        Assert.Equal(gpu.PowerLimit, system.GpuPowerLimitWatts);
        Assert.Equal(gpu.MemUsed, system.GpuMemoryUsedBytes);
        Assert.Equal(gpu.MemTotal, system.GpuMemoryTotalBytes);
        Assert.Equal(m.Hardware.RamUsed, system.RamUsedBytes);
        Assert.Equal(m.Hardware.RamTotal, system.RamTotalBytes);
        Assert.Equal(m.Hardware.Cpu, system.CpuPercent);
        Assert.NotNull(system.GpuPercent);
    }

    [Fact]
    public void EngineDetailsAreTheEnginesOwnAndSectionsItDoesNotHaveAreNotDrawn()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-decode.json"), null, "m");
        Assert.Equal("0.3.2", s.Info!.Version);
        Assert.Equal("int8", s.Info.KvFormat);
        Assert.Equal("8", s.Info.DraftWindow);
        Assert.Equal(17086, s.Info.ExpertCacheMib);
        Assert.Equal(1, s.Info.BatchSlots);
        Assert.Equal(0.92, s.Info.ExpertHitRate);            // the newest request's expert cache hit rate: an engine detail, not the cache dots
        Assert.Null(s.Cache);                                // conversation_cache_mib is 0: the cache is switched off
        Assert.Null(s.Totals);
        Assert.Null(s.Slots);
    }

    [Fact]
    public void RequestRowsCarryTheCacheVerdictFromPromptAndReusedNotFromTheExpertHitRate()
    {
        var s = Provider().Translate(Metrics("basalt-0.3.2-decode.json"), null, "m");
        Assert.Equal(12, s.Recent.Count);
        var partial = s.Recent.Single(r => r.PromptTokens == 38964);       // 19,435 of 38,964 came from the conversation cache
        Assert.Equal(19435, partial.ReusedTokens);
        Assert.Equal(CacheHit.Partial, partial.Hit);
        Assert.Equal(19529, partial.PromptRead);
        var miss = s.Recent.Single(r => r.PromptTokens == 90494);          // nothing reused
        Assert.Equal(0, miss.ReusedTokens);
        Assert.Equal(CacheHit.Miss, miss.Hit);
        Assert.Equal(CacheHit.Small, s.Recent.Single(r => r.PromptTokens == 53).Hit);
        Assert.All(s.Recent, r => Assert.NotNull(r.FinishedAt));
        Assert.Equal(s.Recent[0].ExpertHitRate, s.Info!.ExpertHitRate);
    }

    [Theory]
    [InlineData(38964, 19435, CacheHit.Partial)]
    [InlineData(90494, 0, CacheHit.Miss)]
    [InlineData(40000, 38000, CacheHit.Hit)]
    public void TheRunningRequestsBarGetsItsVerdictFromTheSlotsReusedCount(int prompt, int reused, CacheHit expected)
    {
        var slots = Slots("basalt-0.3.2-prefill.slots.json")!;
        slots[0] = slots[0]! with { PromptTokens = prompt, Reused = reused };      // as the engine reports it once it knows the reused part
        var s = Provider().Translate(Metrics("basalt-0.3.2-prefill.json"), slots, "m");
        Assert.Equal(expected, CacheHits.Classify(s.Live!.PromptTotal, s.Live.ReusedEstimate));
        Assert.Equal(expected, Display.Progress(s)!.Value.Hit);
    }

    [Fact]
    public void ARequestWithoutReusedGetsNoVerdict()
    {
        var m = Json.Deserialize(@"{""engine"":{""max_context"":8192},""live"":{""state"":""idle""},""requests"":[{""prompt_tokens"":9000,""output_tokens"":5}]}", Json.BasaltMetricsType)!;
        var s = Provider().Translate(m, null, "m");
        Assert.Null(s.Recent[0].Hit);
        Assert.Null(s.Recent[0].PromptRead);
    }

    [Fact]
    public void ConversationCacheIsDrawnOnlyWhenItHasABudget()
    {
        BasaltMetrics With(string engine) => Json.Deserialize(@"{""engine"":" + engine + @",""live"":{""state"":""idle""}}", Json.BasaltMetricsType)!;
        var on = Provider().Translate(With(@"{""conversation_cache_mib"":8192,""conversation_cache_slots"":4}"), null, "m").Cache!;
        Assert.Equal(8192.0 * 1_048_576, on.BudgetBytes);
        Assert.Equal(4, on.Slots);
        Assert.Null(on.UsedBytes);                          // Basalt does not say what is parked
        Assert.Null(on.Parked);
        Assert.False(on.EntriesKnown);
        Assert.Null(Provider().Translate(With(@"{""conversation_cache_mib"":0,""conversation_cache_slots"":4}"), null, "m").Cache);
        Assert.Null(Provider().Translate(With("{}"), null, "m").Cache);
    }

    [Fact]
    public void ParallelRequestsAreCountedOnlyWithMoreThanOneSlot()
    {
        BasaltMetrics With(string slots) => Json.Deserialize(@"{""engine"":{},""live"":{""state"":""generating"",""slots"":" + slots + @",""tok_s"":50}}", Json.BasaltMetricsType)!;
        Assert.Equal(2, Provider().Translate(With(@"{""total"":4,""active"":2,""queue_depth"":1}"), null, "m").Live!.Running);
        Assert.Null(Provider().Translate(With(@"{""total"":1,""active"":1,""queue_depth"":0}"), null, "m").Live!.Running);
        Assert.Equal(1, Provider().Translate(With(@"{""total"":4,""active"":2,""queue_depth"":1}"), null, "m").Queued);   // queue_depth stands in for queued
    }

    [Theory]
    [InlineData(@"{""state"":""loading"",""load"":{""kind"":""weights"",""percent"":37.4}}", "Loading weights 37 %")]
    [InlineData(@"{""state"":""loading"",""load"":{""kind"":""wake""}}", "Waking up")]
    [InlineData(@"{""state"":""loading""}", "Loading weights")]
    [InlineData(@"{""state"":""asleep""}", "Asleep")]
    [InlineData(@"{""state"":""unloaded""}", "Weights not loaded")]
    [InlineData(@"{}", "State not reported")]
    public void AnEngineThatCannotTakeARequestIsLoadingWithItsOwnReason(string live, string reason)
    {
        var m = Json.Deserialize(@"{""engine"":{""max_context"":8192},""live"":" + live + "}", Json.BasaltMetricsType)!;
        var s = Provider().Translate(m, null, "m");
        Assert.Equal(EngineStatus.Loading, s.Status);
        Assert.Equal(reason, s.LoadingReason);
    }

    [Fact]
    public void MissingFieldsAreUnknownNotZero()
    {
        var m = Json.Deserialize(@"{""engine"":{},""live"":{""state"":""reading""},""hardware"":{""gpus"":[null,{""util"":""high""}]}}", Json.BasaltMetricsType)!;
        var s = Provider().Translate(m, null, "m");
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Null(s.Live!.PromptTotal);
        Assert.Null(s.Live.Percent);
        Assert.Null(s.Context.Used);
        Assert.Null(s.Context.Max);
        Assert.Null(s.Info);
        Assert.Null(s.System!.GpuPercent);                  // "high" is not a number
        Assert.Empty(s.Recent);
    }
}

public class BasaltFlowTests
{
    private const string Router = "http://router:1";
    private const string Upstream = Router + "/upstream/demo-model";

    private static FakeSource Basalt(string state = "prefill") =>
        new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Json(Upstream + "/health", "basalt-0.3.2-" + state + ".health.json")
            .Json(Upstream + "/metrics", "basalt-0.3.2-" + state + ".json")
            .Json(Upstream + "/slots", "basalt-0.3.2-" + state + ".slots.json");

    private static AutoEngineProvider Auto(FakeSource source, string? url = null) => new(new EngineConfig { Router = Router, Url = url }, source);

    [Fact]
    public async Task ABasaltBehindTheRouterIsRecognisedByItsHealthAndReadWithGetRequestsOnly()
    {
        var source = Basalt();
        var provider = Auto(source);
        var s = await provider.PollAsync(CancellationToken.None);
        Assert.Equal("Basalt", s!.Engine);
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal("Demo Model (4 parallel)", s.Model);   // the router's friendly name
        Assert.Equal(59, s.Live!.Percent);
        Assert.Equal(new[] { Router + "/running", Upstream + "/health", Upstream + "/metrics", Upstream + "/slots" }, source.Requested);
        Assert.Contains(s.Links, l => l.Title == "Open Basalt UI" && l.Target == Upstream + "/");

        source.Requested.Clear();
        await provider.PollAsync(CancellationToken.None);
        Assert.Equal(new[] { Router + "/running", Upstream + "/metrics", Upstream + "/slots" }, source.Requested);   // the kind is asked once
    }

    [Fact]
    public async Task SlotsAreOnlyAskedWhileARequestRuns()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Json(Upstream + "/health", "basalt-0.3.2-decode.health.json")
            .Text(Upstream + "/metrics", @"{""engine"":{""max_context"":8192},""live"":{""state"":""idle""}}");
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Idle, s!.Status);
        Assert.DoesNotContain(Upstream + "/slots", source.Requested);
    }

    [Fact]
    public async Task AnIdleEngineWithSeveralSlotsIsAskedForItsSlots()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Json(Upstream + "/health", "basalt-0.3.2-decode.health.json")
            .Json(Upstream + "/metrics", "basalt-0.3.2-parallel-idle.json").Json(Upstream + "/slots", "basalt-0.3.2-parallel-idle.slots.json");
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Idle, s!.Status);
        Assert.Equal(4, s.Slots!.Count);
        Assert.Contains(Upstream + "/slots", source.Requested);
    }

    [Fact]
    public async Task SlotsThatCannotBeReadLeaveTheProgressUnknownAndTheRestShown()
    {
        var source = Basalt().Status(Upstream + "/slots", 429);
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Prefill, s!.Status);
        Assert.False(s.RouterLimited);
        Assert.Null(s.Live!.PromptRead);
        Assert.Equal(56967, s.Live.PromptTotal);
    }

    [Fact]
    public async Task AStrataBehindTheSameRouterIsStillReadAsStrata()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Text(Upstream + "/health", @"{""status"":""ok"",""service"":""strata""}")
            .Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal("Strata", s!.Engine);
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Equal(new[] { Router + "/running", Upstream + "/health", Upstream + "/metrics" }, source.Requested);
    }

    [Fact]
    public async Task AnEngineWithoutHealthIsReadAsStrata()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json").Status(Upstream + "/health", 404)
            .Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        Assert.Equal("Strata", (await Auto(source).PollAsync(CancellationToken.None))!.Engine);
    }

    [Fact]
    public async Task RouterLimitKeepsTheLastBasaltData()
    {
        var limited = false;
        var source = Basalt().Route(Upstream + "/metrics", () => limited ? new FakeAnswer(429, null, FetchFailure.Status) : new FakeAnswer(200, Fixtures.Text("basalt-0.3.2-prefill.json")));
        var provider = Auto(source);
        Assert.False((await provider.PollAsync(CancellationToken.None))!.RouterLimited);
        limited = true;
        var second = await provider.PollAsync(CancellationToken.None);
        Assert.True(second!.RouterLimited);
        Assert.Equal("Basalt", second.Engine);
    }

    [Fact]
    public async Task ABasaltThatStopsAnsweringIsAnErrorStateAndIsAskedAgainWhichKindItIs()
    {
        var up = true;
        var source = Basalt().Route(Upstream + "/metrics", () => up ? new FakeAnswer(200, Fixtures.Text("basalt-0.3.2-prefill.json")) : new FakeAnswer(0, null, FetchFailure.Refused));
        var provider = Auto(source);
        await provider.PollAsync(CancellationToken.None);
        up = false;
        Assert.Equal(EngineStatus.Error, (await provider.PollAsync(CancellationToken.None))!.Status);
        up = true;
        source.Requested.Clear();
        await provider.PollAsync(CancellationToken.None);
        Assert.Contains(Upstream + "/health", source.Requested);
    }

    [Fact]
    public async Task WithoutARouterBasaltIsReadDirectlyAndNamedByItsModel()
    {
        const string address = "http://127.0.0.1:8095";
        var source = new FakeSource().Json(address + "/health", "basalt-0.3.2-decode.health.json")
            .Json(address + "/metrics", "basalt-0.3.2-decode.json").Json(address + "/slots", "basalt-0.3.2-decode.slots.json");
        var s = await new AutoEngineProvider(new EngineConfig { Url = address }, source).PollAsync(CancellationToken.None);
        Assert.Equal("Basalt", s!.Engine);
        Assert.Equal("qwen3.8-flash-next-gsq-rco-coder", s.Model);
        Assert.Equal(EngineStatus.Decode, s.Status);
    }

    [Fact]
    public async Task ADocumentThatIsNoEnginesIsNotARunningBasalt()
    {
        const string address = "http://127.0.0.1:8095";
        var source = new FakeSource().Text(address + "/metrics", "{}");
        var provider = new BasaltProvider(new EngineConfig { Url = address }, source);
        Assert.Null(await provider.PollAsync(CancellationToken.None));
        Assert.Contains("not a Basalt metrics document", provider.LastError);
    }

    [Fact]
    public void TheRegistryChoosesTheProviderFromTheEngineSetting()
    {
        var source = new FakeSource();
        Assert.IsType<BasaltProvider>(EngineRegistry.Make(new EngineConfig { Engine = "basalt" }, source));
        Assert.IsType<AutoEngineProvider>(EngineRegistry.Make(new EngineConfig { Engine = "strata" }, source));
        Assert.Contains("basalt", EngineRegistry.Available);
    }

    [Fact]
    public async Task ServerDetectionFindsABasaltServerByItsMetrics()
    {
        const string address = "http://127.0.0.1:8095";
        var source = new FakeSource().Json(address + "/metrics", "basalt-0.3.2-decode.json");
        var found = await ServerDetection.FindAsync(source, null, CancellationToken.None);
        Assert.Contains(found, e => e.Url == address && e.Router == null);
    }
}
