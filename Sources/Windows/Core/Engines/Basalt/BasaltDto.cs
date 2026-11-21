using System.Text.Json.Serialization;

namespace Stratoscope.Core.Engines.Basalt;

// The documents Basalt sends (serve/service.py: metrics(), slot_table()), as typed records. Like Dto.cs: every property is nullable, null means
// the engine did not say, and a value of the wrong type is skipped. Names are the JSON keys. Only what Stratoscope shows is read.

/// <summary>Basalt's <c>GET /metrics</c> document.</summary>
public sealed record BasaltMetrics
{
    [JsonPropertyName("engine"), JsonConverter(typeof(TolerantObject<BasaltEngineDto>))] public BasaltEngineDto? Engine { get; init; }
    [JsonPropertyName("live"), JsonConverter(typeof(TolerantObject<BasaltLiveDto>))] public BasaltLiveDto? Live { get; init; }
    [JsonPropertyName("requests"), JsonConverter(typeof(TolerantList<BasaltRequestDto>))] public List<BasaltRequestDto?>? Requests { get; init; }
    [JsonPropertyName("totals"), JsonConverter(typeof(TolerantObject<BasaltTotalsDto>))] public BasaltTotalsDto? Totals { get; init; }
    [JsonPropertyName("hardware"), JsonConverter(typeof(TolerantObject<BasaltHardwareDto>))] public BasaltHardwareDto? Hardware { get; init; }
}

public sealed record BasaltTotalsDto
{
    [JsonPropertyName("since")] public double? Since { get; init; }
    [JsonPropertyName("requests")] public double? Requests { get; init; }
    [JsonPropertyName("written")] public double? Written { get; init; }
}

public sealed record BasaltEngineDto
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("max_context")] public double? MaxContext { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("kv")] public string? Kv { get; init; }
    /// <summary>The KV tokens resident in video memory (/metrics engine.kv_resident); 0 means none.</summary>
    [JsonPropertyName("kv_resident")] public double? KvResident { get; init; }
    /// <summary>The draft window. Basalt sends a number; it is kept as text.</summary>
    [JsonPropertyName("spec")] public string? Spec { get; init; }
    [JsonPropertyName("expert_cache_mib")] public double? ExpertCacheMib { get; init; }
    /// <summary>The conversation cache's budget in MiB; 0 means the cache is switched off.</summary>
    [JsonPropertyName("conversation_cache_mib")] public double? ConversationCacheMib { get; init; }
    [JsonPropertyName("conversation_cache_slots")] public double? ConversationCacheSlots { get; init; }
}

public sealed record BasaltLiveDto
{
    /// <summary>"idle", "reading", "generating", "loading", "unloaded" or "asleep".</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    /// <summary>While the engine loads: what it does and how far it is. Null otherwise.</summary>
    [JsonPropertyName("load"), JsonConverter(typeof(TolerantObject<BasaltLoadDto>))] public BasaltLoadDto? Load { get; init; }
    /// <summary>Requests waiting for the engine (the same number as <c>slots.queue_depth</c>).</summary>
    [JsonPropertyName("queued")] public double? Queued { get; init; }
    [JsonPropertyName("slots"), JsonConverter(typeof(TolerantObject<BasaltSlotCountDto>))] public BasaltSlotCountDto? Slots { get; init; }
    /// <summary>The size of the newest request's prompt (the whole prompt, also while it is still being read).</summary>
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("generated")] public double? Generated { get; init; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; init; }
    /// <summary>The generation speed over the last seconds; null unless the engine generates.</summary>
    [JsonPropertyName("tok_s")] public double? TokensPerSecond { get; init; }
    /// <summary>The mean speed of reading new prompt tokens (cached ones excluded). Basalt keeps sending it after the prompt is read.</summary>
    [JsonPropertyName("prefill_tok_s_mean")] public double? PrefillTokensPerSecondMean { get; init; }
}

public sealed record BasaltLoadDto
{
    /// <summary>"weights" or "wake".</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("percent")] public double? Percent { get; init; }
}

public sealed record BasaltSlotCountDto
{
    [JsonPropertyName("total")] public double? Total { get; init; }
    [JsonPropertyName("active")] public double? Active { get; init; }
    [JsonPropertyName("queue_depth")] public double? QueueDepth { get; init; }
}

public sealed record BasaltRequestDto
{
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    [JsonPropertyName("reused")] public double? Reused { get; init; }
    [JsonPropertyName("prompt_ms")] public double? PromptMilliseconds { get; init; }
    [JsonPropertyName("output_tokens")] public double? OutputTokens { get; init; }
    [JsonPropertyName("decode_tok_s")] public double? DecodeTokensPerSecond { get; init; }
    [JsonPropertyName("duration_s")] public double? DurationSeconds { get; init; }
    /// <summary>Share of the experts looked up that were in the expert cache.</summary>
    [JsonPropertyName("hit_rate")] public double? HitRate { get; init; }
    /// <summary>Seconds since 1970-01-01 (UTC) when the request finished.</summary>
    [JsonPropertyName("time")] public double? Time { get; init; }
    [JsonPropertyName("finish")] public string? Finish { get; init; }
}

/// <summary>Basalt nests its video cards under <c>gpus</c>; the machine numbers are beside them. Units: bytes, degrees Celsius, watts, percent.</summary>
public sealed record BasaltHardwareDto
{
    [JsonPropertyName("gpus"), JsonConverter(typeof(TolerantList<BasaltGpuDto>))] public List<BasaltGpuDto?>? Gpus { get; init; }
    [JsonPropertyName("cpu")] public double? Cpu { get; init; }
    [JsonPropertyName("ram_used")] public double? RamUsed { get; init; }
    [JsonPropertyName("ram_total")] public double? RamTotal { get; init; }
}

public sealed record BasaltGpuDto
{
    [JsonPropertyName("util")] public double? Util { get; init; }
    [JsonPropertyName("mem_used")] public double? MemUsed { get; init; }
    [JsonPropertyName("mem_total")] public double? MemTotal { get; init; }
    [JsonPropertyName("temp")] public double? Temp { get; init; }
    [JsonPropertyName("power")] public double? Power { get; init; }
    [JsonPropertyName("power_limit")] public double? PowerLimit { get; init; }
}

/// <summary>One row of Basalt's <c>GET /slots</c> (the llama.cpp shape): the request a slot runs right now.</summary>
public sealed record BasaltSlotDto
{
    [JsonPropertyName("id")] public double? Id { get; init; }
    /// <summary>"idle", "prefill" or "decoding".</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    /// <summary>The slot's generation speed over the last seconds (the engine's own window).</summary>
    [JsonPropertyName("tok_s")] public double? TokensPerSecond { get; init; }
    [JsonPropertyName("max_tokens")] public double? MaxTokens { get; init; }
    [JsonPropertyName("prompt_tokens")] public double? PromptTokens { get; init; }
    /// <summary>How much of the prompt is read. Null while the engine cannot say (several requests reading).</summary>
    [JsonPropertyName("prefill_done")] public double? PrefillDone { get; init; }
    /// <summary>The prompt tokens served from the conversation cache; null until the engine knows.</summary>
    [JsonPropertyName("reused")] public double? Reused { get; init; }
    [JsonPropertyName("generated")] public double? Generated { get; init; }
    /// <summary>The slot's prompt plus what it has generated; for an idle slot, the conversation it still holds.</summary>
    [JsonPropertyName("ctx_used")] public double? CtxUsed { get; init; }
}
