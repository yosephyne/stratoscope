namespace Stratoscope.Core;

// The engine-neutral data model, the C# twin of Snapshot.swift. Every engine adapter translates its own
// metrics into an EngineSnapshot; the UI only ever sees this. A number the engine did not report is null
// ("unknown"); a section an engine cannot provide is null and that part of the panel is not drawn.

public enum EngineStatus
{
    /// <summary>The model is starting; <see cref="EngineSnapshot.LoadingReason"/> says why we are waiting.</summary>
    Loading,
    Idle,
    /// <summary>Requests run, and the engine reports neither reading nor writing (no evidence of the phase, no speed).</summary>
    Running,
    /// <summary>Reading the prompt.</summary>
    Prefill,
    /// <summary>Generating reasoning tokens.</summary>
    Thinking,
    /// <summary>Generating the answer.</summary>
    Decode,
    /// <summary>The router answers but has no model loaded.</summary>
    NoModel,
    /// <summary>The router answers but the engine behind it does not; <see cref="EngineSnapshot.LoadingReason"/> says why.</summary>
    Error,
}

/// <summary>The request that is running right now.</summary>
public sealed class LiveRequest
{
    public double? PromptRead { get; set; }
    public double? PromptTotal { get; set; }
    public double? Generated { get; set; }
    public double? MaxTokens { get; set; }
    public double? ElapsedSeconds { get; set; }
    /// <summary>The reading speed. The engine's total over all requests when several run; see RoutedProvider.cs.</summary>
    public double? PrefillTokensPerSecond { get; set; }
    /// <summary>The speed of the generation. The engine's total over all requests when several run; see RoutedProvider.cs.</summary>
    public double? DecodeTokensPerSecond { get; set; }
    /// <summary>How many requests run together; null or 1 for a single request.</summary>
    public int? Running { get; set; }
    /// <summary>Requests the engine holds back for its control lines, or null.</summary>
    public int? Waiting { get; set; }
    public bool IsParallel => Running is int r && r > 1;

    /// <summary>Prompt tokens served from cache. Strata does not report this for a running request, so it is derived: approximate.</summary>
    public double? ReusedEstimate { get; set; }

    /// <summary>Prefill progress, 0..100, or null while the engine has not reported the prompt size.</summary>
    public int? Percent =>
        PromptTotal is double total && total > 0 && PromptRead is double read ? Math.Min(100, (int)(read / total * 100)) : (int?)null;
}

/// <summary>A finished request.</summary>
public sealed class RequestStats
{
    public double? PromptTokens { get; set; }
    public double? ReusedTokens { get; set; }
    public double? PromptRead { get; set; }
    public double? PromptMilliseconds { get; set; }
    public double? OutputTokens { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>When the request finished (UTC), if the engine says.</summary>
    public DateTime? FinishedAt { get; set; }
    /// <summary>How it ended ("stop", "length", ...), as the engine names it.</summary>
    public string? Finish { get; set; }
    /// <summary>Expert-cache hit rate of this request, 0..1.</summary>
    public double? ExpertHitRate { get; set; }

    /// <summary>Size of the conversation after this request: the prompt plus the answer. Null when either number is unknown.</summary>
    public double? ContextTokens => PromptTokens is double prompt && OutputTokens is double output ? prompt + output : (double?)null;

    /// <summary>
    /// This request's reading speed: the prompt tokens it computed over the time the engine took to read them (custom 27B: computed_tokens over
    /// prefill_s, its own prefill turns). No minimum: every request that read at least one token in a measured time above zero has one; null
    /// only when nothing was read (all of the prompt came from the cache) or the engine did not report the time.
    /// </summary>
    public double? PrefillTokensPerSecond() =>
        PromptMilliseconds is double ms && PromptRead is double read && ms > 0 && read > 0 && double.IsFinite(ms) && double.IsFinite(read)
            ? read / (ms / 1000) : (double?)null;

    /// <summary>The cache verdict of this request, or null when the engine did not report prompt and reuse.</summary>
    public CacheHit? Hit => CacheHits.Classify(PromptTokens, ReusedTokens);
}

public sealed class ContextUsage
{
    public double? Used { get; set; }
    /// <summary>The maximum context, or null when the engine did not say.</summary>
    public double? Max { get; set; }
    /// <summary>
    /// The token count where the client summarises or trims the conversation, drawn as a marker on the context bar. Null = nobody knows it:
    /// no marker. Never guessed: it comes from a client config that talks to the engine, or from the user's own setting.
    /// </summary>
    public double? CompactAt { get; set; }

    /// <summary>Room left below the engine's limit, or null when either number is unknown.</summary>
    public double? Free => Max is double max && max > 0 && Used is double used ? Math.Max(0, max - used) : (double?)null;
}

public sealed class SystemStats
{
    public double? RamUsedBytes { get; set; }
    public double? RamTotalBytes { get; set; }
    public double? GpuPercent { get; set; }
    public double? CpuPercent { get; set; }
    public double? GpuTemperatureCelsius { get; set; }
    public double? GpuPowerWatts { get; set; }
    public double? GpuPowerLimitWatts { get; set; }
    public double? GpuMemoryUsedBytes { get; set; }
    public double? GpuMemoryTotalBytes { get; set; }
    /// <summary>Share of time the video memory interface was busy, 0..100 (NVML memory utilisation; custom 27B server), or null.</summary>
    public double? GpuMemoryBusyPercent { get; set; }
}

/// <summary>One parked conversation in the engine's conversation cache.</summary>
public sealed class CacheEntry
{
    public CacheEntry(int id, long tokens, double bytes, DateTime? parkedAt = null)
    {
        Id = id; Tokens = tokens; Bytes = bytes; ParkedAt = parkedAt;
    }
    public int Id { get; }
    public long Tokens { get; }
    public double Bytes { get; }
    public DateTime? ParkedAt { get; }
}

public sealed class CacheStats
{
    public double? BudgetBytes { get; set; }
    public double? UsedBytes { get; set; }
    public int? Slots { get; set; }
    public int? Parked { get; set; }
    public double? Requests { get; set; }
    public double? RequestsReused { get; set; }
    public double? Evictions { get; set; }
    /// <summary>The parked conversations as far as the engine's log tells. Empty without a readable log.</summary>
    public List<CacheEntry> Entries { get; set; } = new List<CacheEntry>();
    /// <summary>True when <see cref="Entries"/> was read from a log, so an empty slot really is free.</summary>
    public bool EntriesKnown { get; set; }

    /// <summary>
    /// Slot rows are listed newest first. A row past the known entries but within <see cref="Parked"/> is a conversation the engine reports
    /// but we have no details for: not free.
    /// </summary>
    public bool IsParkedWithoutDetails(int row) => row >= Entries.Count && row < (Parked ?? 0);
}

public sealed class Totals
{
    public double? Requests { get; set; }
    public double? OutputTokens { get; set; }
    public double? PromptTokens { get; set; }
    public double? ReusedTokens { get; set; }
    /// <summary>
    /// Prompt tokens computed so far, cumulative since the engine started and not counting the ones served from cache. Only for a counter that advances
    /// while a request reads (per chunk), never one that is added once at the start of a request; null otherwise. The history graph takes the prefill speed
    /// from its delta between polls.
    /// </summary>
    public double? PrefillComputedTokens { get; set; }
    public double? PromptMilliseconds { get; set; }
    public double? DecodeMilliseconds { get; set; }
    public DateTime? Since { get; set; }
}

/// <summary>Engine settings worth showing (optional; an engine that reports none gets no such section).</summary>
public sealed class EngineInfo
{
    public string? Version { get; set; }
    public string? KvFormat { get; set; }
    public double? KvResidentTokens { get; set; }
    public string? DraftWindow { get; set; }
    public double? ExpertCacheMib { get; set; }
    public double? BatchSlots { get; set; }
    /// <summary>Hit rate (0..1) of the newest finished request.</summary>
    public double? ExpertHitRate { get; set; }
    /// <summary>KV cache pages not in use (custom 27B server), or null.</summary>
    public double? KvFreePages { get; set; }
    /// <summary>KV pages that only a prefix cache checkpoint holds (custom 27B server), or null.</summary>
    public double? KvCheckpointOnlyPages { get; set; }
    /// <summary>Checkpoints in the prefix cache (custom 27B server), or null.</summary>
    public double? PrefixCheckpoints { get; set; }
    /// <summary>Why the scheduler holds a waiting request back: "kv", "speed", or "none" when it reports no block. Null when unknown.</summary>
    public string? AdmissionBlocked { get; set; }
    /// <summary>The name of the video card the engine runs on (custom 27B server), or null.</summary>
    public string? GpuName { get; set; }
    /// <summary>The number of requests the scheduler aims to run together (custom 27B server, <c>target_seqs</c>), or null.</summary>
    public double? TargetSeqs { get; set; }
}

/// <summary>One slot of a batching engine.</summary>
public sealed class BatchSlot
{
    public int Index { get; set; }
    public string? State { get; set; }
    public double? HeldTokens { get; set; }
    // The running request of the slot (null while idle).
    public double? PromptTokens { get; set; }
    public double? PromptRead { get; set; }
    public double? Generated { get; set; }
    /// <summary>The slot's own decode speed: tokens generated over the time since its first token.</summary>
    public double? TokensPerSecond { get; set; }
    /// <summary>The slot's own prefill speed while it reads, when the engine reports one (custom: prompt tokens computed over its prefill wall time).</summary>
    public double? PrefillTokensPerSecond { get; set; }
    public double? ElapsedSeconds { get; set; }
    /// <summary>The request's token limit, when the engine reports one for the slot (Strata does not yet).</summary>
    public double? MaxTokens { get; set; }
    /// <summary>The tokens in the slot's working memory: what an idle slot holds for a next turn, what a running one holds (its prompt and what it has written).</summary>
    public double? Tokens => HeldTokens ?? (PromptTokens.HasValue ? PromptTokens + (Generated ?? 0) : null);
    public bool IsActive => State != null && State != "idle";
    public bool IsReading => State == "reading";
    public bool IsDecoding => State == "decoding";
}

public sealed class EngineLink
{
    public EngineLink(string title, string target) { Title = title; Target = target; }
    public string Title { get; }
    /// <summary>An http(s) URL or the full path of a local file.</summary>
    public string Target { get; }
}

public sealed class EngineSnapshot
{
    public EngineSnapshot(string engine, string model, EngineStatus status = EngineStatus.Idle)
    {
        Engine = engine; Model = model; Status = status;
    }

    public string Engine { get; set; }
    public string Model { get; set; }
    public EngineStatus Status { get; set; }
    public string? LoadingReason { get; set; }
    public int Queued { get; set; }
    public LiveRequest? Live { get; set; }
    /// <summary>Finished requests, newest first.</summary>
    public List<RequestStats> Recent { get; set; } = new List<RequestStats>();
    public ContextUsage Context { get; set; } = new ContextUsage();
    public SystemStats? System { get; set; }
    public CacheStats? Cache { get; set; }
    public Totals? Totals { get; set; }
    public EngineInfo? Info { get; set; }
    public List<BatchSlot>? Slots { get; set; }
    public List<EngineLink> Links { get; set; } = new List<EngineLink>();

    // Full-poll history for router configurations. Use the source address as identity because model names can match.
    internal string? HistorySource { get; set; }
    internal EngineCounters? FullPollCounters { get; set; }
    internal ThroughputWindow? ReportedThroughput { get; set; }

    /// <summary>
    /// The router refused the metrics request because its per-model request limit is reached (HTTP 429).
    /// The numbers are then the ones from the last successful poll.
    /// </summary>
    public bool RouterLimited { get; set; }

    public bool IsLoading => Status == EngineStatus.Loading;
    /// <summary>False while there is nothing to show numbers for: no model, or the engine is not responding.</summary>
    public bool HasModel => Status != EngineStatus.NoModel;
    public bool IsError => Status == EngineStatus.Error;
    public bool IsBusy => Status == EngineStatus.Running || Status == EngineStatus.Prefill || Status == EngineStatus.Thinking || Status == EngineStatus.Decode;
    public bool IsReading => Status == EngineStatus.Prefill;

    /// <summary>A hash of everything the panel, the readout and the icon show. Two polls with the same hash look the same.</summary>
    public int ContentHash()
    {
        var h = new HashCode();
        h.Add(Engine); h.Add(Model); h.Add(Status); h.Add(LoadingReason); h.Add(Queued); h.Add(RouterLimited);
        h.Add(ReportedThroughput);
        if (Live is { } l)
        {
            h.Add(l.PromptRead); h.Add(l.PromptTotal); h.Add(l.Generated); h.Add(l.MaxTokens); h.Add(l.PrefillTokensPerSecond);
            h.Add(l.DecodeTokensPerSecond); h.Add(l.ReusedEstimate); h.Add(l.ElapsedSeconds is double e ? Math.Floor(e) : (double?)null); h.Add(l.Running); h.Add(l.Waiting);
        }
        h.Add(Recent.Count);
        foreach (var r in Recent.Take(12))
        {
            h.Add(r.PromptTokens); h.Add(r.ReusedTokens); h.Add(r.PromptRead); h.Add(r.PromptMilliseconds); h.Add(r.OutputTokens);
            h.Add(r.DecodeTokensPerSecond); h.Add(r.DurationSeconds); h.Add(r.FinishedAt); h.Add(r.Finish);
        }
        h.Add(Context.Used); h.Add(Context.Max); h.Add(Context.CompactAt);
        if (System is { } s) { h.Add(s.RamUsedBytes); h.Add(s.RamTotalBytes); h.Add(s.GpuPercent); h.Add(s.CpuPercent); h.Add(s.GpuTemperatureCelsius); h.Add(s.GpuPowerWatts); h.Add(s.GpuMemoryUsedBytes); h.Add(s.GpuMemoryBusyPercent); }
        if (Cache is { } c)
        {
            h.Add(c.UsedBytes); h.Add(c.Slots); h.Add(c.Parked); h.Add(c.Requests); h.Add(c.RequestsReused); h.Add(c.Evictions);
            foreach (var e in c.Entries) { h.Add(e.Id); h.Add(e.Tokens); h.Add(e.ParkedAt); }
        }
        if (Totals is { } t) { h.Add(t.Requests); h.Add(t.OutputTokens); h.Add(t.PromptTokens); h.Add(t.ReusedTokens); h.Add(t.PrefillComputedTokens); }
        if (Info is { } i) { h.Add(i.Version); h.Add(i.BatchSlots); h.Add(i.ExpertHitRate); h.Add(i.KvFreePages); h.Add(i.KvCheckpointOnlyPages); h.Add(i.PrefixCheckpoints); h.Add(i.AdmissionBlocked); h.Add(i.GpuName); h.Add(i.TargetSeqs); h.Add(i.KvFormat); h.Add(i.DraftWindow); }
        if (Slots != null) foreach (var s2 in Slots) { h.Add(s2.State); h.Add(s2.HeldTokens); h.Add(s2.PromptRead); h.Add(s2.Generated); h.Add(s2.TokensPerSecond); h.Add(s2.ElapsedSeconds is double e2 ? Math.Floor(e2) : (double?)null); h.Add(s2.MaxTokens); }
        return h.ToHashCode();
    }

    /// <summary>A shallow copy, for marking the last data as stale without touching the original.</summary>
    public EngineSnapshot WithRouterLimited()
    {
        var copy = (EngineSnapshot)MemberwiseClone();
        copy.RouterLimited = true;
        copy.FullPollCounters = null;
        return copy;
    }
}
