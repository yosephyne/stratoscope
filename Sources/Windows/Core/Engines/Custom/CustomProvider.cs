namespace Stratoscope.Core.Engines.Custom;

/// <summary>
/// Adapter for the custom 27B server (its metrics use the q27: prefix; frontend/server.py), behind a llama-swap router or directly.
///
/// Data sources, all GET. A routed read requires a ready model from the router's <c>/running</c>.
///  - <c>/health</c>: <c>{"status":"ok"}</c> when the engine is ready; another status (<c>unloaded</c>, <c>loading</c>) means no request can run
///    yet and <c>/metrics</c> is not asked. It names no engine, which is why <see cref="AutoEngineProvider"/> recognises the custom 27B server by its metrics.
///  - <c>/metrics</c> (Prometheus text, prefix <c>q27:</c>): running and waiting requests, the engine's sequence capacity, free KV pages,
///    prefix cache checkpoints, which admission block holds a request back, and the token totals.
///
///  - <c>/ui/state</c> (JSON, about 150 KB, asked on every poll): <c>tick.seqs</c> with each request's state, its own phase (prefill or decode), prompt,
///    fed, out, max_new, computed, prefill_s and decode_s, and on newer servers its own speeds (prefill_tps, prefill_wall_s, prefill_wall_tps, decode_tps);
///    <c>tick.sample</c> and <c>tick.vram</c> (video card, memory parts, KV cache); <c>tick.cache</c> and <c>tick.sched</c>; <c>info</c> (context size,
///    slots, the drafter: <c>drafter</c>, older servers only the bool <c>spec</c>, KV format, card name); <c>log</c> (the <c>done</c> events are the finished requests).
///    If it is missing, refused or not JSON, the phase is "running" only and those parts are not drawn.
///
/// What the custom 27B server does not report is not drawn: RAM and CPU. The metrics alone are totals across all
/// requests: without /ui/state neither reading nor writing is shown, and no speed.
/// </summary>
/// <remarks>
/// Speed rule: name the source of every value; never estimate.
/// When several requests run together, the headline decode speed (and the headline prefill speed while reading) is the engine's own total over
/// all requests. Each engine reports that total itself:
///  - Strata: live.tok_s, the total of the generating requests (serve/server.py _tok_s). Only when the engine has no total yet (the newest request
///    still reads its prompt) is the sum of the derived per-slot speeds used (StrataProvider.cs, the branch where rates.Sum() is taken). Prefill:
///    live.prefill_tok_s_mean, the engine's mean; a value derived from the progress between polls only when that is missing.
///  - Basalt: live.tok_s, which the engine sums over its slots (BasaltProvider.cs, the speed line).
///  - llama.cpp: /metrics llamacpp:predicted_tokens_seconds, the server-wide decode rate over all slots (prompt_tokens_seconds for prefill). It
///    needs the server's --metrics flag. No sum is used (LlamaCppProvider.cs, the DecodeTokensPerSecond line).
///  - custom: /ui/state tick.sample.decode_busy_tps and tick.sample.prefill_busy_tps (newer servers: the engine's tokens over all requests per
///    second of the time it spent on that phase in the sample), else tick.sample.decode_tps and tick.sample.prefill_tps (its counter deltas
///    per second of wall time between samples) (CustomProvider.cs). The per-request sum is used only when the sample has no rate.
/// A request's own speed is shown on its own row only. Never add up the per-request rows to make the headline unless the engine has no total.
/// This engine: /ui/state tick.sample.decode_busy_tps / prefill_busy_tps, else decode_tps / prefill_tps. Canonical text: RoutedProvider.cs.
/// </remarks>
public sealed class CustomProvider : RoutedProvider
{
    public const string Id = "custom";

    public CustomProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
        : base(config, http, clients) { }

    public override string Brand => "Custom";

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;
        var model = routedId ?? Brand;

        var health = await http.GetAsync(Endpoints.Join(engineBase, "health"), Json.CustomHealthType, cancellation).ConfigureAwait(false);
        if (routedId is not null && health.Status is 409 or 429)
            return new EngineRead(health.Status, false, health.Failure, health.Detail, null);
        if (health.Ok && NotReady(health.Body!.Status) is string reason)
            return new EngineRead(200, true, FetchFailure.None, null, Placeholder(model, EngineStatus.Loading, reason));
        // The server answers 503 while its engine is not ready (a start, or after a crash).
        if (health.Failure == FetchFailure.Status && health.Status == 503)
            return new EngineRead(200, true, FetchFailure.None, null, Placeholder(model, EngineStatus.Loading, "Engine not ready (HTTP 503)"));

        var text = await http.GetTextAsync(Endpoints.Join(engineBase, "metrics"), cancellation).ConfigureAwait(false);
        if (!text.Ok) return new EngineRead(text.Status, false, text.Failure, text.Detail, null);
        var metrics = CustomMetrics.Parse(text.Body);
        if (metrics is null)
        {
            // Read directly, a document without q27: series is no custom 27B server. Routed, the router named an engine that sends no q27: metrics: an error.
            return routedId is null
                ? new EngineRead(text.Status, true, FetchFailure.None, null, null)
                : new EngineRead(text.Status, false, FetchFailure.Other, "no q27: metrics", null);
        }

        // /ui/state is about 150 KB (logs and history). It is the only source of the video card, the memory parts, the cache counters, the engine
        // settings and the finished requests, so it is asked on every poll, idle too. Same router path, 429 handling and timeout as /metrics.
        CustomUiState? ui = null;
        {
            var state = await http.GetAsync(Endpoints.Join(engineBase, "ui", "state"), Json.CustomUiStateType, cancellation).ConfigureAwait(false);
            if (state.Status == 429) return new EngineRead(429, false, FetchFailure.Status, state.Detail, null);
            // Missing, refused or not JSON: no per-request data, the aggregate state "running" stays.
            if (state.Ok) ui = state.Body;
        }

        return new EngineRead(text.Status, true, FetchFailure.None, null, Translate(metrics, model, ui),
            config.Router is null ? null : Counters(metrics));
    }

    /// <summary>
    /// The light <c>/metrics</c> read for direct connections.
    /// </summary>
    internal override async Task<EngineCounters?> ReadCountersFromAsync(string engineBase, CancellationToken cancellation)
    {
        try
        {
            var text = await http.GetTextAsync(Endpoints.Join(engineBase, "metrics"), cancellation).ConfigureAwait(false);
            if (!text.Ok) return null;
            var metrics = CustomMetrics.Parse(text.Body);
            return metrics is null ? null : Counters(metrics);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    // Prefill tokens advance per chunk and generation tokens per delivery. Newer servers also report phase clocks.
    private static EngineCounters Counters(CustomMetrics metrics) =>
        new(metrics.PrefillTokensTotal, metrics.GenerationTokensTotal,
            metrics.PrefillBusySecondsTotal, metrics.DecodeBusySecondsTotal, metrics.DecodeStepTokensTotal);

    private static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tokens divided by the seconds spent, only when both are exposed and the time is above zero.</summary>
    private static double? Speed(double? tokens, double? seconds) => tokens.HasValue && seconds > 0 ? tokens / seconds : null;

    /// <summary>A phase speed of the sample when that phase ran in it (above 0), else null.</summary>
    private static double? Positive(double? v) => v is double d && double.IsFinite(d) && d > 0 ? d : null;

    // stats.py publishes token-counter deltas divided by each sample's wall-time interval. Reverse the division
    // before adding the counts. Start a fresh window at the latest engine lifecycle event or unknown interval.
    private static ThroughputWindow? ReadThroughputHistory(CustomUiState? ui)
    {
        if (ui?.History is not { Count: >= 2 } history || history[^1]?.T is not double end
            || !double.IsFinite(end) || ui.Tick?.T - end > SpeedHistory.MaxPollGapSeconds)
            return null;

        var since = ui.Log is { Count: > 0 } log ? log[0]?.T ?? 0 : 0;
        if (ui.Log is { } events)
            foreach (var entry in events)
                if (entry is { Kind: "engine", T: double t }) since = Math.Max(since, t);

        var start = end;
        double generated = 0, prefilled = 0;
        for (var i = history.Count - 1; i > 0 && end - start < SpeedHistory.WindowSeconds; i--)
        {
            if (history[i - 1]?.T is not double previous || previous < since || history[i] is not { } sample) break;
            var elapsed = start - previous;
            if (!double.IsFinite(elapsed) || elapsed <= 0 || elapsed > SpeedHistory.MaxPollGapSeconds
                || CountTokens(sample.DecodeTps, elapsed) is not double output
                || CountTokens(sample.PrefillTps, elapsed) is not double prompt) break;
            generated += output;
            prefilled += prompt;
            start = previous;
        }
        return end > start ? new ThroughputWindow(end - start, generated, prefilled) : null;
    }

    private static double? CountTokens(double? rate, double elapsed)
    {
        if (rate is not double tps || !double.IsFinite(tps) || tps < 0) return null;
        var tokens = tps * elapsed;
        var whole = Math.Round(tokens);
        return Math.Abs(tokens - whole) <= 1e-6 ? whole : null;
    }

    /// <summary>The state text of a <c>/health</c> status other than ok, or null when the engine is ready.</summary>
    internal static string? NotReady(string? status) =>
        string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ? null : status is null ? "State not reported" : "State: " + status;

    /// <summary>
    /// Translates the metrics into a snapshot: running requests give Running, none give Idle. With <paramref name="ui"/> (<c>/ui/state</c>, read only while
    /// requests run) the phase of each running request is the engine's own: phase "prefill" is reading, "decode" is writing. Public so tests can feed payloads.
    /// </summary>
    public EngineSnapshot Translate(CustomMetrics m, string model, CustomUiState? ui = null)
    {
        var running = m.Running ?? 0;
        var busy = running > 0;
        var status = busy ? EngineStatus.Running : EngineStatus.Idle;

        LiveRequest? live = busy ? new LiveRequest { Running = running > 1 ? (int)running : null } : null;
        int? queued = null;

        if (busy && ui?.Seqs is { } seqs)
        {
            var rows = seqs.Where(s => s != null).Select(s => s!).ToList();
            // waiting: queued, not drawn as busy. running, parked and admitted: counted as running. The phase is the engine's own word for each
            // of them: a parked request that is decoding is still decoding (the scheduler parks it between its turns), so it counts as writing.
            var active = rows.Where(s => Is(s.State, "running") || Is(s.State, "parked") || Is(s.State, "admitted")).ToList();
            var readers = active.Where(s => Is(s.Phase, "prefill")).ToList();
            var writers = active.Where(s => Is(s.Phase, "decode")).ToList();
            if (active.Count > 0)
            {
                queued = rows.Count(s => Is(s.State, "waiting"));
                status = writers.Count > 0 ? EngineStatus.Decode : readers.Count > 0 ? EngineStatus.Prefill : EngineStatus.Running;
                var together = active.Count > 1;
                var one = together ? null : active[0];
                var sample = ui?.Tick?.Sample;
                // Speeds only from exposed values and only for a time above zero. Several requests together: the sum over those in the phase.
                double? Sum(IEnumerable<double?> values) { var list = values.ToList(); return list.Count > 0 && list.All(v => v.HasValue) ? list.Sum(v => v!.Value) : null; }
                live = new LiveRequest
                {
                    PromptRead = status == EngineStatus.Prefill ? Sum(readers.Select(s => s.Fed)) : null,
                    PromptTotal = status == EngineStatus.Prefill && !together ? one!.Prompt : null,
                    Generated = status == EngineStatus.Decode ? Sum(writers.Select(s => s.Out)) : null,
                    MaxTokens = status == EngineStatus.Decode && !together ? one!.MaxNew : null,
                    // Speed rule (full text on this class and in RoutedProvider.cs): the headline is the engine's own total over all requests,
                    // tick.sample.prefill_busy_tps / decode_busy_tps (tokens per second of the time spent on that phase) when the server has them
                    // and the phase ran in the sample, else tick.sample.prefill_tps / decode_tps (per second of wall time); the sum of the
                    // per-request speeds is used only when the sample has no rate.
                    PrefillTokensPerSecond = status == EngineStatus.Prefill ? (Positive(sample?.PrefillBusyTps) ?? sample?.PrefillTps ?? Sum(readers.Select(s => Speed(s.Computed, s.PrefillSeconds)))) : null,
                    DecodeTokensPerSecond = Positive(sample?.DecodeBusyTps) ?? sample?.DecodeTps ?? Sum(writers.Select(s => Speed(s.Out, s.DecodeSeconds))),
                    Running = together ? active.Count : null,
                };
            }
        }

        // Counters since the engine started. Prompt tokens are all prompts admitted, reused ones included.
        Totals? totals = null;
        if (m.PromptTokensTotal.HasValue || m.GenerationTokensTotal.HasValue || m.RequestsTotal.HasValue || m.PrefillTokensTotal.HasValue)
            totals = new Totals
            {
                Requests = m.RequestsTotal, PromptTokens = m.PromptTokensTotal, ReusedTokens = m.CachedTokensTotal, OutputTokens = m.GenerationTokensTotal,
                // q27:prefill_tokens_total is added per prefill chunk (worker.py), so it advances while a request reads (checked live: it moved by 27 tokens
                // in 4 s during a prefill); q27:prompt_tokens_total is added once when a request starts and is not used for a speed.
                PrefillComputedTokens = m.PrefillTokensTotal,
            };

        string? blocked = m.BlockedKv == 1 ? "kv" : m.BlockedSpeed == 1 ? "speed" : m.BlockedKv == 0 && m.BlockedSpeed == 0 ? "none" : null;
        var tick = ui?.Tick;
        var uiInfo = ui?.Info;
        var batchSlotCount = m.EngineSeqs ?? uiInfo?.MaxSeqs;
        // Metrics first (they are asked on every poll); /ui/state fills what they lack.
        var kvFree = m.KvFreePages ?? tick?.Cache?.FreePages;
        var kvCheckpointOnly = m.KvCheckpointOnlyPages ?? tick?.Cache?.CheckpointOnlyPages;
        var checkpoints = m.PrefixCacheCheckpoints ?? tick?.Cache?.Checkpoints;
        // The drafter the engine names (info.drafter); an older server reports only whether one runs (info.spec), which was always DFlash2.
        string? draft = uiInfo?.Drafter ?? (uiInfo?.Spec is bool spec ? (spec ? "DFlash2" : "none") : null);
        EngineInfo? info = null;
        if (batchSlotCount.HasValue || kvFree.HasValue || checkpoints.HasValue || blocked != null || uiInfo != null || tick?.Sched != null)
            info = new EngineInfo
            {
                BatchSlots = batchSlotCount, KvFreePages = kvFree, KvCheckpointOnlyPages = kvCheckpointOnly,
                PrefixCheckpoints = checkpoints, AdmissionBlocked = blocked,
                KvFormat = uiInfo?.KvDtype, DraftWindow = draft, GpuName = uiInfo?.Gpu, TargetSeqs = tick?.Sched?.TargetSeqs,
            };

        // Finished requests: the "done" events of the engine's log (the last 500 events), newest first. ttft and the like are not drawn.
        var recent = (ui?.Log ?? new List<CustomLogEntry?>())
            .Where(e => e is { Kind: "done", Rec: not null }).Select(e => e!)
            .Reverse().Take(20)
            .Select(e => new RequestStats
            {
                PromptTokens = e.Rec!.PromptTokens, ReusedTokens = e.Rec.CachedTokens, PromptRead = e.Rec.ComputedTokens,
                PromptMilliseconds = e.Rec.PrefillSeconds * 1000, OutputTokens = e.Rec.CompletionTokens,
                DecodeTokensPerSecond = e.Rec.DecodeTps, DurationSeconds = e.Rec.Total,
                FinishedAt = e.T is double t ? DateTime.UnixEpoch.AddSeconds(t) : null, Finish = e.Rec.Reason,
            }).ToList();

        // The context of the fullest running request: the prompt fed so far plus what it wrote (the prompt when fed is missing); otherwise the newest finished one.
        double? ctxUsed = null;
        var runningSeqs = (ui?.Seqs ?? new List<CustomSeq?>()).Where(s => s != null && Is(s.State, "running")).Select(s => s!).ToList();
        foreach (var s in runningSeqs)
            if ((s.Fed ?? s.Prompt) is double basis) ctxUsed = Math.Max(ctxUsed ?? 0, basis + (s.Out ?? 0));
        if (ctxUsed is null && recent.Count > 0) ctxUsed = recent[0].ContextTokens;

        // The video card: the NVML sample. Video memory used is the total minus the "free" part of the engine's own accounting (vram.parts).
        SystemStats? system = null;
        if (tick?.Sample != null || tick?.Vram != null)
        {
            var vram = tick?.Vram;
            double? free = vram?.Parts?.FirstOrDefault(p => p?.Name == "free")?.Bytes;
            system = new SystemStats
            {
                GpuPercent = tick?.Sample?.Util, GpuMemoryBusyPercent = tick?.Sample?.MemUtil, GpuTemperatureCelsius = tick?.Sample?.Temp,
                GpuPowerWatts = tick?.Sample?.Power, GpuPowerLimitWatts = tick?.Sample?.PowerLimit,
                GpuMemoryTotalBytes = vram?.Total, GpuMemoryUsedBytes = vram?.Total is double total && free.HasValue ? total - free : null,
                // RAM and CPU are not in /ui/state.
            };
        }

        // The KV cache: its budget is the "KV cache" part of the video memory, in use is that minus its own "free" part; the counters are the engine's.
        CacheStats? cache = null;
        var kv = tick?.Vram?.Parts?.FirstOrDefault(p => p?.Name == "KV cache");
        if (kv != null || tick?.Cache != null)
        {
            var kvFreeBytes = kv?.Parts?.FirstOrDefault(p => p?.Name == "free")?.Bytes;
            cache = new CacheStats
            {
                BudgetBytes = kv?.Bytes, UsedBytes = kv?.Bytes is double kvBytes && kvFreeBytes.HasValue ? kvBytes - kvFreeBytes : null,
                Requests = tick?.Cache?.Lookups, RequestsReused = tick?.Cache?.Hits, Evictions = tick?.Cache?.Evicted,
            };
        }

        // One row per engine slot: a running request fills one, the rest are idle. Only when the slot count is known.
        List<BatchSlot>? slots = null;
        if (batchSlotCount is double count && count > 0 && ui != null)
        {
            var held = (ui.Seqs ?? new List<CustomSeq?>())
                .Where(s => s != null && (Is(s.State, "running") || Is(s.State, "parked") || Is(s.State, "admitted"))).Select(s => s!).ToList();
            slots = new List<BatchSlot>();
            for (var i = 0; i < Math.Max(count, held.Count); i++)
            {
                if (i >= held.Count) { slots.Add(new BatchSlot { Index = i, State = "idle" }); continue; }
                var s = held[i];
                var isRunning = Is(s.State, "running");
                var reading = Is(s.Phase, "prefill");
                slots.Add(new BatchSlot
                {
                    Index = i,
                    State = isRunning ? (Is(s.Phase, "decode") ? "decoding" : reading ? "reading" : "running") : s.State!.ToLowerInvariant(),
                    PromptTokens = s.Prompt, PromptRead = s.Fed, Generated = s.Out, MaxTokens = s.MaxNew,
                    // The request's own speeds, the engine's numbers when it reports them (decode_tps, prefill_wall_tps); an older server: its decode
                    // speed over its decode time (a parked request decodes too, in turns with the others). The reading time is its prefill wall time.
                    TokensPerSecond = Is(s.Phase, "decode") ? s.DecodeTps ?? Speed(s.Out, s.DecodeSeconds) : null,
                    PrefillTokensPerSecond = reading ? s.PrefillWallTps : null,
                    ElapsedSeconds = reading ? s.PrefillWallSeconds : null,
                });
            }
        }

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = queued ?? (m.Waiting is double waiting ? (int)waiting : 0),
            Live = live, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = uiInfo?.MaxTokens, CompactAt = CompactAt(model) },
            System = system, Cache = cache, Totals = totals, Info = info, Slots = slots, Links = Links(),
            ReportedThroughput = ReadThroughputHistory(ui),
        };
    }
}
