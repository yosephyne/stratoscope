namespace Stratoscope.Core.Engines.Strata;

/// <summary>
/// Adapter for the Strata local inference engine. Port of StrataProvider.swift.
///
/// Data sources:
///  - the router's /running (optional, when a llama-swap style router sits in front of Strata)
///  - the Strata server's /metrics, reached through the router at /upstream/&lt;model&gt;/metrics
///  - the Strata log, for the parked-conversation list that /metrics does not name
///
/// History curves: <c>totals</c> are added once when a request ends (serve/server.py, the request-finished block that does <c>t["output_tokens"] += n</c>),
/// so no counter of /metrics advances during a request and <see cref="Totals.PrefillComputedTokens"/> stays empty. The curves take the batch slots
/// (<c>live.slots[]</c>: <c>prompt_read</c> per chunk, <c>generated</c> per token), each decoding slot's speed derived between polls. The v1 telemetry
/// counter (<c>rates.output_tokens_server_counter</c>, completed plus in-flight tokens) is a separate, heavier endpoint and is not read.
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
/// This engine: live.tok_s (prefill: live.prefill_tok_s_mean). Canonical text: RoutedProvider.cs.
/// </remarks>
public sealed class StrataProvider : RoutedProvider
{
    public const string Id = "strata";

    private readonly StrataCacheLog cacheLog;
    private readonly Func<DateTime> clock;
    private (double Read, DateTime At)? lastRead;

    /// <param name="clients">Where to look for the summarise threshold of the AI clients that use this engine. Null (the default) reads no client config.</param>
    public StrataProvider(EngineConfig config, IJsonSource http, Func<DateTime>? clock = null, ClientConfigReader? clients = null)
        : base(config, http, clients)
    {
        this.clock = clock ?? (() => DateTime.UtcNow);
        cacheLog = new StrataCacheLog(this.clock);
    }

    public override string Brand => "Strata";

    // MARK: Polling

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;
        var metrics = await http.GetAsync(Endpoints.Join(engineBase, "metrics"), Json.MetricsType, cancellation).ConfigureAwait(false);
        if (!metrics.Ok) return new EngineRead(metrics.Status, false, metrics.Failure, metrics.Detail, null);
        if (routedId is null)
            return new EngineRead(metrics.Status, true, FetchFailure.None, null, IsEmpty(metrics.Body!.Engine) ? null : Translate(metrics.Body, metrics.Body.Engine!.Model ?? "Strata"));
        return new EngineRead(metrics.Status, true, FetchFailure.None, null, Translate(metrics.Body!, routedId));   // the id finds the client config of this model
    }

    // MARK: Translation

    /// <summary>The speed of each generating slot from its generated count between polls (see <see cref="SlotRateTracker"/>).</summary>
    private readonly SlotRateTracker slotSpeeds = new();

    /// <summary>Translates one /metrics document into a snapshot. Public so tests can feed recorded payloads.</summary>
    public EngineSnapshot Translate(StrataMetrics m, string model)
    {
        var eng = m.Engine ?? new EngineDto();
        var live = m.Live ?? new LiveDto();
        var reqs = (m.Requests ?? new List<RequestDto?>()).Where(r => r != null).Select(r => r!).ToList();

        // Status
        var state = live.State ?? "idle";
        var phase = live.Phase ?? "";
        var reading = state != "idle" && (phase.Contains("prompt") || (live.Generated ?? 0) == 0);
        var status = state == "idle" ? EngineStatus.Idle : reading ? EngineStatus.Prefill : phase == "thinking" ? EngineStatus.Thinking : EngineStatus.Decode;

        // Requests that run together in batch slots. live.tok_s is already the total of all generating requests (serve/server.py _tok_s), but
        // it is null while the newest request is still reading its prompt, and live.prompt_read describes that one request only; the slots
        // carry the rest. A slot's own tok_s is a mean over a time base that includes tokens generated before it, so it is not used: each
        // slot's speed is derived from its generated count between polls. While any slot generates, that is the state (the headline); the
        // reading slots then do not show. One running request is the single-request case and is left as it is.
        var rawSlots = (live.Slots ?? new List<SlotDto?>()).Where(s => s != null).Select(s => s!).ToList();
        var running = rawSlots.Where(s => s.State == "reading" || s.State == "decoding").ToList();
        var together = running.Count > 1;
        var decoding = running.Where(s => s.State == "decoding").ToList();
        var readingSlots = running.Where(s => s.State == "reading").ToList();
        if (together && state != "idle")
        {
            reading = decoding.Count == 0;
            status = reading ? EngineStatus.Prefill : EngineStatus.Decode;
        }

        // Prefill speed: Strata's mean, otherwise derived from the progress between two polls.
        var prefill = live.PrefillTokensPerSecondMean;
        var read = live.PromptRead;
        if (together && reading) { read = readingSlots.Sum(s => s.PromptRead ?? 0); prefill = null; }
        var now = clock();
        if ((prefill ?? 0) <= 0 && reading && read is double r1 && lastRead is (double r0, DateTime t0) && r1 > r0 && now > t0)
            prefill = (r1 - r0) / (now - t0).TotalSeconds;
        if (reading && read is double r2)
        {
            if (lastRead is null || r2 != lastRead.Value.Read) lastRead = (r2, now);
        }
        else lastRead = null;

        // Cache reuse of the running request is not reported directly: when it starts, prompt_read already sits at
        // the reused part; later, read = mean prefill speed x elapsed time. Approximate, and shown with an "about" sign.
        double? reused = null;
        if (reading && read is double r3)
        {
            var mean = live.PrefillTokensPerSecondMean ?? 0;
            var elapsed = live.ElapsedSeconds ?? 0;
            reused = mean > 0 && elapsed > 1 ? Math.Max(0, r3 - mean * elapsed) : r3;
            if (together) reused = null;      // the cache verdict is not known per slot
        }

        var slotRates = slotSpeeds.Update(decoding.Where(s => s.Slot.HasValue).Select(s => ((int)s.Slot!.Value, s.Generated)), now);
        LiveRequest? liveRequest = null;
        if (state != "idle")
        {
            var t = live.TokensPerSecond;
            var decodeSpeed = (t ?? 0) > 0 ? t : live.TokensPerSecondMean;
            double? total = live.PromptTotal, generated = live.Generated;
            if (together)
            {
                if (reading) total = readingSlots.Sum(s => s.PromptTokens ?? 0);
                generated = decoding.Sum(s => s.Generated ?? 0);
                if ((t ?? 0) <= 0)        // the engine has no total while the newest request reads: the sum of the derived slot speeds
                {
                    var rates = decoding.Select(s => slotRates.GetValueOrDefault((int)(s.Slot ?? -1))).Where(r => r > 0).ToList();
                    decodeSpeed = rates.Count > 0 ? rates.Sum() : null;
                }
                else decodeSpeed = t;
            }
            liveRequest = new LiveRequest
            {
                PromptRead = read, PromptTotal = total, Generated = generated,
                MaxTokens = live.MaxTokens, ElapsedSeconds = live.ElapsedSeconds,
                PrefillTokensPerSecond = prefill,
                DecodeTokensPerSecond = decodeSpeed,
                ReusedEstimate = reused,
                Running = together || live.Running > 1 ? (int)Math.Max(running.Count, live.Running ?? 0) : null,
                Waiting = live.Waiting is double w && w > 0 ? (int)w : null,
            };
        }

        var recent = reqs.Select(q => new RequestStats
        {
            PromptTokens = q.PromptTokens, ReusedTokens = q.Reused, PromptRead = q.PromptRead,
            PromptMilliseconds = q.PromptMilliseconds, OutputTokens = q.OutputTokens,
            DecodeTokensPerSecond = q.DecodeTokensPerSecond, DurationSeconds = q.DurationSeconds,
            FinishedAt = FinishedAt(q.Time),
            Finish = q.Finish, ExpertHitRate = q.HitRate,
        }).ToList();

        double? ctxUsed = live.PromptTokens is double pt ? pt + (live.Generated ?? 0) : null;
        if (ctxUsed is null && recent.Count > 0 && recent[0].PromptTokens is double p0 && recent[0].OutputTokens is double o0) ctxUsed = p0 + o0;

        SystemStats? system = null;
        if (!IsEmpty(m.Hardware))
        {
            var hw = m.Hardware!;
            system = new SystemStats
            {
                RamUsedBytes = hw.RamUsed, RamTotalBytes = hw.RamTotal, GpuPercent = hw.GpuUtil, CpuPercent = hw.Cpu,
                GpuTemperatureCelsius = hw.GpuTemp, GpuPowerWatts = hw.GpuPower, GpuPowerLimitWatts = hw.GpuPowerLimit, GpuMemoryUsedBytes = hw.GpuMemUsed, GpuMemoryTotalBytes = hw.GpuMemTotal,
            };
        }

        CacheStats? cache = null;
        if (!IsEmpty(m.ConversationCache))
        {
            var cc = m.ConversationCache!;
            cacheLog.Update(CurrentLogPath());
            var entries = new List<CacheEntry>(cacheLog.Entries);
            int? parked = cc.Parked is double pk ? (int)pk : cacheLog.Readable ? entries.Count : null;
            while (parked is int pn && entries.Count > pn && entries.Count > 0) entries.RemoveAt(0);
            cache = new CacheStats
            {
                BudgetBytes = cc.BudgetMib * 1_048_576, UsedBytes = cc.Bytes,
                Slots = cc.Slots is double sl ? (int)sl : null, Parked = parked,
                Requests = cc.Requests, RequestsReused = cc.RequestsReused, Evictions = cc.Evictions,
                Entries = cacheLog.Readable ? entries : new List<CacheEntry>(), EntriesKnown = cacheLog.Readable,
            };
        }

        Totals? totals = null;
        if (!IsEmpty(m.Totals))
        {
            var tot = m.Totals!;
            totals = new Totals
            {
                Requests = tot.Requests, OutputTokens = tot.OutputTokens, PromptTokens = tot.PromptTokens, ReusedTokens = tot.Reused,
                PromptMilliseconds = tot.PromptMilliseconds, DecodeMilliseconds = tot.DecodeMilliseconds,
                Since = tot.Since is double since ? DateTime.UnixEpoch.AddSeconds(since) : null,
            };
        }

        EngineInfo? info = null;
        if (!IsEmpty(m.Engine))
            info = new EngineInfo
            {
                Version = eng.Version, KvFormat = eng.Kv, KvResidentTokens = eng.KvResident,
                DraftWindow = eng.Spec, ExpertCacheMib = eng.ExpertCacheMib, BatchSlots = eng.BatchSlots,
                ExpertHitRate = reqs.Count > 0 ? reqs[0].HitRate : null,
            };

        List<BatchSlot>? slots = null;
        if (rawSlots.Count > 0)
            slots = rawSlots.Select(s => new BatchSlot
            {
                Index = (int)(s.Slot ?? 0), State = s.State, HeldTokens = s.HeldTokens,
                PromptTokens = s.PromptTokens, PromptRead = s.PromptRead, Generated = s.Generated, TokensPerSecond = slotRates.TryGetValue((int)(s.Slot ?? -1), out var rate) ? rate : null, ElapsedSeconds = s.ElapsedSeconds, MaxTokens = s.MaxTokens,
            }).ToList();

        return new EngineSnapshot("Strata", model, status)
        {
            Queued = (int)(live.Queued ?? 0),
            Live = liveRequest, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = eng.MaxContext, CompactAt = CompactAt(model) },   // null = the engine did not say
            System = system, Cache = cache, Totals = totals, Info = info, Slots = slots, Links = Links(),
            OutputProgress = ReadOutputProgress(m.Totals, live, rawSlots, running),
        };
    }

    private static OutputProgress? ReadOutputProgress(TotalsDto? totals, LiveDto live, List<SlotDto> slots, List<SlotDto> running)
    {
        if (totals is not { Since: double since, Requests: double finished, OutputTokens: double written }
            || !double.IsFinite(since) || !double.IsFinite(finished) || finished < 0
            || !double.IsFinite(written) || written < 0) return null;

        // In some configurations the engine runs requests outside this table. A complete set is required to sum live progress.
        double? active = null;
        if (live.OutsideSlots == 0 && live.Running == running.Count && live.Parallel == slots.Count && slots.Count > 0
            && slots.All(slot => slot.State is "idle" or "reading" or "decoding")
            && running.All(slot => slot.Generated is double n && double.IsFinite(n) && n >= 0))
            active = running.Sum(slot => slot.Generated!.Value);
        return new OutputProgress(since, finished, written, active);
    }
}
