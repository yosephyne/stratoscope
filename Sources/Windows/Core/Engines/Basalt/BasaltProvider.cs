using System.Globalization;

namespace Stratoscope.Core.Engines.Basalt;

/// <summary>
/// Adapter for the Basalt local inference engine (verified against Basalt 0.3.2, behind a llama-swap router).
///
/// Data sources:
///  - the router's /running (optional), and Basalt's /metrics, reached through the router at /upstream/&lt;model&gt;/metrics
///  - Basalt's /slots (the llama.cpp shape), asked only while a request runs: /metrics gives the prompt's size, only /slots says how much of
///    it is read, and it also carries the request's token limit and its cache reuse.
/// Basalt reports completed totals in newer versions. They are kept separate from live generation totals.
/// Batch slots come from <c>/slots</c> when the engine has more than one.
///
/// History curves: Basalt sends no cumulative counter (<c>/metrics</c> has the finished requests only), so <see cref="Totals.PrefillComputedTokens"/>
/// stays empty and the curves take the slots: each decoding slot's own <c>tok_s</c> (the engine's window), and the reading slots' <c>prefill_done</c>
/// between polls (null while several read: then the live prefill mean is the last fallback).
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
/// This engine: live.tok_s. Canonical text: RoutedProvider.cs.
/// </remarks>
public sealed class BasaltProvider : RoutedProvider
{
    public const string Id = "basalt";

    public BasaltProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
        : base(config, http, clients) { }

    public override string Brand => "Basalt";

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;
        var metrics = await http.GetAsync(Endpoints.Join(engineBase, "metrics"), Json.BasaltMetricsType, cancellation).ConfigureAwait(false);
        if (!metrics.Ok) return new EngineRead(metrics.Status, false, metrics.Failure, metrics.Detail, null);
        var body = metrics.Body!;
        if (routedId is null && IsEmpty(body.Engine)) return new EngineRead(metrics.Status, true, FetchFailure.None, null, null);

        // A running request has a row worth asking for, and so has an engine with several slots (an idle slot says what it holds). A refused
        // or failed /slots just leaves the progress and the slot list unknown.
        List<BasaltSlotDto?>? slots = null;
        if (body.Live?.State is "reading" or "generating" || body.Live?.Slots?.Total > 1)
            slots = (await http.GetAsync(Endpoints.Join(engineBase, "slots"), Json.BasaltSlotsType, cancellation).ConfigureAwait(false)).Body;

        var model = routedId ?? body.Engine?.Model ?? "Basalt";
        return new EngineRead(metrics.Status, true, FetchFailure.None, null, Translate(body, slots, model));
    }

    /// <summary>Translates one /metrics document (and the /slots rows, when asked for) into a snapshot. Public so tests can feed recorded payloads.</summary>
    public EngineSnapshot Translate(BasaltMetrics m, List<BasaltSlotDto?>? slotRows, string model)
    {
        var eng = m.Engine ?? new BasaltEngineDto();
        var live = m.Live ?? new BasaltLiveDto();
        var reqs = (m.Requests ?? new List<BasaltRequestDto?>()).Where(r => r != null).Select(r => r!).ToList();

        // Status: Basalt says "reading" and "generating" and does not tell reasoning from the answer, so there is no Thinking here.
        // Anything else but "idle" means the weights are not ready for a request, and the engine's own word is shown as the reason.
        var status = live.State switch
        {
            "idle" => EngineStatus.Idle,
            "reading" => EngineStatus.Prefill,
            "generating" => EngineStatus.Decode,
            _ => EngineStatus.Loading,
        };
        if (status == EngineStatus.Loading) return Placeholder(model, status, NotReady(live));

        // The running request: the slot row when /slots was asked (it is the request that holds the engine; /metrics describes the newest
        // one, which may still wait in the queue), otherwise /metrics.
        // With several requests running together, the headline is their total, like Strata's: reading slots add up their prompts, generating
        // slots their counts, and the speed is live.tok_s (the engine already sums it over the slots).
        var rows = (slotRows ?? new List<BasaltSlotDto?>()).Where(s => s != null).Select(s => s!).ToList();
        var running = rows.Where(s => s.State is "prefill" or "decoding").ToList();
        var together = running.Count > 1;
        var row = running.FirstOrDefault();
        var reading = status == EngineStatus.Prefill;
        LiveRequest? liveRequest = null;
        if (status != EngineStatus.Idle)
        {
            var speed = live.TokensPerSecond;
            double? total = row?.PromptTokens ?? live.PromptTokens, read = row?.PrefillDone, generated = row?.Generated ?? live.Generated;
            if (together)
            {
                var readers = running.Where(s => s.State == "prefill").ToList();
                var writers = running.Where(s => s.State == "decoding").ToList();
                if (reading)
                {
                    total = readers.All(s => s.PromptTokens.HasValue) ? readers.Sum(s => s.PromptTokens!.Value) : null;
                    read = readers.All(s => s.PrefillDone.HasValue) ? readers.Sum(s => s.PrefillDone!.Value) : null;   // several readers: Basalt says nothing
                    generated = 0;
                }
                else
                {
                    generated = writers.All(s => s.Generated.HasValue) ? writers.Sum(s => s.Generated!.Value) : null;
                    total = read = null;
                }
            }
            liveRequest = new LiveRequest
            {
                PromptTotal = total, PromptRead = read, Generated = generated,
                MaxTokens = together ? null : row?.MaxTokens,
                ElapsedSeconds = live.ElapsedSeconds,
                PrefillTokensPerSecond = reading && (live.PrefillTokensPerSecondMean ?? 0) > 0 ? live.PrefillTokensPerSecondMean : null,
                DecodeTokensPerSecond = (speed ?? 0) > 0 ? speed : null,
                ReusedEstimate = together ? null : row?.Reused,      // the engine's own number once it knows it; the field also carries Strata's estimates
                // Several requests at once only when Basalt has more than one slot.
                Running = together ? (int?)running.Count : live.Slots is { Total: > 1, Active: > 1 } s ? (int)s.Active!.Value : null,
            };
        }

        // One entry per slot, for an engine with more than one: what each holds or runs. A single slot has nothing to tell apart.
        List<BatchSlot>? batchSlots = null;
        if (rows.Count > 1)
            batchSlots = rows.Select((s, position) => new BatchSlot
            {
                Index = (int)(s.Id ?? position),
                State = s.State switch { "prefill" => "reading", "decoding" => "decoding", "idle" => "idle", _ => null },
                HeldTokens = s.State == "idle" ? s.CtxUsed : null,        // an idle slot keeps its last conversation
                PromptTokens = s.State is "prefill" or "decoding" ? s.PromptTokens : null,
                PromptRead = s.State is "prefill" or "decoding" ? s.PrefillDone : null,
                Generated = s.State is "prefill" or "decoding" ? s.Generated : null,
                TokensPerSecond = s.State == "decoding" && (s.TokensPerSecond ?? 0) > 0 ? s.TokensPerSecond : null,
                MaxTokens = s.State is "prefill" or "decoding" ? s.MaxTokens : null,
            }).ToList();

        var recent = reqs.Select(q => new RequestStats
        {
            PromptTokens = q.PromptTokens, ReusedTokens = q.Reused,
            // Basalt's own definition of the tokens a request read (its "best prefill" is this over prompt_ms).
            PromptRead = q.PromptTokens is double prompt && q.Reused is double reused ? Math.Max(0, prompt - reused) : null,
            PromptMilliseconds = q.PromptMilliseconds, OutputTokens = q.OutputTokens,
            DecodeTokensPerSecond = q.DecodeTokensPerSecond, DurationSeconds = q.DurationSeconds,
            FinishedAt = FinishedAt(q.Time), Finish = q.Finish, ExpertHitRate = q.HitRate,
        }).ToList();

        // The slot closest to the limit when several run (the context bar shows how full the fullest is).
        double? ctxUsed = running.Select(s => s.CtxUsed).Where(c => c.HasValue).DefaultIfEmpty(null).Max();
        if (ctxUsed is null && live.PromptTokens is double pt && status != EngineStatus.Idle) ctxUsed = pt + (live.Generated ?? 0);
        if (ctxUsed is null && recent.Count > 0 && recent[0].PromptTokens is double p0 && recent[0].OutputTokens is double o0) ctxUsed = p0 + o0;

        // One video card is shown (Basalt reports a list; this one is the first). A machine with several would need a model of its own.
        SystemStats? system = null;
        if (!IsEmpty(m.Hardware))
        {
            var hw = m.Hardware!;
            var gpu = hw.Gpus?.FirstOrDefault(g => g != null);
            system = new SystemStats
            {
                RamUsedBytes = hw.RamUsed, RamTotalBytes = hw.RamTotal, CpuPercent = hw.Cpu,
                GpuPercent = gpu?.Util, GpuTemperatureCelsius = gpu?.Temp, GpuPowerWatts = gpu?.Power, GpuPowerLimitWatts = gpu?.PowerLimit,
                GpuMemoryUsedBytes = gpu?.MemUsed, GpuMemoryTotalBytes = gpu?.MemTotal,
            };
        }

        EngineInfo? info = null;
        if (!IsEmpty(m.Engine))
            info = new EngineInfo
            {
                Version = eng.Version, KvFormat = eng.Kv, KvResidentTokens = eng.KvResident, DraftWindow = eng.Spec, ExpertCacheMib = eng.ExpertCacheMib,
                BatchSlots = live.Slots?.Total, ExpertHitRate = reqs.Count > 0 ? reqs[0].HitRate : null,
            };

        // The conversation cache: Basalt reports its budget and slot count, not what is parked, so only those two are drawn. A budget of 0 is
        // the cache switched off: there is nothing to show.
        CacheStats? cache = null;
        if (eng.ConversationCacheMib is double budget && budget > 0)
            cache = new CacheStats { BudgetBytes = budget * 1_048_576, Slots = eng.ConversationCacheSlots is double sl ? (int)sl : null };

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = (int)(live.Queued ?? live.Slots?.QueueDepth ?? 0),
            Live = liveRequest, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = eng.MaxContext, CompactAt = CompactAt(model) },
            System = system, Cache = cache, Info = info, Slots = batchSlots, Links = Links(),
            OutputProgress = ReadOutputProgress(m, rows, status),
        };
    }

    private static OutputProgress? ReadOutputProgress(BasaltMetrics metrics, List<BasaltSlotDto> rows, EngineStatus status)
    {
        if (metrics.Totals is not { Since: double since, Requests: double finished, Written: double written }
            || !double.IsFinite(since) || !double.IsFinite(finished) || finished < 0
            || !double.IsFinite(written) || written < 0) return null;

        double? activeTokens = null;
        if (metrics.Live?.Slots is { Active: double active, Total: double total } && rows.Count == total
            && rows.All(row => row.State is "idle" or "prefill" or "decoding"))
        {
            // /slots and /metrics are separate observations. Discard an incomplete slot table.
            int activeRows = 0;
            double sum = 0;
            foreach (var row in rows)
            {
                if (row.State == "idle") continue;
                if (row.Generated is not double n || !double.IsFinite(n) || n < 0) return new OutputProgress(since, finished, written, null);
                activeRows++;
                sum += n;
            }
            if (activeRows == active) activeTokens = sum;
        }
        else if (rows.Count == 0 && status == EngineStatus.Idle && metrics.Live?.Slots is { Total: 1, Active: 0 })
            activeTokens = 0;

        return new OutputProgress(since, finished, written, activeTokens);
    }

    /// <summary>Why a request cannot start yet, from the engine's state and, while it loads, its progress.</summary>
    private static string NotReady(BasaltLiveDto live)
    {
        switch (live.State)
        {
            case "loading":
                var percent = live.Load?.Percent is double p ? " " + Math.Round(p).ToString(CultureInfo.InvariantCulture) + " %" : "";
                return live.Load?.Kind == "wake" ? "Waking up" : "Loading weights" + percent;
            case "unloaded": return "Weights not loaded";
            case "asleep": return "Asleep";
            default: return live.State is null ? "State not reported" : "State: " + live.State;
        }
    }
}
