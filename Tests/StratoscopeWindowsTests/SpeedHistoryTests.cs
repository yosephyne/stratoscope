using Stratoscope.Core;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

public class SpeedScaleTests
{
    [Theory]
    [InlineData(5.1, 5.1)] [InlineData(12, 12)] [InlineData(12.01, 13)]
    [InlineData(99, 99)] [InlineData(101, 110)] [InlineData(126, 130)] [InlineData(283, 290)] [InlineData(1260, 1300)] [InlineData(12600, 13000)]
    public void TheMaximumHasTwoSignificantDigits(double value, double expected) => Assert.Equal(expected, SpeedScale.RoundUp(value), 9);

    [Fact]
    public void ARiseAddsAMarginAndRoundsUpAtOnce()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(130, scale.Observe(120, 0));       // 120 + 5 % = 126, up to two significant digits
        Assert.Equal(150, scale.Observe(135, 1));       // 141.75
        Assert.Equal(150, scale.Max);
    }

    [Fact]
    public void TheMaximumDoesNotMoveWhileValuesStayBelowIt()
    {
        var scale = new SpeedScale(100);
        for (int i = 0; i < 100; i++) Assert.Equal(100, scale.Observe(60 + (i % 7) * 5, i));
        scale.Observe(150, 100);                        // up to 160
        foreach (var v in new[] { 151.0, 155, 159, 160, 152 }) Assert.Equal(160, scale.Observe(v, 101));      // a creeping speed does not move it
    }

    [Fact]
    public void TheMaximumHoldsThenFallsToJustAboveThePeak()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);
        Assert.Equal(420, scale.Max);                    // 400 + 5 %
        // Everything below half of 420 (210) for less than the hold time: nothing changes.
        for (double t = 1; t < SpeedScale.FallHoldSeconds; t += 1) Assert.Equal(420, scale.Observe(100, t));
        // At the hold time: just above the peak of the period (100 + 5 % = 105, up to 110).
        Assert.Equal(110, scale.Observe(100, SpeedScale.FallHoldSeconds + 1));
        // Idle for another period: down to the floor, not below.
        double now = SpeedScale.FallHoldSeconds + 1;
        for (int i = 0; i < 100; i++) scale.Observe(0, now += 1);
        Assert.Equal(20, scale.Max);
    }

    [Fact]
    public void AValueAtHalfOfTheMaximumRestartsTheHoldTime()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);                                   // 420
        for (double t = 1; t < 20; t++) scale.Observe(100, t);
        scale.Observe(220, 20);                                  // at least half: the period starts over
        for (double t = 21; t < 20 + SpeedScale.FallHoldSeconds; t++) Assert.Equal(420, scale.Observe(100, t));
        Assert.True(scale.Observe(100, 20 + SpeedScale.FallHoldSeconds + 1) < 420);
    }

    [Fact]
    public void ASteppedDownScaleDoesNotOscillate()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);
        double now = 0;
        // A steady 100 t/s for two minutes: one step down, then it stays (100 is above half of the new maximum, and below it).
        var seen = new HashSet<double>();
        for (int i = 0; i < 120; i++) seen.Add(scale.Observe(100, now += 1));
        Assert.Equal(new HashSet<double> { 420, 110 }, seen);
    }

    [Fact]
    public void ASpeedThatSwingsAroundTheMaximumNeverMakesTheScaleChangePerSample()
    {
        var scale = new SpeedScale(100);
        scale.Observe(120, 0);                           // the first rise, to 130
        int changes = 0; double last = scale.Max;
        for (int i = 0; i < 600; i++)
        {
            var v = 90 + 30 * Math.Sin(i * 0.9);        // 60 to 120: around the maximum, never above it
            var max = scale.Observe(v, i + 1);
            if (max != last) { changes++; last = max; }
        }
        Assert.Equal(0, changes);                        // no rise, and no fall: the swing keeps returning above half of the maximum
    }

    [Fact]
    public void UnknownValuesAreIgnored()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(100, scale.Observe(double.NaN, 0));
        Assert.Equal(100, scale.Observe(double.PositiveInfinity, 1));
        Assert.Equal(100, scale.Observe(-5, 2));
        Assert.Equal(0, scale.Fraction(double.NaN));
    }

    [Fact]
    public void TheFractionIsTheShareOfTheScale()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(0.25, scale.Fraction(25));
        Assert.Equal(1, scale.Fraction(500));
        Assert.Equal(0, scale.Min);
    }

    [Fact]
    public void TheChartScalesOfTheSpeedSectionFollowTheSameRule()
    {
        var h = new SpeedHistory();
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Idle);
        s.Recent.Add(new RequestStats { DecodeTokensPerSecond = 30, PromptRead = 20_000, PromptMilliseconds = 40_000 });     // prefill 500 t/s
        h.Add(DateTime.UnixEpoch.AddSeconds(1000), s);
        Assert.Equal(32, h.DecodeChartScale.Max);        // 30 + 5 % = 31.5 up from 10
        Assert.Equal(530, h.PrefillChartScale.Max);      // 500 + 5 % = 525 up from 100
        s.Recent[0].DecodeTokensPerSecond = 28;
        h.Add(DateTime.UnixEpoch.AddSeconds(1002), s);
        Assert.Equal(32, h.DecodeChartScale.Max);        // not changed by the next poll
    }
}

public class PhaseClockTests
{
    private static DateTime At(double seconds) => DateTime.UnixEpoch.AddSeconds(1_000_000 + seconds);
    /// <summary>Counters with phase clocks: prompt tokens, generated tokens, prefill seconds, decode seconds, decode-step tokens.</summary>
    private static EngineCounters C(double prefill, double generated, double prefillS, double decodeS, double decodeTokens) =>
        new EngineCounters(prefill, generated, prefillS, decodeS, decodeTokens);

    [Fact]
    public void ThePhaseSpeedIsTokensOverTheTimeSpentOnThePhaseNotTheReadInterval()
    {
        var h = new SpeedHistory();
        Assert.False(h.AddCounters(At(0), C(1000, 100, 2.0, 4.0, 90)));             // baseline
        // In the next second: 800 prompt tokens in 0.1 s of prefill, 41 tokens (40 from decode steps, 1 first token) in 0.5 s of decode.
        Assert.True(h.AddCounters(At(1.0), C(1800, 141, 2.1, 4.5, 130)));
        Assert.Equal(8000, h.Latest(HistoryCurve.Prefill), 6);                     // not 800 per wall second
        Assert.Equal(80, h.Latest(HistoryCurve.Decode), 6);                        // not 41
        Assert.Equal(841, h.Latest(HistoryCurve.Total), 6);                        // the throughput stays per wall second
    }

    [Fact]
    public void ThePhaseSpeedIsHeldBetweenPublicationsThenZero()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), C(0, 0, 0, 0, 0));
        h.AddCounters(At(0.5), C(500, 0, 0.05, 0, 0));                              // 10,000 t/s
        h.AddCounters(At(1.0), C(500, 0, 0.05, 0, 0));                              // not published again yet: held
        Assert.Equal(10_000, h.Latest(HistoryCurve.Prefill), 6);
        h.AddCounters(At(3.0), C(500, 0, 0.05, 0, 0));                              // the phase stopped
        Assert.Equal(0, h.Latest(HistoryCurve.Prefill));
        Assert.Equal(0, h.Latest(HistoryCurve.Decode));
    }

    [Fact]
    public void OlderEnginesWithoutPhaseClocksKeepTheWallTimeRates()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), new EngineCounters(1000, 100));
        h.AddCounters(At(1.0), new EngineCounters(1800, 140));
        Assert.Equal(800, h.Latest(HistoryCurve.Prefill), 6);
        Assert.Equal(40, h.Latest(HistoryCurve.Decode), 6);
        // The read that first brings the clocks is their baseline: still the wall rates, phase speeds from the next one.
        h.AddCounters(At(2.0), C(2600, 180, 3.0, 5.0, 170));
        Assert.Equal(800, h.Latest(HistoryCurve.Prefill), 6);
        h.AddCounters(At(3.0), C(3400, 220, 3.2, 5.8, 210));
        Assert.Equal(4000, h.Latest(HistoryCurve.Prefill), 6);
        Assert.Equal(50, h.Latest(HistoryCurve.Decode), 6);
    }

    [Fact]
    public void ARestartIsAGap()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), C(1000, 100, 2.0, 4.0, 90));
        Assert.True(h.AddCounters(At(0.5), C(10, 2, 0.01, 0.02, 1)));
        Assert.True(double.IsNaN(h.Latest(HistoryCurve.Prefill)));
        h.AddCounters(At(1.0), C(410, 4, 0.06, 0.06, 3));
        Assert.Equal(8000, h.Latest(HistoryCurve.Prefill), 6);
        Assert.Equal(50, h.Latest(HistoryCurve.Decode), 6);
    }
}

public class CounterRateTests
{
    private static DateTime At(double seconds) => DateTime.UnixEpoch.AddSeconds(1_000_000 + seconds);
    private static EngineCounters C(double? prefill, double? generated) => new EngineCounters(prefill, generated);

    [Fact]
    public void TwoSecondRouterPollsUseWallTimeForTheTotalCurveAndLegend()
    {
        var h = new SpeedHistory();
        for (int t = 0; t <= 124; t += 2)
        {
            var active = Math.Min(t, 64);
            h.Add(At(t), new EngineSnapshot("Custom", "m", EngineStatus.Decode)
            {
                HistorySource = "route/m",
                FullPollCounters = new EngineCounters(active * 500, active * 1000, active / 2.0,
                    active / 2.0, active * 1000),
            });
            if (t == 64) Assert.Equal(1500, h.Latest(HistoryCurve.Total), 9);
        }
        var end = SpeedHistory.Seconds(At(124));
        Assert.Equal(500, h.Average(HistoryCurve.Decode, end), 9);
        Assert.Equal(750, h.Average(HistoryCurve.Total, end), 9);
    }

    [Fact]
    public void TheWindowUsesTheObservedBoundaryAndResetsOnAChangeOfEngine()
    {
        var h = new SpeedHistory();
        EngineSnapshot Snap(string source, double tokens) => new("Custom", source, EngineStatus.Decode)
        {
            HistorySource = source,
            FullPollCounters = C(tokens, tokens * 2),
        };
        for (int t = 0; t <= 120; t += 3) h.Add(At(t), Snap("old", t * 100));
        var end = SpeedHistory.Seconds(At(121));
        h.Add(At(121), Snap("old", 12_300));
        Assert.Equal(24_600d / 121, h.Average(HistoryCurve.Decode, end), 9);
        Assert.Equal(36_900d / 121, h.Average(HistoryCurve.Total, end), 9);
        h.Add(At(122), Snap("new", 45_000));
        Assert.True(double.IsNaN(h.Average(HistoryCurve.Decode, end + 1)));
        Assert.True(double.IsNaN(h.Average(HistoryCurve.Total, end + 1)));
    }

    [Theory]
    [InlineData(0, 121)]   // restarted counters
    [InlineData(200, 160)] // a long gap
    public void ARestartOrLongGapRequiresAFreshWindow(int tokens, int seconds)
    {
        var h = new SpeedHistory();
        for (int t = 0; t <= 120; t += 2) h.AddCounters(At(t), C(t, t));
        h.AddCounters(At(seconds), C(tokens, tokens));
        Assert.True(double.IsNaN(h.Average(HistoryCurve.Total, SpeedHistory.Seconds(At(seconds)))));
        for (int t = seconds + 2; t <= seconds + 120; t += 2) h.AddCounters(At(t), C(tokens, tokens));
        Assert.Equal(0, h.Average(HistoryCurve.Total, SpeedHistory.Seconds(At(seconds + 120))));
    }

    [Fact]
    public void PrefillAndDecodeComeFromTheCounterDeltasAndTotalIsTheirSum()
    {
        var h = new SpeedHistory();
        Assert.False(h.AddCounters(At(0), C(1000, 5000)));                // the first read is only the baseline
        Assert.True(h.AddCounters(At(0.5), C(2200, 5100)));                // +1200 prompt tokens, +100 generated in half a second
        Assert.Equal(2400, h.Latest(HistoryCurve.Prefill), 9);
        Assert.Equal(200, h.Latest(HistoryCurve.Decode), 9);
        Assert.Equal(2600, h.Latest(HistoryCurve.Total), 9);               // prefill plus decode, both counted
    }

    [Fact]
    public void WhileNothingRunsTheCountersStandStillAndTheRatesAreZero()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), C(10, 20));
        h.AddCounters(At(0.5), C(10, 20));
        Assert.Equal(0, h.Latest(HistoryCurve.Total));
    }

    [Fact]
    public void ACounterThatFallsIsAGapAndTheNextReadIsTheBaselineAgain()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), C(1000, 5000));
        Assert.True(h.AddCounters(At(0.5), C(20, 30)));                    // the engine restarted
        Assert.True(double.IsNaN(h.Latest(HistoryCurve.Total)));
        Assert.True(h.AddCounters(At(1.0), C(120, 130)));                  // 100 and 100 in half a second
        Assert.Equal(200, h.Latest(HistoryCurve.Prefill), 9);
        Assert.Equal(200, h.Latest(HistoryCurve.Decode), 9);
    }

    [Fact]
    public void MissingCountersAddNothingAndALongGapRestartsTheBaseline()
    {
        var h = new SpeedHistory();
        Assert.False(h.AddCounters(At(0), null));
        Assert.False(h.AddCounters(At(0), C(1, null)));
        Assert.False(h.AddCounters(At(0), C(null, 1)));
        h.AddCounters(At(1), C(100, 100));
        Assert.False(h.AddCounters(At(100), C(900, 900)));                 // 99 s later: no rate over such a gap
        Assert.Equal(0, h.Count);
        Assert.True(h.AddCounters(At(100.5), C(950, 960)));
        Assert.Equal(100, h.Latest(HistoryCurve.Prefill), 9);
    }

    // The custom 27B engine publishes q27:generation_tokens_total once a second, about 100 tokens at a time (recorded 2026-11-14: steps of
    // 112, 95, 115, ... between reads half a second apart, in between 0). Two neighbouring reads then differ by 0 and 200 in turn.
    private static double PublishedOncePerSecond(double readSeconds) => 100 * Math.Floor(readSeconds - 0.13) + 100;

    [Fact]
    public void ACounterPublishedOncePerSecondGivesASteadyRateNotAPingPong()
    {
        var h = new SpeedHistory();
        double[] jitter = { 0, 0.04, -0.03, 0.05, -0.02, 0.01, 0.03, -0.04 };         // the reads do not come exactly every half second
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < 80; i++)
        {
            var t = 0.5 * i + jitter[i % jitter.Length];
            h.AddCounters(At(t), C(0, PublishedOncePerSecond(t)));
            if (t > 5) { var v = h.Latest(HistoryCurve.Decode); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        }
        Assert.InRange(lo, 85, 115);      // the neighbour deltas gave 0 and 200
        Assert.InRange(hi, 85, 115);
    }

    [Fact]
    public void TheRecordedEngineCountersGiveNoPingPong()
    {
        // 80 real reads of the custom engine's counters (see the note in the file): the generation counter rises in steps once a second.
        var reads = System.Text.Json.JsonDocument.Parse(Fixtures.Text("custom/custom-counters-steps-per-second.json")).RootElement.GetProperty("reads");
        var h = new SpeedHistory();
        var rates = new List<double>(); var neighbour = new List<double>();
        double? previous = null;
        foreach (var r in reads.EnumerateArray())
        {
            var t = r.GetProperty("t").GetDouble(); var g = r.GetProperty("generated").GetDouble();
            h.AddCounters(At(t), C(r.GetProperty("prefill").GetDouble(), g));
            if (t > 10) rates.Add(h.Latest(HistoryCurve.Decode));
            if (previous is double p && t > 10) neighbour.Add((g - p) / 0.5);
            previous = g;
        }
        double Spread(List<double> v) { var m = v.Average(); return Math.Sqrt(v.Average(x => (x - m) * (x - m))); }
        Assert.True(Spread(rates) < Spread(neighbour) / 2, $"spread {Spread(rates):0.0} against {Spread(neighbour):0.0} of the neighbour deltas");
        Assert.True(rates.Max() < 150, $"largest {rates.Max():0.0}");
    }

    [Fact]
    public void ASteadilyRisingCounterKeepsTheShortWindow()
    {
        var h = new SpeedHistory();
        for (int i = 0; i < 20; i++) h.AddCounters(At(0.5 * i), C(0, 50.0 * i));        // 100 t/s, a rise at every read
        Assert.Equal(100, h.Latest(HistoryCurve.Decode), 6);
    }

    [Fact]
    public void WhenTheCounterStopsTheRateFallsToZeroWithinTheHold()
    {
        var h = new SpeedHistory();
        for (int i = 0; i < 20; i++) h.AddCounters(At(0.5 * i), C(0, 100.0 * Math.Floor(0.5 * i)));
        Assert.True(h.Latest(HistoryCurve.Decode) > 50);
        for (int i = 20; i < 28; i++) h.AddCounters(At(0.5 * i), C(0, 100.0 * 9));       // nothing more is published
        Assert.Equal(0, h.Latest(HistoryCurve.Decode));
    }

    [Fact]
    public void AStepAfterALongQuietTimeIsOneBurstNotARateOverTheQuietTime()
    {
        var h = new SpeedHistory();
        for (int i = 0; i <= 40; i++) h.AddCounters(At(0.5 * i), C(0, 0));
        h.AddCounters(At(20.5), C(0, 100));
        Assert.Equal(100 / CounterRate.BurstSeconds, h.Latest(HistoryCurve.Decode), 6);
        h.AddCounters(At(22.0), C(0, 100));
        Assert.Equal(100 / CounterRate.BurstSeconds, h.Latest(HistoryCurve.Decode), 6);     // still the burst's 1.5 s
        h.AddCounters(At(22.5), C(0, 100));
        Assert.Equal(0, h.Latest(HistoryCurve.Decode));                                     // then gone: 100 tokens were shown as 100 tokens
    }

    [Fact]
    public void TheFullPollLeavesTheCurvesToTheCountersWhileTheyAreFresh()
    {
        var h = new SpeedHistory();
        h.AddCounters(At(0), C(0, 0));
        h.AddCounters(At(0.5), C(500, 100));
        var before = h.Count;
        h.Add(At(1.0), new EngineSnapshot("Custom", "m", EngineStatus.Idle));      // a full poll one second later adds no sample of its own
        Assert.Equal(before, h.Count);
        h.Add(At(10), new EngineSnapshot("Custom", "m", EngineStatus.Idle));       // counters stale (9.5 s): the full poll feeds the curves again
        Assert.Equal(before + 1, h.Count);
    }

    [Fact]
    public void TheFullPollUsesTheCountersOfItsSnapshotBetweenPolls()
    {
        var h = new SpeedHistory();
        EngineSnapshot Snap(double prefill, double generated) => new EngineSnapshot("Strata", "m", EngineStatus.Decode)
        {
            Live = new LiveRequest { Running = 3 },            // parallel, and the headline phase is writing: the live speeds say nothing about prefill
            Totals = new Totals { PrefillComputedTokens = prefill, OutputTokens = generated },
        };
        h.Add(At(0), Snap(100, 1000));
        h.Add(At(2), Snap(500, 1400));                           // 200 prompt tokens/s and 200 generated tokens/s over 2 s
        Assert.Equal(200, h.Latest(HistoryCurve.Prefill), 9);
        Assert.Equal(200, h.Latest(HistoryCurve.Decode), 9);
        Assert.Equal(400, h.Latest(HistoryCurve.Total), 9);
    }

    [Fact]
    public void WithoutCountersTheSlotsGiveTheRatesAndTheLiveRequestIsTheLastFallback()
    {
        var h = new SpeedHistory();
        EngineSnapshot Snap(double read, double tps) => new EngineSnapshot("Basalt", "m", EngineStatus.Decode)
        {
            Live = new LiveRequest { Running = 2 },
            Slots = new List<BatchSlot>
            {
                new BatchSlot { Index = 0, State = "decoding", TokensPerSecond = tps },
                new BatchSlot { Index = 1, State = "reading", PromptTokens = 10_000, PromptRead = read },
            },
        };
        h.Add(At(0), Snap(1000, 30));
        h.Add(At(2), Snap(1600, 32));                            // the reading slot moved 600 tokens in 2 s
        Assert.Equal(300, h.Latest(HistoryCurve.Prefill), 9);
        Assert.Equal(32, h.Latest(HistoryCurve.Decode), 9);
        Assert.Equal(332, h.Latest(HistoryCurve.Total), 9);

        var plain = new SpeedHistory();                          // no counters, no slots: the live speeds
        plain.Add(At(0), new EngineSnapshot("Strata", "m", EngineStatus.Decode) { Live = new LiveRequest { DecodeTokensPerSecond = 26, PrefillTokensPerSecond = 0 } });
        Assert.Equal(26, plain.Latest(HistoryCurve.Decode));
    }

    [Fact]
    public void TheCustomEngineMapsItsPerChunkPrefillCounter()
    {
        var provider = new Stratoscope.Core.Engines.Custom.CustomProvider(new EngineConfig(), new FakeSource());
        var metrics = Stratoscope.Core.Engines.Custom.CustomMetrics.Parse("q27:num_requests_running 1\nq27:prefill_tokens_total 273202\nq27:generation_tokens_total 111093\nq27:prompt_tokens_total 9782133\nq27:requests_total 5\nq27:cached_tokens_total 1\n");
        var snapshot = provider.Translate(metrics!, "m");
        Assert.Equal(273202, snapshot.Totals?.PrefillComputedTokens);
        Assert.Equal(111093, snapshot.Totals?.OutputTokens);
    }
}

public class DampingPerSecondTests
{
    [Theory]
    [InlineData(HistorySmoothing.Normal)] [InlineData(HistorySmoothing.High)] [InlineData(HistorySmoothing.Highest)]
    public void TheSmoothingFeelsTheSameAtAnySampleRate(HistorySmoothing smoothing)
    {
        // A step from 0 to 100 at t = 0: after two seconds the filtered value is the same whether samples come every 2 s, 0.5 s or 0.1 s.
        double After(double spacing)
        {
            var f = new OnePoleFilter(smoothing.Damping());
            f.Step(0);
            double y = 0;
            for (double t = spacing; t <= 2.0 + 1e-9; t += spacing) y = f.Step(100, spacing);
            return y;
        }
        var expected = 100 * (1 - Math.Pow(smoothing.Damping(), 2));
        Assert.Equal(expected, After(2), 6);
        Assert.Equal(expected, After(0.5), 6);
        Assert.Equal(expected, After(0.1), 6);
    }

    [Fact]
    public void ASampleStepUsesDToThePowerDt()
    {
        var f = new OnePoleFilter(0.5);
        f.Step(0);
        Assert.Equal(100 * (1 - Math.Sqrt(0.5)), f.Step(100, 0.5), 9);
        Assert.Equal(100, new OnePoleFilter(0).Step(100, 0.5));       // Off passes every value
    }
}

public class OnePoleFilterTests
{
    [Theory]
    [InlineData(HistorySmoothing.Off, 0.0)]
    [InlineData(HistorySmoothing.Normal, 0.5)]
    [InlineData(HistorySmoothing.High, 0.9)]
    [InlineData(HistorySmoothing.Highest, 0.95)]
    public void TheSettingsHaveTheirDamping(HistorySmoothing smoothing, double damping) => Assert.Equal(damping, smoothing.Damping());

    [Fact]
    public void ItFollowsYEqualsDYPlusOneMinusDX()
    {
        var f = new OnePoleFilter(0.5);
        Assert.Equal(10, f.Step(10));                 // the first value passes
        Assert.Equal(15, f.Step(20), 9);              // 0.5 * 10 + 0.5 * 20
        Assert.Equal(7.5, f.Step(0), 9);
    }

    [Fact]
    public void OffPassesEveryValue()
    {
        var f = new OnePoleFilter(0);
        foreach (var v in new[] { 3.0, 99, 0, 12 }) Assert.Equal(v, f.Step(v));
    }

    [Fact]
    public void ASlowerFilterReactsLessToAStep()
    {
        double After(double d) { var f = new OnePoleFilter(d); f.Step(0); return f.Step(100); }
        Assert.True(After(0.5) > After(0.9) && After(0.9) > After(0.95));
        Assert.Equal(5, After(0.95), 9);
    }

    [Fact]
    public void AnUnknownValueBreaksTheLineAndRestartsTheFilter()
    {
        var f = new OnePoleFilter(0.9);
        f.Step(100);
        Assert.True(double.IsNaN(f.Step(double.NaN)));
        Assert.Equal(40, f.Step(40));
    }
}

public class SpeedHistoryTests
{
    private static readonly double[] Ages = new double[SpeedHistory.Capacity];
    private static readonly double[] Values = new double[SpeedHistory.Capacity];

    [Fact]
    public void TheBufferKeepsTheLastTwoMinutesAtTheSampleSpacing()
    {
        var h = new SpeedHistory();
        Assert.Equal(120, SpeedHistory.WindowSeconds);
        for (int k = 0; k <= 600; k++) h.AddSample(k * SpeedHistory.SampleSeconds, 10, 20);       // 300 s at the sample rate
        // The buffer also keeps the warm-up before the window (160 to 300 s); the graph reads only the window (180 to 300 s), both ends included.
        Assert.Equal((int)((SpeedHistory.WindowSeconds + SpeedHistory.WarmupSeconds) / SpeedHistory.SampleSeconds) + 1, h.Count);
        var n = h.ReadSmoothed(HistoryCurve.Decode, 0, 300, Ages, Values);
        Assert.Equal((int)(SpeedHistory.WindowSeconds / SpeedHistory.SampleSeconds) + 1, n);
        Assert.Equal(120, Ages[0]);
        Assert.Equal(0, Ages[n - 1]);
        Assert.Equal(SpeedHistory.SampleSeconds, Ages[n - 2] - Ages[n - 1], 9);            // the spacing is the sample interval
        Assert.All(Values.Take(n), v => Assert.Equal(20, v));
    }

    [Fact]
    public void TheOldestSampleOnScreenDoesNotJumpAsTheWindowSlides()
    {
        // A value that alternates 0 / 100 every sample: with the warm-up the oldest visible point is already smoothed (near 50) at every
        // repaint; a filter restarted at the window edge would show the raw 0 or 100 there and flip between them as the window slides.
        var h = new SpeedHistory();
        for (int k = 0; k <= 400; k++) h.AddSample(k * SpeedHistory.SampleSeconds, k % 2 == 0 ? 0 : 100, 0);
        foreach (var now in new[] { 200.0, 200.5 })
        {
            Assert.True(h.ReadSmoothed(HistoryCurve.Prefill, 0.9, now, Ages, Values) > 0);
            Assert.InRange(Values[0], 25, 75);
        }
    }

    [Fact]
    public void TheTotalIsPromptPlusGeneratedAndUnknownWhenEitherIs()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 100, 30);
        Assert.Equal(130, h.Latest(HistoryCurve.Total));
        h.AddSample(2, double.NaN, 30);
        Assert.True(double.IsNaN(h.Latest(HistoryCurve.Total)));
        Assert.Equal(30, h.Latest(HistoryCurve.Decode));
    }

    [Fact]
    public void TheCurvesAreSmoothedWhenReadAndRawInside()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);
        h.AddSample(2, 0, 100);
        Assert.Equal(100, h.Latest(HistoryCurve.Decode));
        var n = h.ReadSmoothed(HistoryCurve.Decode, HistorySmoothing.Normal.Damping(), 2, Ages, Values);
        Assert.Equal(2, n);
        Assert.Equal(75, Values[1], 9);                  // damping 0.5 per second, 2 s apart: d = 0.5 ^ 2 = 0.25, y = 0.75 * 100
        h.ReadSmoothed(HistoryCurve.Decode, HistorySmoothing.Off.Damping(), 2, Ages, Values);
        Assert.Equal(100, Values[1]);
    }

    [Fact]
    public void TheGraphScaleFollowsTheLargestCurve()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 400, 50);                           // total 450
        Assert.True(h.GraphScale.Max >= 450);
    }

    [Fact]
    public void AnIdleEngineIsZeroAndAnOfflineOneIsUnknown()
    {
        Assert.Equal((0.0, 0.0), SpeedHistory.Rates(new EngineSnapshot("Strata", "m", EngineStatus.Idle)));
        var (p, d) = SpeedHistory.Rates(null);
        Assert.True(double.IsNaN(p) && double.IsNaN(d));
    }

    [Fact]
    public void OneReadingRequestDoesNotWriteButParallelOnesStayUnknown()
    {
        var one = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PrefillTokensPerSecond = 900 } };
        Assert.Equal((900.0, 0.0), SpeedHistory.Rates(one));
        var parallel = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PrefillTokensPerSecond = 900, Running = 3 } };
        var (_, decode) = SpeedHistory.Rates(parallel);
        Assert.True(double.IsNaN(decode));
    }
}

public class RequestBarTests
{
    private static BatchSlot Slot(int i, string state, double? read = null, double? total = null, double? tps = null) =>
        new BatchSlot { Index = i, State = state, PromptRead = read, PromptTokens = total, TokensPerSecond = tps };

    [Fact]
    public void OneRequestIsOneBarWithItsPrefillProgress()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PromptRead = 500, PromptTotal = 1000 } };
        var bars = RequestBars.From(s);
        Assert.Single(bars);
        Assert.True(bars[0].Reading);
        Assert.Equal(0.5, bars[0].Fraction);
    }

    [Fact]
    public void OneWritingRequestIsOneBarWithItsSpeed()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode) { Live = new LiveRequest { DecodeTokensPerSecond = 26 } };
        var bars = RequestBars.From(s);
        Assert.Single(bars);
        Assert.False(bars[0].Reading);
        Assert.Equal(26, bars[0].TokensPerSecond);
    }

    [Fact]
    public void ParallelRequestsAreOneBarEachWithTheirOwnPhase()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode)
        {
            Live = new LiveRequest { Running = 3, DecodeTokensPerSecond = 60 },
            Slots = new List<BatchSlot> { Slot(0, "decoding", tps: 30), Slot(1, "reading", 100, 400), Slot(2, "idle"), Slot(3, "decoding", tps: 25) },
        };
        var bars = RequestBars.From(s);
        Assert.Equal(3, bars.Count);
        Assert.False(bars[0].Reading); Assert.Equal(30, bars[0].TokensPerSecond);
        Assert.True(bars[1].Reading); Assert.Equal(0.25, bars[1].Fraction);
        Assert.Equal(30, RequestBars.PeakSpeed(bars));
    }

    [Fact]
    public void AtMostEightBars()
    {
        var slots = Enumerable.Range(0, 12).Select(i => Slot(i, "decoding", tps: 10)).ToList();
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode) { Live = new LiveRequest { Running = 12 }, Slots = slots };
        Assert.Equal(8, RequestBars.From(s).Count);
    }

    [Fact]
    public void NoPhaseAndNoBusyMeanNoBars()
    {
        Assert.Empty(RequestBars.From(new EngineSnapshot("Strata", "m", EngineStatus.Running)));
        Assert.Empty(RequestBars.From(new EngineSnapshot("Strata", "m", EngineStatus.Idle)));
        Assert.Empty(RequestBars.From(null));
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void TheStackFitsTheSpaceAndOneBarKeepsItsHeight(int dpi)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        Assert.Equal(ReadoutPainter.GlyphHeightPx(m, true), ReadoutPainter.BarRows(1, m).Total);
        for (int n = 2; n <= 8; n++)
        {
            var stack = ReadoutPainter.BarRows(n, m); var (bar, gap, total) = (stack.Bar, stack.Gap, stack.Total);
            Assert.True(bar >= 2);
            Assert.Equal(n * bar + stack.Extra + (n - 1) * gap, total);
            Assert.True(total <= m.Px(ReadoutPainter.MaxStackHeight), n + " bars at " + dpi + " DPI");
            Assert.True(bar >= 2 && gap >= 1, n + " bars at " + dpi + " DPI");
        }
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void TheStackGrowsWithTheCountButBarsGetThinner(int dpi)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        var single = ReadoutPainter.BarRows(1, m);
        var previous = single;
        for (int n = 2; n <= 8; n++)
        {
            var rows = ReadoutPainter.BarRows(n, m);
            Assert.True(rows.Total >= previous.Total, "taller or equal at " + n);
            Assert.True(rows.HeightOf(0) <= previous.HeightOf(0), "thinner or equal at " + n);
            Assert.True(rows.HeightOf(0) < single.Bar);
            Assert.True(rows.Total < single.Total * n, "grows less than the count");
            previous = rows;
        }
    }
}

public class WindowAverageTests
{
    [Fact]
    public void ThePrefillAverageUsesRawSamplesAndThroughputRequiresCounters()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);                      // older than the window at the end (121 s): dropped
        h.AddSample(100, 10, 100);
        h.AddSample(110, 20, 200);
        h.AddSample(121, double.NaN, 300);
        Assert.Equal(15, h.Average(HistoryCurve.Prefill, 121));        // the two known samples
        Assert.True(double.IsNaN(h.Average(HistoryCurve.Decode, 121)));
        Assert.True(double.IsNaN(h.Average(HistoryCurve.Total, 121)));
        Assert.True(double.IsNaN(new SpeedHistory().Average(HistoryCurve.Total, 0)));
    }

    [Fact]
    public void TheAverageIgnoresTheSmoothing()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);
        h.AddSample(2, 100, 0);
        h.AddSample(3, 300, 0);
        Assert.Equal(200, h.Average(HistoryCurve.Prefill, 3));
    }

    [Fact]
    public void TimeInWhichNothingRanIsNotPartOfThePrefillAverage()
    {
        var h = new SpeedHistory();
        for (int t = 0; t < 100; t++) h.AddSample(t, 0, 0);
        h.AddSample(100, 80, 0); h.AddSample(101, 120, 0);
        Assert.Equal(100, h.Average(HistoryCurve.Prefill, 101));
        Assert.True(double.IsNaN(new SpeedHistory().Average(HistoryCurve.Prefill, 101)));
    }
}

public class HistorySmoothingConfigTests
{
    [Theory]
    [InlineData("off", HistorySmoothing.Off)]
    [InlineData("normal", HistorySmoothing.Normal)]
    [InlineData("High", HistorySmoothing.High)]
    [InlineData("highest", HistorySmoothing.Highest)]
    public void TheSettingIsReadFromTheEnvironment(string value, HistorySmoothing expected)
    {
        var config = Config.Load(new Dictionary<string, string?> { ["STRATOSCOPE_HISTORY_SMOOTHING"] = value }, "does-not-exist.json");
        Assert.Equal(expected, config.HistorySmoothing);
        Assert.Empty(config.Problems);
    }

    [Fact]
    public void NormalIsTheDefaultAndAWrongValueIsNamed()
    {
        Assert.Equal(HistorySmoothing.Normal, Config.Load(new Dictionary<string, string?>(), "does-not-exist.json").HistorySmoothing);
        var wrong = Config.Load(new Dictionary<string, string?> { ["STRATOSCOPE_HISTORY_SMOOTHING"] = "heavy" }, "does-not-exist.json");
        Assert.Contains(wrong.Problems, p => p.Contains("history_smoothing"));
    }
}
