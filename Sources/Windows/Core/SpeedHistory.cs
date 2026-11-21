namespace Stratoscope.Core;

/// <summary>The curves of the history graph.</summary>
public enum HistoryCurve { Prefill, Decode, Total }

/// <summary>One running request as the readout bar shows it.</summary>
/// <param name="Reading">True while the request reads its prompt (the bar is its progress), false while it generates (the bar is its speed).</param>
/// <param name="Fraction">Prefill progress 0..1 while reading; unused while generating.</param>
/// <param name="TokensPerSecond">The request's own decode speed, or null when the engine does not report it.</param>
public readonly record struct RequestBar(bool Reading, double Fraction, double? TokensPerSecond);

/// <summary>
/// The per-request bars of the taskbar readout. One bar per running request, at most <see cref="MaxBars"/>. Source: the batch slots the
/// engine reports (state, prompt read and prompt tokens, the slot's own generated-token speed). A single request, or an engine without
/// slots, is one bar from the live request, so one request looks as it always did.
/// </summary>
public static class RequestBars
{
    public const int MaxBars = 8;

    public static IReadOnlyList<RequestBar> From(EngineSnapshot? s)
    {
        if (s is null || !s.IsBusy) return Array.Empty<RequestBar>();
        // Every held request counts, as in the panel: with batched decode only one request is "running" at a time and the others are "parked"
        // between steps, but all of them are decoding.
        var active = s.Slots?.Where(sl => sl.IsActive).ToList();
        if (active != null && active.Count > 1)
        {
            var list = new List<RequestBar>(Math.Min(MaxBars, active.Count));
            foreach (var slot in active.Take(MaxBars))
                list.Add(slot.IsReading
                    ? new RequestBar(true, Display.SlotFraction(slot, 0), null)
                    : new RequestBar(false, 0, slot.TokensPerSecond));
            return list;
        }
        switch (s.Status)
        {
            case EngineStatus.Prefill: return new[] { new RequestBar(true, (s.Live?.Percent ?? 0) / 100.0, null) };
            case EngineStatus.Thinking:
            case EngineStatus.Decode: return new[] { new RequestBar(false, 0, s.Live?.DecodeTokensPerSecond) };
            default: return Array.Empty<RequestBar>();     // running without a phase: no bar, the state dot says it
        }
    }

    /// <summary>The largest decode speed among the bars, or null when none is known. What the speed scale is fed.</summary>
    public static double? PeakSpeed(IReadOnlyList<RequestBar> bars)
    {
        double? peak = null;
        foreach (var b in bars) if (!b.Reading && b.TokensPerSecond is double v && double.IsFinite(v)) peak = Math.Max(peak ?? 0, v);
        return peak;
    }
}

internal readonly record struct ThroughputWindow(double Seconds, double Generated, double Prefill);

/// <summary>
/// The last two minutes of speed of one server, for the history graph and the adaptive scales.
/// Curves (tokens per second, all from the engine's own numbers; an unknown value is NaN and breaks the line):
///  - Prefill: prompt tokens per second over all requests. With the engine's phase clocks (<see cref="AddCounters"/>) per second of the time the
///    engine spent on prompt work, else per second of wall time.
///  - Decode: generated tokens per second over all requests, likewise per second of decode time with the phase clocks.
///  - Total: tokens processed per second through the system, prompt plus generated, the throughput meaning of vLLM
///    (<c>vllm:prompt_tokens_total</c> + <c>vllm:generation_tokens_total</c> as a rate) and of the OpenTelemetry GenAI token usage
///    (<c>gen_ai.client.token.usage</c>, input and output tokens), per second of wall time. With counters it uses their deltas between polls;
///    otherwise it is the sum of the available phase rates when both are known.
/// While the engine reports it is idle, all three are 0 (nothing runs). While one request reads, it does not write, so an unreported
/// decode speed is 0 then, and the other way round; with parallel requests an unreported speed stays unknown.
/// The samples are raw. The smoothing (<see cref="OnePoleFilter"/>) is applied when the graph reads them, so changing the setting changes
/// the whole window at once.
/// </summary>
public sealed class SpeedHistory
{
    /// <summary>The length of the history graph and of the averages of its legend, seconds.</summary>
    public const double WindowSeconds = 120;
    /// <summary>How often the speed counters are sampled between the full polls, seconds.</summary>
    public const double SampleSeconds = 0.5;
    /// <summary>
    /// Samples kept before the window, seconds: the smoothing runs through them, so a sample is already filtered when it enters the graph
    /// (a filter started at the window's oldest sample would restart on every repaint and make the left edge jump).
    /// </summary>
    public const double WarmupSeconds = 20;
    /// <summary>Room for the window and the warm-up at the sample rate, with a margin for the odd extra full-poll sample.</summary>
    public const int Capacity = (int)((WindowSeconds + WarmupSeconds) / SampleSeconds) + 160;

    private readonly double[] time = new double[Capacity];
    private readonly double[][] values = { new double[Capacity], new double[Capacity], new double[Capacity] };
    private int start, count;

    /// <summary>The scale of the per-request bars in the taskbar readout (one request's decode speed).</summary>
    public SpeedScale RequestScale { get; } = new SpeedScale();
    /// <summary>The scale of the reading rows of the panel (one request's own prefill speed): the same kind of adaptive scale as <see cref="RequestScale"/>, from 1,000 t/s.</summary>
    public SpeedScale RequestPrefillScale { get; } = new SpeedScale(1000);
    /// <summary>The scale of the history graph (the largest of the three curves).</summary>
    public SpeedScale GraphScale { get; } = new SpeedScale();
    private readonly SpeedScale[] curveScales = { new SpeedScale(100), new SpeedScale(10), new SpeedScale(100) };
    /// <summary>The scale of one curve in the history graph (each curve is drawn against its own adaptive maximum).</summary>
    public SpeedScale ScaleOf(HistoryCurve curve) => curveScales[(int)curve];
    /// <summary>The scales of the bar charts of the Speed section (the speed of the last finished requests): prefill from 100 t/s, decode from 10 t/s.</summary>
    public SpeedScale PrefillChartScale { get; } = new SpeedScale(100);
    public SpeedScale DecodeChartScale { get; } = new SpeedScale(10);

    public int Count => count;

    public static double Seconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>How long after the last counter sample the full polls leave the curves to the counters (a few sample intervals, and more than a full poll takes).</summary>
    private const double CounterFreshSeconds = 3;
    private double lastCounterSeconds = double.NaN;
    private double counterSampleSeconds = double.NegativeInfinity;
    private readonly CounterRate prefillRate = new(), generatedRate = new();
    private readonly PhaseRate prefillPhase = new(), decodePhase = new();
    private readonly record struct CounterReading(double Time, double Prefill, double Generated);
    private readonly Queue<CounterReading> counterWindow = new(Capacity);
    private CounterReading latestCounters;
    private ThroughputWindow? reportedThroughput;
    private double reportedThroughputAt;
    private bool phaseBaseline;
    private string? lastHistorySource;

    /// <summary>
    /// A sample from the light counter read (every <see cref="SampleSeconds"/>). With the engine's phase clocks
    /// (<see cref="EngineCounters.HasPhaseClocks"/>), Prefill is the prompt tokens per second of the time the engine spent on prompt work and Decode
    /// the decode-step tokens per second of the time it spent on decode steps (<see cref="PhaseRate"/>: the sampling interval does not dilute
    /// them; 0 while the phase does not run), and Total stays the throughput, prompt plus generated tokens per second of wall time. Without them,
    /// prefill and decode are the rates of the computed prompt tokens and the generated tokens per second of wall time, each between the reads at
    /// which its counter rose (<see cref="CounterRate"/>: an engine that publishes its counter once a second gives no 0, 2x, 0, 2x ping-pong), and
    /// Total always uses both token deltas over the actual elapsed time between reads. Returns true when a sample was added.
    /// The first read only sets the baseline; a counter that fell (the engine restarted)
    /// adds an unknown sample (a gap) and a new baseline; a gap over <see cref="MaxPollGapSeconds"/> adds nothing and starts over. Null or
    /// incomplete token counters add nothing, and the full polls feed the curves.
    /// </summary>
    public bool AddCounters(DateTime utc, EngineCounters? counters)
    {
        var now = Seconds(utc);
        if (counters?.PrefillComputedTokens is not double prefill || counters.GeneratedTokens is not double generated
            || !double.IsFinite(prefill) || !double.IsFinite(generated)) return false;
        var elapsed = now - lastCounterSeconds;
        var hadBaseline = !double.IsNaN(lastCounterSeconds) && elapsed > 0 && elapsed <= MaxPollGapSeconds;
        var previous = latestCounters;
        var reset = hadBaseline && (prefill < previous.Prefill || generated < previous.Generated);
        if (!hadBaseline || reset) counterWindow.Clear();
        while (counterWindow.TryPeek(out var oldest) && (now - oldest.Time > WindowSeconds + MaxPollGapSeconds || counterWindow.Count == Capacity))
            counterWindow.Dequeue();
        latestCounters = new CounterReading(now, prefill, generated);
        counterWindow.Enqueue(latestCounters);
        lastCounterSeconds = now;
        var phases = counters.HasPhaseClocks;
        if (!phases) { prefillPhase.Reset(); decodePhase.Reset(); }
        if (!hadBaseline)
        {
            prefillRate.Reset(); generatedRate.Reset(); prefillPhase.Reset(); decodePhase.Reset();
            prefillRate.Update(now, prefill); generatedRate.Update(now, generated);
            if (phases)
            {
                prefillPhase.Update(now, prefill, counters.PrefillBusySeconds!.Value);
                decodePhase.Update(now, counters.DecodeStepTokens!.Value, counters.DecodeBusySeconds!.Value);
            }
            phaseBaseline = phases;
            return false;
        }
        counterSampleSeconds = now;
        var p = prefillRate.Update(now, prefill);
        var g = generatedRate.Update(now, generated);
        double pp = 0, dp = 0;
        var hadPhaseBaseline = phaseBaseline;
        if (phases)
        {
            // (fed on every read, a gap included, so a restart gives all rates their new baseline at the same read)
            pp = prefillPhase.Update(now, prefill, counters.PrefillBusySeconds!.Value);
            dp = decodePhase.Update(now, counters.DecodeStepTokens!.Value, counters.DecodeBusySeconds!.Value);
        }
        phaseBaseline = phases;
        if (reset || double.IsNaN(p) || double.IsNaN(g) || double.IsNaN(pp) || double.IsNaN(dp)) { AddSample(now, double.NaN, double.NaN); return true; }
        var total = ((prefill - previous.Prefill) + (generated - previous.Generated)) / elapsed;
        // The read that first brings the phase clocks is their baseline; until the next one the wall-time rates stand in.
        if (phases && hadPhaseBaseline) AddSample(now, pp, dp, total);
        else AddSample(now, p, g, total);
        return true;
    }

    /// <summary>Records the state of one full poll. A null snapshot (nothing answers) records unknown values.</summary>
    public void Add(DateTime utc, EngineSnapshot? s)
    {
        if (s?.RouterLimited == true) return;   // The panel can retain the data, but it is not a new observation.
        if (s?.HistorySource != lastHistorySource)
        {
            lastHistorySource = s?.HistorySource;
            lastCounterSeconds = double.NaN;
            counterWindow.Clear();
            counterSampleSeconds = double.NegativeInfinity;
            lastPollSeconds = double.NaN;
        }
        reportedThroughput = s?.ReportedThroughput;
        reportedThroughputAt = Seconds(utc);
        if (s?.FullPollCounters is { } counters) AddCounters(utc, counters);

        // While counters feed the curves, do not interleave rates from another source.
        if (Seconds(utc) - counterSampleSeconds > CounterFreshSeconds)
        {
            var (prefill, decode) = RatesBetweenPolls(Seconds(utc), s);
            AddSample(Seconds(utc), prefill, decode);
        }
        if (RequestBars.PeakSpeed(RequestBars.From(s)) is double peak) RequestScale.Observe(peak, Seconds(utc));
        if (s?.Slots != null)
        {
            // The panel's rows: a decoding slot's speed shares the taskbar scale, a reading slot's prefill speed has its own.
            double? decodePeak = null, prefillPeak = null;
            foreach (var slot in s.Slots.Where(sl => sl.IsActive))
            {
                if (slot.IsReading) { if (slot.PrefillTokensPerSecond is double v && double.IsFinite(v)) prefillPeak = Math.Max(prefillPeak ?? 0, v); }
                else if (slot.TokensPerSecond is double d && double.IsFinite(d)) decodePeak = Math.Max(decodePeak ?? 0, d);
            }
            if (decodePeak is double dp) RequestScale.Observe(dp, Seconds(utc));
            if (prefillPeak is double pp) RequestPrefillScale.Observe(pp, Seconds(utc));
        }
        if (s != null && s.Recent.Count > 0)
        {
            // The charts of the finished requests share the rule of the other scales: they do not change with every poll.
            var recent = s.Recent.Take(12).ToList();
            var decodeMax = recent.Max(r => r.DecodeTokensPerSecond ?? 0);
            var prefillMax = recent.Max(r => r.PrefillTokensPerSecond() ?? 0);
            DecodeChartScale.Observe(decodeMax, Seconds(utc));
            PrefillChartScale.Observe(prefillMax, Seconds(utc));
        }
    }

    /// <summary>A gap longer than this between two polls (a sleeping PC, a stalled engine) makes no rate: the counters then say nothing about the speed.</summary>
    internal const double MaxPollGapSeconds = 30;
    private const int MaxSlots = 64;
    private double lastPollSeconds = double.NaN, lastPrefillCounter = double.NaN, lastGenerationCounter = double.NaN;
    private readonly double[] lastSlotRead = Enumerable.Repeat(double.NaN, MaxSlots).ToArray();

    /// <summary>
    /// The prefill and decode speed of a poll, at the source and the same for every engine, in this order:
    ///  1. The engine's cumulative counters between two polls (<see cref="Totals.PrefillComputedTokens"/> for prompt tokens that were computed,
    ///     <see cref="Totals.OutputTokens"/> for generated tokens): the delta over the seconds between the polls. This covers every request at
    ///     once whatever phase the engine's headline status names. Only counters that advance while a request runs are used (the engine adapter
    ///     fills them only then); a counter that falls (the engine restarted), a missing one, or a gap over <see cref="MaxPollGapSeconds"/>
    ///     gives no counter rate for that poll.
    ///  2. The sum over the batch slots: the decoding slots' own speeds, and for the reading slots the prompt tokens read since the last poll.
    ///     Counted only when the engine reports slots.
    ///  3. The live request's speeds, <see cref="Rates"/>.
    /// Total is their sum, as always.
    /// </summary>
    internal (double Prefill, double Decode) RatesBetweenPolls(double nowSeconds, EngineSnapshot? s)
    {
        var fallback = Rates(s);
        var elapsed = nowSeconds - lastPollSeconds;
        var usable = !double.IsNaN(lastPollSeconds) && elapsed > 0 && elapsed <= MaxPollGapSeconds;
        double prefillCounter = double.NaN, generationCounter = double.NaN;
        if (s != null && !s.IsError && s.HasModel && !s.IsLoading)
        {
            prefillCounter = s.Totals?.PrefillComputedTokens is double p && double.IsFinite(p) ? p : double.NaN;
            generationCounter = s.Totals?.OutputTokens is double g && double.IsFinite(g) ? g : double.NaN;
        }
        double Delta(double now, double before) => usable && !double.IsNaN(now) && !double.IsNaN(before) && now >= before ? (now - before) / elapsed : double.NaN;
        var prefillRate = Delta(prefillCounter, lastPrefillCounter);
        var decodeRate = Delta(generationCounter, lastGenerationCounter);

        // The slots: reading ones by the progress of their prompt since the last poll, decoding ones by their own speed.
        double slotPrefill = double.NaN, slotDecode = double.NaN;
        var slotRead = Enumerable.Repeat(double.NaN, MaxSlots).ToArray();
        if (s?.Slots is { Count: > 0 } slots && s.HasModel && !s.IsError && !s.IsLoading)
        {
            double? prefillSum = 0, decodeSum = 0;
            foreach (var slot in slots)
            {
                if (slot.Index < 0 || slot.Index >= MaxSlots) continue;
                if (slot.IsReading && slot.PromptRead is double read)
                {
                    slotRead[slot.Index] = read;
                    var before = lastSlotRead[slot.Index];
                    if (usable && !double.IsNaN(before) && read >= before) prefillSum += (read - before) / elapsed;
                    else if (usable) prefillSum = null;          // a slot that just started or reset: its rate is unknown this poll
                }
                else if (slot.IsReading) prefillSum = null;
                if (slot.IsDecoding) decodeSum = slot.TokensPerSecond is double v && double.IsFinite(v) && decodeSum != null ? decodeSum + v : null;
            }
            if (!usable && slots.Any(x => x.IsReading)) prefillSum = null;
            slotPrefill = prefillSum ?? double.NaN;
            slotDecode = decodeSum ?? double.NaN;
        }

        lastPollSeconds = nowSeconds; lastPrefillCounter = prefillCounter; lastGenerationCounter = generationCounter;
        Array.Copy(slotRead, lastSlotRead, MaxSlots);

        if (s is null || s.IsError || !s.HasModel || s.IsLoading || s.Status == EngineStatus.Idle) return fallback;
        var prefill = !double.IsNaN(prefillRate) ? prefillRate : !double.IsNaN(slotPrefill) ? slotPrefill : fallback.Prefill;
        var decode = !double.IsNaN(decodeRate) ? decodeRate : !double.IsNaN(slotDecode) ? slotDecode : fallback.Decode;
        return (prefill, decode);
    }

    /// <summary>The prefill and decode speed of a snapshot as the curves take them (see the class comment).</summary>
    public static (double Prefill, double Decode) Rates(EngineSnapshot? s)
    {
        if (s is null || s.IsError || !s.HasModel || s.IsLoading) return (double.NaN, double.NaN);
        if (s.Status == EngineStatus.Idle) return (0, 0);
        double P(double? v) => v is double d && double.IsFinite(d) ? d : double.NaN;
        var prefill = P(s.Live?.PrefillTokensPerSecond);
        var decode = P(s.Live?.DecodeTokensPerSecond);
        if (s.Live?.IsParallel != true)
        {
            if (double.IsNaN(decode) && s.Status == EngineStatus.Prefill) decode = 0;
            if (double.IsNaN(prefill) && (s.Status == EngineStatus.Decode || s.Status == EngineStatus.Thinking)) prefill = 0;
        }
        return (prefill, decode);
    }

    /// <summary>Adds one sample; the total is prefill plus decode. Samples older than <see cref="WindowSeconds"/> are dropped.</summary>
    public void AddSample(double nowSeconds, double prefill, double decode) =>
        AddSample(nowSeconds, prefill, decode, double.IsNaN(prefill) || double.IsNaN(decode) ? double.NaN : prefill + decode);

    /// <summary>
    /// Adds one sample with its own total: with phase speeds for prefill and decode (each over the time spent on its phase) the total is the
    /// throughput over wall time, which is not their sum.
    /// </summary>
    public void AddSample(double nowSeconds, double prefill, double decode, double total)
    {
        while (count > 0 && nowSeconds - time[start] > WindowSeconds + WarmupSeconds) { start = (start + 1) % Capacity; count--; }
        if (count == Capacity) { start = (start + 1) % Capacity; count--; }
        var i = (start + count) % Capacity;
        time[i] = nowSeconds;
        values[0][i] = prefill; values[1][i] = decode; values[2][i] = total;
        count++;
        double Known(double v) => double.IsNaN(v) ? 0 : v;
        GraphScale.Observe(Math.Max(Known(total), Math.Max(Known(prefill), Known(decode))), nowSeconds);
        // Each curve also has its own scale: prefill runs at thousands of t/s, decode at tens, so one shared axis flattens decode.
        curveScales[0].Observe(Known(prefill), nowSeconds); curveScales[1].Observe(Known(decode), nowSeconds); curveScales[2].Observe(Known(total), nowSeconds);
    }

    /// <summary>
    /// Copies one curve, smoothed with damping <paramref name="damping"/>, into the caller's arrays: the age of each sample in seconds
    /// (0 = now, up to <see cref="WindowSeconds"/>) and its value (NaN where unknown). Returns the number of samples. The arrays must hold
    /// <see cref="Count"/> items.
    /// </summary>
    public int ReadSmoothed(HistoryCurve curve, double damping, double nowSeconds, double[] ages, double[] output)
    {
        var filter = new OnePoleFilter(damping);
        var source = values[(int)curve];
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            var i = (start + k) % Capacity;
            var age = nowSeconds - time[i];
            // The damping is per second (see HistorySmoothings.Damping); the samples are not evenly spaced, so each step uses its own spacing.
            // The filter runs through the warm-up samples too; only the window is returned.
            var dt = k == 0 ? 1.0 : time[i] - time[(i + Capacity - 1) % Capacity];
            var y = filter.Step(source[i], dt);
            if (age > WindowSeconds) continue;
            ages[n] = Math.Max(0, age); output[n] = y; n++;
        }
        return n;
    }

    /// <summary>
    /// Decode and total use up to two minutes of wall time. Engine history can provide the window on the first poll.
    /// Prefill is the average of positive raw prefill samples. NaN when the necessary data is unavailable.
    /// </summary>
    public double Average(HistoryCurve curve, double nowSeconds)
    {
        if (curve != HistoryCurve.Prefill) return WallAverage(curve, nowSeconds);
        var source = values[(int)curve];
        double sum = 0; int n = 0;
        for (int k = 0; k < count; k++)
        {
            var i = (start + k) % Capacity;
            if (nowSeconds - time[i] > WindowSeconds || double.IsNaN(source[i]) || source[i] <= 0) continue;
            sum += source[i]; n++;
        }
        return n == 0 ? double.NaN : sum / n;
    }

    private double WallAverage(HistoryCurve curve, double nowSeconds)
    {
        var window = nowSeconds - reportedThroughputAt <= MaxPollGapSeconds ? reportedThroughput : null;
        if (counterWindow.Count >= 2 && nowSeconds - latestCounters.Time <= MaxPollGapSeconds)
        {
            var cutoff = latestCounters.Time - WindowSeconds;
            var first = counterWindow.Peek();
            // Take an observed boundary. While the window fills, use all the time observed so far.
            foreach (var reading in counterWindow)
            {
                if (reading.Time > cutoff) break;
                first = reading;
            }
            var seconds = latestCounters.Time - first.Time;
            if (window is null || seconds >= WindowSeconds || seconds >= window.Value.Seconds)
                window = new ThroughputWindow(seconds, latestCounters.Generated - first.Generated, latestCounters.Prefill - first.Prefill);
        }
        if (window is not { } available) return double.NaN;
        var tokens = available.Generated + (curve == HistoryCurve.Total ? available.Prefill : 0);
        return tokens / available.Seconds;
    }

    /// <summary>The newest raw value of a curve, or NaN when there is none.</summary>
    public double Latest(HistoryCurve curve) => count == 0 ? double.NaN : values[(int)curve][(start + count - 1) % Capacity];
}
