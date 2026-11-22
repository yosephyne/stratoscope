import Foundation

// SPEED RULE (name the source of every value; never estimate).
//  - MTPLX: the server runs one request at a time (scheduler mode "serial"). Its /metrics has no live document: `latest` and `recent` are
//    per finished request, and /health says only how many requests run (`active_requests`) and when the last one started.
//    So while a request runs the readout says "running" (the engine does not say whether it reads or writes) and shows no speed;
//    the speeds are the finished requests': prefill_tok_s, decode_tok_s (the engine's own values, never derived here).

/// Reads MTPLX (the MLX engine with native multi-token prediction; also its fork for M1 Macs, `mtplx-m1`).
///
/// MTPLX answers `/metrics` with JSON that has `latest`, `recent` (finished requests, oldest first) and `tool_parse_counters`, and `/health`
/// with the model, the context window, the MTP depth, the profile and `active_requests`. Behind llama-swap, `StrataProvider` finds the
/// server through the router and hands that document to this reader (`recognises`).
public final class MtplxReader {
    public static let id = "mtplx"
    public static let brand = "MTPLX"
    private let http = JSONFetcher()

    public init() {}

    /// MTPLX's /metrics: `latest` and `recent` plus `tool_parse_counters`, and nothing of Strata's (`engine`, `live`).
    public static func recognises(_ metrics: [String: Any]) -> Bool {
        metrics["latest"] is [String: Any] && metrics["tool_parse_counters"] is [String: Any] && metrics["engine"] == nil
    }

    /// Asks `<base>/health` and translates it together with `metrics`. A failed /health leaves the live state and the context window unknown.
    public func read(_ metrics: [String: Any], base: String, model: String, compactAt: Double?, links: [EngineLink],
                     _ completion: @escaping (EngineSnapshot?) -> Void) {
        http.get(base + "/health") { [self] health in
            completion(snapshot(metrics: metrics, health: health ?? [:], model: model, compactAt: compactAt, links: links, now: Date()))
        }
    }

    /// Translates one /metrics and one /health document. Internal so tests can feed recorded payloads.
    func snapshot(metrics m: [String: Any], health h: [String: Any], model: String, compactAt: Double?, links: [EngineLink], now: Date) -> EngineSnapshot {
        func num(_ o: [String: Any], _ k: String) -> Double? { (o[k] as? NSNumber).flatMap { CFGetTypeID($0) == CFBooleanGetTypeID() ? nil : $0.doubleValue } }
        let latest = m["latest"] as? [String: Any] ?? [:]
        let all = (m["recent"] as? [[String: Any]]) ?? []
        // `recent` is oldest first; Stratoscope wants newest first. `latest` is the newest and is part of `recent` once it ends.
        var rows = Array(all.reversed())
        if rows.isEmpty, !latest.isEmpty { rows = [latest] }

        let recent = rows.map { r -> RequestStats in
            RequestStats(promptTokens: num(r, "prompt_tokens") ?? 0,
                         reusedTokens: num(r, "cached_tokens") ?? 0,
                         promptRead: num(r, "new_prefill_tokens") ?? 0,
                         promptMilliseconds: (num(r, "prompt_eval_time_s") ?? 0) * 1000,
                         outputTokens: num(r, "completion_tokens") ?? 0,
                         decodeTokensPerSecond: num(r, "decode_tok_s"),
                         durationSeconds: num(r, "request_elapsed_s") ?? 0,
                         finishedAt: StrataProvider.finishedAt(num(r, "completed_at_s")),
                         finish: r["finish_reason"] as? String)
        }

        let active = Int(num(h, "active_requests") ?? 0)
        var status: EngineStatus = active > 0 ? .running : .idle
        var live: LiveRequest?
        if active > 0 {
            let started = num(h, "last_request_started_at")
            live = LiveRequest(elapsedSeconds: started.map { max(0, now.timeIntervalSince1970 - $0) }, running: active > 1 ? active : nil)
        }
        if h.isEmpty && active == 0 { status = .idle }

        let window = num(h, "context_window") ?? num(h["execution_window"] as? [String: Any] ?? [:], "tokens") ?? 0
        var used: Double?
        if let l = recent.first { used = l.promptTokens + l.outputTokens }

        var system: SystemStats?
        if let total = num(h, "unified_memory_bytes") { system = SystemStats(ramTotalBytes: total) }

        // The prompt cache: how many finished requests found part of their prompt already read.
        let reused = Double(recent.filter { $0.reusedTokens > 0 }.count)
        let cache = recent.isEmpty ? nil : CacheStats(requests: Double(recent.count), requestsReused: reused)

        var totals: Totals?
        if let n = num(h, "requests_completed") {
            totals = Totals(requests: n, outputTokens: recent.reduce(0) { $0 + $1.outputTokens },
                            promptTokens: recent.reduce(0) { $0 + $1.promptTokens }, reusedTokens: recent.reduce(0) { $0 + $1.reusedTokens })
        }

        let profile = (h["profile"] as? [String: Any])?["name"] as? String
        let depth = num(h, "depth").map { "MTP depth \(Int($0))" }
        let info = EngineInfo(kvFormat: h["paged_kv_quantization"] as? String,
                              draftWindow: [profile, depth].compactMap { $0 }.joined(separator: ", ").nilIfEmpty,
                              gpuName: h["chip"] as? String)

        let name = (h["model"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? model
        return EngineSnapshot(engine: Self.brand, model: model.isEmpty ? name : model, status: status, queued: 0, live: live, recent: recent,
                              context: ContextUsage(used: used, max: window, compactAt: compactAt),
                              system: system, cache: cache, totals: totals, links: links, engineVersion: nil, info: info)
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
