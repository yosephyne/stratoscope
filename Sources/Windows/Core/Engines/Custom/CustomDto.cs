using System.Text.Json.Serialization;

namespace Stratoscope.Core.Engines.Custom;

/// <summary>
/// <c>GET /health</c> of the custom 27B server (frontend/server.py <c>do_GET</c>): <c>{"status":"ok"}</c> when the engine is ready, 200 with
/// <c>unloaded</c> or <c>loading</c> after an idle unload (the service is up; a request loads the engine), 503 while the engine is not ready.
/// </summary>
public sealed record CustomHealth
{
    [JsonPropertyName("status")] public string? Status { get; init; }
}

/// <summary>
/// The part of the custom 27B server's <c>GET /ui/state</c> that Stratoscope reads (server.py: <c>dict(HUB.init(), tick=HUB.tick())</c>, stats.py):
/// <c>info</c> (the engine settings and the GPU name), <c>log</c> (the last 500 events; the <c>done</c> ones carry a finished request) and <c>tick</c>
/// (the live part: seqs, vram, cache, sample, sched). The answer also carries the sample history (about 150 KB in all).
/// </summary>
public sealed record CustomUiState
{
    [JsonPropertyName("info")] public CustomInfo? Info { get; init; }
    [JsonPropertyName("history")] public List<CustomSample?>? History { get; init; }
    [JsonPropertyName("log")] public List<CustomLogEntry?>? Log { get; init; }
    [JsonPropertyName("tick")] public CustomTick? Tick { get; init; }

    /// <summary>The per-request list <c>tick.seqs</c> (worker.py <c>Scheduler.stats()</c>).</summary>
    [JsonIgnore] public List<CustomSeq?>? Seqs => Tick?.Seqs;
}

/// <summary><c>info</c> of <c>/ui/state</c> (stats.py <c>Hub.info</c>, server.py). <c>max_tokens</c> is the context the server accepts (<c>max_model_len</c> of <c>/v1/models</c>).</summary>
public sealed record CustomInfo
{
    [JsonPropertyName("max_tokens")] public double? MaxTokens { get; init; }
    [JsonPropertyName("max_seqs")] public double? MaxSeqs { get; init; }
    /// <summary>True when speculative decoding runs (any drafter; older servers report only this).</summary>
    [JsonPropertyName("spec")] public bool? Spec { get; init; }
    /// <summary>The drafter the engine runs (frontend launch.drafter_name): lookup, mtp+lookup, dflash2, dflash2+dspine, dspine or none.</summary>
    [JsonPropertyName("drafter")] public string? Drafter { get; init; }
    [JsonPropertyName("kv_dtype")] public string? KvDtype { get; init; }
    [JsonPropertyName("gpu")] public string? Gpu { get; init; }
}

/// <summary><c>tick</c> of <c>/ui/state</c> (stats.py <c>Hub.tick()</c>).</summary>
public sealed record CustomTick
{
    [JsonPropertyName("t")] public double? T { get; init; }
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("seqs")] public List<CustomSeq?>? Seqs { get; init; }
    [JsonPropertyName("vram")] public CustomVram? Vram { get; init; }
    [JsonPropertyName("cache")] public CustomCache? Cache { get; init; }
    [JsonPropertyName("sample")] public CustomSample? Sample { get; init; }
    [JsonPropertyName("sched")] public CustomSched? Sched { get; init; }
}

/// <summary>The latest NVML sample (stats.py <c>Nvml.sample</c>).</summary>
public sealed record CustomSample
{
    [JsonPropertyName("t")] public double? T { get; init; }
    [JsonPropertyName("util")] public double? Util { get; init; }
    [JsonPropertyName("mem_util")] public double? MemUtil { get; init; }
    [JsonPropertyName("power")] public double? Power { get; init; }
    [JsonPropertyName("power_limit")] public double? PowerLimit { get; init; }
    [JsonPropertyName("temp")] public double? Temp { get; init; }
    /// <summary>The engine's total decode rate over all requests (stats.py: generated tokens per second of wall time between samples).</summary>
    [JsonPropertyName("decode_tps")] public double? DecodeTps { get; init; }
    /// <summary>The engine's total prefill rate over all requests (stats.py: prompt tokens per second of wall time between samples).</summary>
    [JsonPropertyName("prefill_tps")] public double? PrefillTps { get; init; }
    /// <summary>Newer servers: prompt tokens per second of the time the engine spent on prompt work in the sample (stats.py <c>phase_rates</c>); 0 when none ran.</summary>
    [JsonPropertyName("prefill_busy_tps")] public double? PrefillBusyTps { get; init; }
    /// <summary>Newer servers: decode-step tokens per second of the time the engine spent on decode steps in the sample; 0 when none ran.</summary>
    [JsonPropertyName("decode_busy_tps")] public double? DecodeBusyTps { get; init; }
}

public sealed record CustomSched
{
    [JsonPropertyName("target_seqs")] public double? TargetSeqs { get; init; }
}

/// <summary>The engine's cache counters (worker.py <c>Scheduler.stats()</c> <c>cache</c>).</summary>
public sealed record CustomCache
{
    [JsonPropertyName("lookups")] public double? Lookups { get; init; }
    [JsonPropertyName("hits")] public double? Hits { get; init; }
    [JsonPropertyName("evicted")] public double? Evicted { get; init; }
    [JsonPropertyName("checkpoints")] public double? Checkpoints { get; init; }
    [JsonPropertyName("free_pages")] public double? FreePages { get; init; }
    [JsonPropertyName("checkpoint_only_pages")] public double? CheckpointOnlyPages { get; init; }
    [JsonPropertyName("pool_pages")] public double? PoolPages { get; init; }
}

public sealed record CustomVram
{
    [JsonPropertyName("total")] public double? Total { get; init; }
    [JsonPropertyName("parts")] public List<CustomVramPart?>? Parts { get; init; }
    [JsonPropertyName("kv_page_tokens")] public double? KvPageTokens { get; init; }
    [JsonPropertyName("slots")] public double? Slots { get; init; }
}

/// <summary>A named part of the video memory in bytes: weights, KV cache (with its own parts), ..., free.</summary>
public sealed record CustomVramPart
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("bytes")] public double? Bytes { get; init; }
    [JsonPropertyName("parts")] public List<CustomVramPart?>? Parts { get; init; }
}

/// <summary>One log event; only a <c>done</c> one has a <c>rec</c> (stats.py <c>done()</c>).</summary>
public sealed record CustomLogEntry
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    /// <summary>Unix seconds of the event.</summary>
    [JsonPropertyName("t")] public double? T { get; init; }
    [JsonPropertyName("rec")] public CustomDone? Rec { get; init; }
}

/// <summary>The record of a finished request.</summary>
public sealed record CustomDone
{
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")] public double? CompletionTokens { get; init; }
    [JsonPropertyName("cached_tokens")] public double? CachedTokens { get; init; }
    [JsonPropertyName("computed_tokens")] public double? ComputedTokens { get; init; }
    [JsonPropertyName("total")] public double? Total { get; init; }
    [JsonPropertyName("prefill_s")] public double? PrefillSeconds { get; init; }
    [JsonPropertyName("decode_tps")] public double? DecodeTps { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>One request of <c>/ui/state</c> <c>seqs</c>. Null means the field was missing, null or of another type.</summary>
public sealed record CustomSeq
{
    [JsonPropertyName("rid")] public string? Rid { get; init; }
    /// <summary>waiting, running, parked or admitted.</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    /// <summary>The engine's own phase: prefill or decode.</summary>
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("prompt")] public double? Prompt { get; init; }
    /// <summary>Prompt tokens the engine has run (capped at the prompt).</summary>
    [JsonPropertyName("fed")] public double? Fed { get; init; }
    [JsonPropertyName("out")] public double? Out { get; init; }
    [JsonPropertyName("max_new")] public double? MaxNew { get; init; }
    /// <summary>Prompt tokens actually prefilled, not restored from the cache.</summary>
    [JsonPropertyName("computed")] public double? Computed { get; init; }
    [JsonPropertyName("prefill_s")] public double? PrefillSeconds { get; init; }
    [JsonPropertyName("decode_s")] public double? DecodeSeconds { get; init; }
    // The request's own speeds as the engine computes them (worker.py stats(); newer servers), live while the phase runs:
    /// <summary>computed / prefill_s: its prompt tokens over the time of its own prefill turns.</summary>
    [JsonPropertyName("prefill_tps")] public double? PrefillTps { get; init; }
    /// <summary>The time from its prompt's start to its first token (to now while it reads), other requests' turns included.</summary>
    [JsonPropertyName("prefill_wall_s")] public double? PrefillWallSeconds { get; init; }
    /// <summary>computed / prefill_wall_s.</summary>
    [JsonPropertyName("prefill_wall_tps")] public double? PrefillWallTps { get; init; }
    /// <summary>(out - 1) / decode_wall: the tokens after the first one over the time since the first one.</summary>
    [JsonPropertyName("decode_tps")] public double? DecodeTps { get; init; }
}

/// <summary>
/// The numbers of the custom 27B server's <c>GET /metrics</c> (Prometheus text, series prefix <c>q27:</c>; worker.py <c>metrics()</c>). Null means the series
/// was not in the answer. The scheduler gauges are read at the time of the request; the totals count since the engine started.
/// </summary>
public sealed record CustomMetrics
{
    public const string Prefix = "q27:";

    public double? Running { get; init; }
    public double? Waiting { get; init; }
    /// <summary>The most sequences the engine was built to run together (not the configured target, <c>target_seqs</c>).</summary>
    public double? EngineSeqs { get; init; }
    public double? KvFreePages { get; init; }
    public double? KvCheckpointOnlyPages { get; init; }
    public double? PrefixCacheCheckpoints { get; init; }
    public double? PrefillTokensTotal { get; init; }
    /// <summary>The phase clocks of newer servers (seconds of prompt work, seconds of decode steps, tokens of decode steps); null on older ones.</summary>
    public double? PrefillBusySecondsTotal { get; init; }
    public double? DecodeBusySecondsTotal { get; init; }
    public double? DecodeStepTokensTotal { get; init; }
    public double? PromptTokensTotal { get; init; }
    public double? CachedTokensTotal { get; init; }
    public double? GenerationTokensTotal { get; init; }
    public double? RequestsTotal { get; init; }
    /// <summary>1 while the scheduler holds the first waiting request back for lack of KV pages (<c>admission_blocked{reason="kv"}</c>).</summary>
    public double? BlockedKv { get; init; }
    /// <summary>1 while it holds it back for the minimum per-sequence speed (<c>admission_blocked{reason="speed"}</c>).</summary>
    public double? BlockedSpeed { get; init; }

    /// <summary>Null when the text has no <c>q27:</c> series at all: that is no custom 27B server.</summary>
    public static CustomMetrics? Parse(string? text)
    {
        var values = PrometheusText.Parse(text, Prefix);
        if (values.Count == 0) return null;
        double? V(string name) => values.TryGetValue(name, out var v) ? v : null;
        return new CustomMetrics
        {
            Running = V("num_requests_running"), Waiting = V("num_requests_waiting"), EngineSeqs = V("engine_seqs"),
            KvFreePages = V("kv_free_pages"), KvCheckpointOnlyPages = V("kv_checkpoint_only_pages"), PrefixCacheCheckpoints = V("prefix_cache_checkpoints"),
            PrefillTokensTotal = V("prefill_tokens_total"), PromptTokensTotal = V("prompt_tokens_total"), CachedTokensTotal = V("cached_tokens_total"),
            PrefillBusySecondsTotal = V("prefill_busy_seconds_total"), DecodeBusySecondsTotal = V("decode_busy_seconds_total"),
            DecodeStepTokensTotal = V("decode_step_tokens_total"),
            GenerationTokensTotal = V("generation_tokens_total"), RequestsTotal = V("requests_total"),
            BlockedKv = V("admission_blocked{reason=\"kv\"}"), BlockedSpeed = V("admission_blocked{reason=\"speed\"}"),
        };
    }
}
