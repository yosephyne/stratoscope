using System.Text.Json.Serialization;

namespace Stratoscope.Core;

// The documents the engines and the settings file are made of, as typed records. Every property is nullable: null means the engine did
// not say (missing key, JSON null, or a value of the wrong type), and the UI then shows a dash or nothing. Names are the JSON keys.

/// <summary>The router's <c>GET /running</c> answer (llama-swap).</summary>
public sealed record RunningResponse
{
    [JsonPropertyName("running"), JsonConverter(typeof(TolerantList<RunningModel>))] public List<RunningModel?>? Running { get; init; }
}

public sealed record RunningModel
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>Strata's <c>GET /metrics</c> document.</summary>
public sealed record StrataMetrics
{
    [JsonPropertyName("engine"), JsonConverter(typeof(TolerantObject<EngineDto>))] public EngineDto? Engine { get; init; }
    [JsonPropertyName("live"), JsonConverter(typeof(TolerantObject<LiveDto>))] public LiveDto? Live { get; init; }
    [JsonPropertyName("requests"), JsonConverter(typeof(TolerantList<RequestDto>))] public List<RequestDto?>? Requests { get; init; }
    [JsonPropertyName("hardware"), JsonConverter(typeof(TolerantObject<HardwareDto>))] public HardwareDto? Hardware { get; init; }
    [JsonPropertyName("conversation_cache"), JsonConverter(typeof(TolerantObject<CacheDto>))] public CacheDto? ConversationCache { get; init; }
    [JsonPropertyName("totals"), JsonConverter(typeof(TolerantObject<TotalsDto>))] public TotalsDto? Totals { get; init; }
}

public sealed record EngineDto
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("max_context")] public double? MaxContext { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("kv")] public string? Kv { get; init; }
    [JsonPropertyName("kv_resident")] public double? KvResident { get; init; }
    /// <summary>The draft window. Strata sends a number; it is kept as text.</summary>
    [JsonPropertyName("spec")] public string? Spec { get; init; }
    [JsonPropertyName("expert_cache_mib")] public double? ExpertCacheMib { get; init; }
    [JsonPropertyName("batch_slots")] public double? BatchSlots { get; init; }
}

public sealed record LiveDto
{
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("queued")] public double? Queued { get; init; }
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("prompt_read")] public double? PromptRead { get; init; }
    [JsonPropertyName("prompt_total")] public double? PromptTotal { get; init; }
    [JsonPropertyName("generated")] public double? Generated { get; init; }
    [JsonPropertyName("max_tokens")] public double? MaxTokens { get; init; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; init; }
    [JsonPropertyName("tok_s")] public double? TokensPerSecond { get; init; }
    [JsonPropertyName("tok_s_mean")] public double? TokensPerSecondMean { get; init; }
    [JsonPropertyName("prefill_tok_s_mean")] public double? PrefillTokensPerSecondMean { get; init; }
    [JsonPropertyName("slots"), JsonConverter(typeof(TolerantList<SlotDto>))] public List<SlotDto?>? Slots { get; init; }
    /// <summary>With batch slots: how many requests run together at most, how many run now, how many wait for the engine's control lines.</summary>
    [JsonPropertyName("parallel")] public double? Parallel { get; init; }
    [JsonPropertyName("running")] public double? Running { get; init; }
    [JsonPropertyName("outside_slots")] public double? OutsideSlots { get; init; }
    [JsonPropertyName("waiting")] public double? Waiting { get; init; }
}

public sealed record SlotDto
{
    [JsonPropertyName("slot")] public double? Slot { get; init; }
    /// <summary>"idle", "reading" or "decoding".</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("held_tokens")] public double? HeldTokens { get; init; }
    // Present while the slot runs a request (serve/server.py slots_view).
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("prompt_read")] public double? PromptRead { get; init; }
    [JsonPropertyName("generated")] public double? Generated { get; init; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; init; }
    /// <summary>The request's token limit. Strata's slots_view does not send it today (checked against serve/server.py and a live run); read when a newer engine does.</summary>
    [JsonPropertyName("max_tokens")] public double? MaxTokens { get; init; }
    // The slot's own "tok_s" is not read: it divides the generated count, which includes tokens from before the first token, by the time
    // since the first token, so it is far too high while another request reads. The speed comes from the count between two polls.
}

public sealed record RequestDto
{
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("reused")] public double? Reused { get; init; }
    [JsonPropertyName("prompt_read")] public double? PromptRead { get; init; }
    [JsonPropertyName("prompt_ms")] public double? PromptMilliseconds { get; init; }
    [JsonPropertyName("output_tokens")] public double? OutputTokens { get; init; }
    [JsonPropertyName("decode_tok_s")] public double? DecodeTokensPerSecond { get; init; }
    [JsonPropertyName("duration_s")] public double? DurationSeconds { get; init; }
    [JsonPropertyName("hit_rate")] public double? HitRate { get; init; }
    /// <summary>Seconds since 1970-01-01 (UTC) when the request finished.</summary>
    [JsonPropertyName("time")] public double? Time { get; init; }
    [JsonPropertyName("finish")] public string? Finish { get; init; }
}

public sealed record HardwareDto
{
    [JsonPropertyName("ram_used")] public double? RamUsed { get; init; }
    [JsonPropertyName("ram_total")] public double? RamTotal { get; init; }
    [JsonPropertyName("gpu_util")] public double? GpuUtil { get; init; }
    [JsonPropertyName("cpu")] public double? Cpu { get; init; }
    [JsonPropertyName("gpu_temp")] public double? GpuTemp { get; init; }
    [JsonPropertyName("gpu_power")] public double? GpuPower { get; init; }
    [JsonPropertyName("gpu_power_limit")] public double? GpuPowerLimit { get; init; }
    [JsonPropertyName("gpu_mem_used")] public double? GpuMemUsed { get; init; }
    [JsonPropertyName("gpu_mem_total")] public double? GpuMemTotal { get; init; }
}

public sealed record CacheDto
{
    [JsonPropertyName("budget_mib")] public double? BudgetMib { get; init; }
    [JsonPropertyName("bytes")] public double? Bytes { get; init; }
    [JsonPropertyName("slots")] public double? Slots { get; init; }
    [JsonPropertyName("parked")] public double? Parked { get; init; }
    [JsonPropertyName("requests")] public double? Requests { get; init; }
    [JsonPropertyName("requests_reused")] public double? RequestsReused { get; init; }
    [JsonPropertyName("evictions")] public double? Evictions { get; init; }
}

public sealed record TotalsDto
{
    [JsonPropertyName("since")] public double? Since { get; init; }
    [JsonPropertyName("requests")] public double? Requests { get; init; }
    [JsonPropertyName("output_tokens")] public double? OutputTokens { get; init; }
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("reused")] public double? Reused { get; init; }
    [JsonPropertyName("prompt_ms")] public double? PromptMilliseconds { get; init; }
    [JsonPropertyName("decode_ms")] public double? DecodeMilliseconds { get; init; }
}

