import Foundation

// SPEED RULE (name the source of every value; never estimate).
// When several requests run together, the headline decode speed (and the headline prefill speed while reading) is the engine's
// own total over all requests. Each engine reports that total itself:
//  - Strata: live.tok_s, the total of the generating requests (serve/server.py _tok_s). The prefill headline is
//    live.prefill_tok_s_mean, the engine's mean. The Windows Strata adapter also has a fallback: when the engine has no total yet,
//    it sums derived per-slot speeds; the macOS reader does not have that fallback.
//  - Basalt: live.tok_s, which the engine sums over its slots.
//  - llama.cpp: /metrics llamacpp:predicted_tokens_seconds, the server-wide decode rate over all slots (prompt_tokens_seconds for
//    prefill). It needs the server's --metrics flag. No sum is used.
//  - custom: /ui/state tick.sample.decode_busy_tps and tick.sample.prefill_busy_tps (newer servers: the engine's tokens over all
//    requests per second of the time it spent on that phase in the sample), else tick.sample.decode_tps and tick.sample.prefill_tps
//    (its counter deltas per second of wall time between samples). The per-request sum is used only when the sample has no rate.
// A request's own speed is shown on its own row only. Never add up the per-request rows to make the headline unless the engine
// has no total.
// This file: the Strata source is live.tok_s (headline decode) and live.prefill_tok_s_mean (headline prefill); there is no summing fallback here.

/// Adapter for the Strata local inference engine.
///
/// Data sources:
///  - the router's `/running` (optional, when a llama-swap style router sits in front of Strata)
///  - the Strata server's `/metrics`
///  - the Strata log, for the parked-conversation list that `/metrics` does not name
/// A Basalt engine answers at the same addresses; its document is recognised and translated by `BasaltReader`.
public final class StrataProvider: EngineProvider {
    public static let id = "strata"

    private let config: Config
    private let http = JSONFetcher()
    private let cacheLog = StrataCacheLog()
    private let basalt = BasaltReader()      // for the Basalt engines that answer at the same addresses
    private let llama = LlamaCppReader()     // for llama-server, whose /metrics (with --metrics) is Prometheus text and which names itself in /props
    private let custom = CustomReader()      // for the custom server, whose /metrics is Prometheus text
    private var proxy = "http://127.0.0.1:8095"
    private var lastRead: (Double, Date)?
    private let slotSpeeds = SlotRateTracker()
    private let clients: ClientConfigReader?

    /// `clients`: where to look for the summarise threshold of the AI clients that use this engine.
    /// Nil (the default) reads no client config.
    public init(config: Config, clients: ClientConfigReader? = nil) { self.config = config; self.clients = clients }

    // MARK: Polling

    public func poll(_ completion: @escaping (EngineSnapshot?) -> Void) {
        guard let router = config.router else { pollDirect(completion); return }
        http.get(router + "/running") { [weak self] r in
            guard let self = self else { return }
            guard let running = (r?["running"] as? [[String: Any]])?.first else { self.pollDirect(completion); return }
            if let p = running["proxy"] as? String { self.proxy = p }
            let name = (running["model"] as? String) ?? "Model"
            let state = running["state"] as? String ?? "?"
            if state != "ready" {
                completion(self.placeholder(model: name, status: .loading("State: \(state)"))); return
            }
            self.http.get(self.proxy + "/metrics") { m in
                guard let m = m else {
                    // No JSON: llama-server and the custom server send Prometheus text. Read through the router's /upstream/<model>/, like the Windows app.
                    let id = name.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? name
                    self.readText(base: router + "/upstream/" + id, routedModel: name) {
                        completion($0 ?? self.placeholder(model: name, status: .loading("Server not responding")))
                    }
                    return
                }
                if BasaltReader.recognises(m) { self.readBasalt(m, model: name, completion); return }
                completion(self.snapshot(from: m, model: name))
            }
        }
    }

    /// Without a router: ask the Strata server directly. If nothing answers, the menu bar item stays hidden.
    private func pollDirect(_ completion: @escaping (EngineSnapshot?) -> Void) {
        let base = config.url ?? "http://127.0.0.1:8095"
        http.get(base + "/metrics") { [weak self] m in
            guard let self = self else { return }
            guard let m = m, let eng = m["engine"] as? [String: Any] else {
                // Not a Strata or Basalt document: llama-server (checked by its /props) and the custom server answer /metrics in text. Hidden when it is none of these.
                self.readText(base: base, routedModel: nil, completion)
                return
            }
            self.proxy = base
            if BasaltReader.recognises(m) { self.readBasalt(m, model: eng["model"] as? String ?? "Basalt", completion); return }
            completion(self.snapshot(from: m, model: eng["model"] as? String ?? "Strata"))
        }
    }

    /// The server behind this address is a Basalt: its document is translated by `BasaltReader`.
    private func readBasalt(_ m: [String: Any], model: String, _ completion: @escaping (EngineSnapshot?) -> Void) {
        basalt.read(m, base: proxy, model: model, compactAt: compactAt(model: model), links: links(engine: "Basalt"), completion)
    }

    /// The JSON reads failed: ask whether llama-server sits at `base` (its `/props` names it), else whether the custom server does (nil when
    /// none does). `routedModel` is the router's name for the model; nil without a router.
    private func readText(base: String, routedModel: String?, _ completion: @escaping (EngineSnapshot?) -> Void) {
        let model = routedModel ?? CustomReader.brand
        llama.read(base: base, model: routedModel, compactAt: compactAt(model: routedModel ?? LlamaCppReader.brand),
                   links: links(engine: LlamaCppReader.brand, base: base)) { [weak self] outcome in
            guard let self = self else { return }
            switch outcome {
            case .read(let snapshot): completion(snapshot)
            case .notLlama: self.readCustom(base: base, model: model, completion)
            }
        }
    }

    /// The JSON reads failed: ask whether the custom server sits at `base` (nil when none does).
    private func readCustom(base: String, model: String, _ completion: @escaping (EngineSnapshot?) -> Void) {
        custom.read(base: base, model: model, compactAt: compactAt(model: model), links: links(engine: CustomReader.brand), completion)
    }

    private func placeholder(model: String, status: EngineStatus) -> EngineSnapshot {
        EngineSnapshot(engine: "Strata", model: model, status: status, context: ContextUsage(max: 0), links: links())
    }

    // MARK: Translation

    /// `base`: the engine's own address when it is not `proxy` (llama-server is read before `proxy` is set).
    private func links(engine: String = "Strata", base: String? = nil) -> [EngineLink] {
        var out: [EngineLink] = []
        if let u = URL(string: (base ?? proxy) + "/") { out.append(EngineLink(title: "Open \(engine) UI", url: u)) }
        if let r = config.router, let u = URL(string: r + "/ui/") { out.append(EngineLink(title: "Open router UI", url: u)) }
        if let w = config.webui, let u = URL(string: w) { out.append(EngineLink(title: "Open chat UI", url: u)) }
        if let l = currentLogPath() { out.append(EngineLink(title: "Open log", url: URL(fileURLWithPath: l))) }
        return out
    }

    /// The engine's log: configured path, otherwise the most recently written *.log of the Strata install.
    func currentLogPath() -> String? {
        if let l = config.log { return l }
        let dir = NSHomeDirectory() + "/.local/share/strata/install"
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: dir) else { return nil }
        var best: (String, Date)?
        for n in names where n.hasSuffix(".log") {
            let p = dir + "/" + n
            if let m = (try? fm.attributesOfItem(atPath: p))?[.modificationDate] as? Date, best == nil || m > best!.1 { best = (p, m) }
        }
        return best?.0
    }

    /// Strata's /metrics has no such value. It comes from the configs of the clients that talk to this
    /// server and use this model; where those say nothing (or disagree), the user's own `compact_at` is the fallback.
    private func compactAt(model: String) -> Double? {
        let endpoints = [config.router, config.url ?? "http://127.0.0.1:8095", proxy].compactMap { $0 }
        return clients?.compactAt(endpoints: endpoints, model: config.clientModel ?? model) ?? config.compactAt
    }

    func snapshot(from m: [String: Any], model: String) -> EngineSnapshot {
        let eng = m["engine"] as? [String: Any] ?? [:], live = m["live"] as? [String: Any] ?? [:]
        let hw = m["hardware"] as? [String: Any] ?? [:], cc = m["conversation_cache"] as? [String: Any] ?? [:]
        let tot = m["totals"] as? [String: Any] ?? [:], reqs = m["requests"] as? [[String: Any]] ?? []
        func num(_ o: [String: Any], _ k: String) -> Double? { (o[k] as? NSNumber)?.doubleValue }

        // Status
        let state = live["state"] as? String ?? "idle"
        let phase = (live["phase"] as? String) ?? ""
        let reading = state != "idle" && (phase.contains("prompt") || (num(live, "generated") ?? 0) == 0)
        let status: EngineStatus = state == "idle" ? .idle : reading ? .prefill : (phase == "thinking" ? .thinking : .decode)

        // Prefill speed: Strata's mean, otherwise derived from progress between two polls.
        var prefill = num(live, "prefill_tok_s_mean")
        if (prefill ?? 0) <= 0, reading, let read = num(live, "prompt_read"), let (r0, t0) = lastRead, read > r0 {
            prefill = (read - r0) / Date().timeIntervalSince(t0)
        }
        if reading, let read = num(live, "prompt_read") {
            if lastRead == nil || read != lastRead!.0 { lastRead = (read, Date()) }
        } else { lastRead = nil }

        // Cache reuse of the running request is not reported directly: when it starts, `prompt_read`
        // already sits at the reused part; later, read = mean prefill speed x elapsed time.
        var reused = 0.0
        if reading, let read = num(live, "prompt_read") {
            let mean = num(live, "prefill_tok_s_mean") ?? 0, el = num(live, "elapsed_s") ?? 0
            reused = (mean > 0 && el > 1) ? max(0, read - mean * el) : read
        }
        // Batch slots (live.slots): one row per slot. Several slots running together set the running count of the live request.
        let rawSlots = (live["slots"] as? [Any] ?? []).compactMap { $0 as? [String: Any] }
        let running = rawSlots.filter { $0["state"] as? String == "reading" || $0["state"] as? String == "decoding" }
        var liveRequest: LiveRequest?
        if state != "idle" {
            let t = num(live, "tok_s")
            liveRequest = LiveRequest(
                promptRead: num(live, "prompt_read") ?? 0, promptTotal: num(live, "prompt_total") ?? 0,
                generated: num(live, "generated") ?? 0, maxTokens: num(live, "max_tokens") ?? 0,
                elapsedSeconds: num(live, "elapsed_s") ?? 0, prefillTokensPerSecond: prefill,
                decodeTokensPerSecond: (t ?? 0) > 0 ? t : num(live, "tok_s_mean"), reusedEstimate: reused,
                running: running.count > 1 ? max(running.count, Int(num(live, "running") ?? 0)) : nil)
        }

        let recent = reqs.map {
            RequestStats(promptTokens: num($0, "prompt_tokens") ?? 0, reusedTokens: num($0, "reused") ?? 0,
                         promptRead: num($0, "prompt_read") ?? 0, promptMilliseconds: num($0, "prompt_ms") ?? 0,
                         outputTokens: num($0, "output_tokens") ?? 0, decodeTokensPerSecond: num($0, "decode_tok_s"),
                         durationSeconds: num($0, "duration_s") ?? 0,
                         finishedAt: Self.finishedAt(num($0, "time")), finish: $0["finish"] as? String, expertHitRate: num($0, "hit_rate"))
        }

        var ctxUsed = num(live, "prompt_tokens").map { $0 + (num(live, "generated") ?? 0) }
        if ctxUsed == nil, let l = recent.first { ctxUsed = l.promptTokens + l.outputTokens }

        let system = hw.isEmpty ? nil : SystemStats(ramUsedBytes: num(hw, "ram_used"), ramTotalBytes: num(hw, "ram_total"),
                                                    gpuPercent: num(hw, "gpu_util"), cpuPercent: num(hw, "cpu"),
                                                    gpuTemperatureCelsius: num(hw, "gpu_temp"), gpuPowerWatts: num(hw, "gpu_power"),
                                                    gpuPowerLimitWatts: num(hw, "gpu_power_limit"),
                                                    gpuMemoryUsedBytes: num(hw, "gpu_mem_used"), gpuMemoryTotalBytes: num(hw, "gpu_mem_total"))

        var cache: CacheStats?
        if !cc.isEmpty {
            cacheLog.update(path: currentLogPath())
            var entries = cacheLog.entries
            let parked = Int(num(cc, "parked") ?? Double(entries.count))
            while entries.count > parked && !entries.isEmpty { entries.removeFirst() }
            cache = CacheStats(budgetBytes: (num(cc, "budget_mib") ?? 0) * 1_048_576, usedBytes: num(cc, "bytes") ?? 0,
                               slots: Int(num(cc, "slots") ?? 0), parked: parked, requests: num(cc, "requests") ?? 0,
                               requestsReused: num(cc, "requests_reused") ?? 0, evictions: num(cc, "evictions") ?? 0,
                               entries: entries)
        }

        var totals: Totals?
        if !tot.isEmpty {
            totals = Totals(requests: num(tot, "requests") ?? 0, outputTokens: num(tot, "output_tokens") ?? 0,
                            promptTokens: num(tot, "prompt_tokens") ?? 0, reusedTokens: num(tot, "reused") ?? 0,
                            since: num(tot, "since").map { Date(timeIntervalSince1970: $0) })
        }

        var info: EngineInfo?
        if !eng.isEmpty {
            info = EngineInfo(version: eng["version"] as? String, kvFormat: eng["kv"] as? String, draftWindow: Self.text(eng["spec"]),
                              expertCacheMib: num(eng, "expert_cache_mib"), batchSlots: num(eng, "batch_slots"),
                              expertHitRate: reqs.first.flatMap { num($0, "hit_rate") })
        }

        // Each decoding slot's own speed from its generated count between polls (a slot's own tok_s counts from before its first token).
        // Strata's totals are added once when a request ends, so no counter of /metrics advances during a request: the curves take the slots.
        let decoding = rawSlots.filter { $0["state"] as? String == "decoding" }
        let slotRates = slotSpeeds.update(decoding.compactMap { s in num(s, "slot").map { (Int($0), num(s, "generated")) } }, now: Date())
        var slots: [BatchSlot]?
        if !rawSlots.isEmpty {
            slots = rawSlots.map {
                BatchSlot(index: Int(num($0, "slot") ?? 0), state: $0["state"] as? String, heldTokens: num($0, "held_tokens"),
                          promptTokens: num($0, "prompt_tokens"), promptRead: num($0, "prompt_read"), generated: num($0, "generated"),
                          tokensPerSecond: num($0, "slot").flatMap { slotRates[Int($0)] },
                          maxTokens: num($0, "max_tokens"), elapsedSeconds: num($0, "elapsed_s"))
            }
        }

        return EngineSnapshot(engine: "Strata", model: model, status: status, queued: Int(num(live, "queued") ?? 0),
                              live: liveRequest, recent: recent,
                              context: ContextUsage(used: ctxUsed, max: num(eng, "max_context") ?? 0,   // 0 = the engine did not say
                                                    compactAt: compactAt(model: model)),
                              system: system, cache: cache, totals: totals, links: links(),
                              engineVersion: eng["version"] as? String, info: info, slots: slots)
    }

    /// The time a request finished, from the engine's seconds since 1970 (UTC); nil for a missing, non-finite or absurd value.
    static func finishedAt(_ epochSeconds: Double?) -> Date? {
        guard let t = epochSeconds, t.isFinite, t > 0, t < 253_402_300_000 else { return nil }
        return Date(timeIntervalSince1970: t)
    }

    /// The draft window: Strata sends a number, Basalt may send text; kept as text. Nil for anything else.
    static func text(_ value: Any?) -> String? {
        if let s = value as? String { return s }
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return nil }
        let d = n.doubleValue
        return d == d.rounded() && abs(d) < 1e15 ? String(Int64(d)) : String(d)
    }
}
