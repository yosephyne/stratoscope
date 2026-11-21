# Architecture

Stratoscope is built so that **the menu knows nothing about any engine**. Engines are adapters.

```
 engine server ──HTTP/log──▶ EngineProvider ──▶ EngineSnapshot ──▶ Panels (AppKit) ──▶ menu bar
 (Strata, …)                 one per engine     engine-neutral      draws what it gets
```

| Folder | Contents | Imports AppKit? |
|---|---|---|
| `Sources/StratoscopeCore/Snapshot.swift` | The engine-neutral data model | no |
| `Sources/StratoscopeCore/EngineProvider.swift` | The adapter protocol and the registry | no |
| `Sources/StratoscopeCore/Engines/<Name>/` | One folder per engine | no |
| `Sources/StratoscopeCore/Config.swift`, `Format.swift`, `CacheHit.swift` | Settings, number formatting, cache verdict | no |
| `Sources/Stratoscope/` | The macOS app: status item, menu, drawing, screenshot renderer | yes |

Everything that can be tested without a screen lives in `StratoscopeCore` and is covered by `Tests/`.

## Platforms

`StratoscopeCore` knows nothing about any UI. It only imports Foundation, so everything an engine adapter does (fetching, parsing, estimating, formatting) is shared by all platforms. The UI is the only platform-specific part: today `Sources/Stratoscope` is the macOS app, built with AppKit. Windows is a second implementation in C# (`Sources/Windows`, `Core` and `App`), kept in step by [Shared behaviour](#shared-behaviour) and the recorded payloads in `Tests/Fixtures`: see [WINDOWS.md](WINDOWS.md).

To add a platform:

1. Add a new target next to `Sources/Stratoscope` (for example `Sources/StratoscopeWindows`) that depends on `StratoscopeCore`.
2. Poll an `EngineProvider` every two seconds and draw the `EngineSnapshot` it returns: a menu bar or taskbar item with the speed, and a panel with the sections. Hide the item when the provider returns `nil`.
3. Keep platform code out of `StratoscopeCore`. If the core needs something platform-specific, put it behind a small protocol.
4. On platforms outside Apple's, `URLSession` lives in `FoundationNetworking`, and paths like `~/.config/stratoscope.json` may need a per-platform location in `Config.swift`.

Open an issue first so the design can be agreed before the code.

## The snapshot

`EngineSnapshot` holds what the menu can show: status (loading, idle, prefill, thinking, decode), the running request, finished requests, context usage, and three **optional** sections: `system` (RAM, GPU, CPU), `cache` (conversation cache and parked conversations) and `totals`. A section that is `nil` is not drawn. An engine that has no cache stats simply gets a shorter menu.

## Where the context numbers come from

| Shown | Source | If missing |
|---|---|---|
| `X of Y` and the bar | `X`: prompt plus generated tokens of the running request, or of the last request when idle. `Y`: the engine's context limit (`ContextUsage.max`) | no limit: just `X`, no bar fill |
| Marker | `ContextUsage.compactAt`, see below | no marker |
| `N free` | limit minus `X`, only when both are known (`ContextUsage.free`) | not shown |
| Bars under the context bar (one per finished request, last 12) | prompt plus output tokens of each finished request (`RequestStats.contextTokens`) | not shown |

The marker on the bar is `ContextUsage.compactAt`: the token count where the *client* summarises or trims the conversation. It is `nil` unless a source knows it, and then nothing is drawn. The adapter decides the source. An engine that reports it passes it through. Strata's `/metrics` has no such field, so the Strata adapter takes, in this order:

1. The client configs read by `ClientConfigReader` (`ClientLimits.swift`): Kilo's `kilo.jsonc` and OpenCode's `opencode.json`. A config counts when its `baseURL` has the same host and port as the router, `url` or proxy, and it names the running model. The value is the model's `limit.context` minus `limit.output`, which is the rule observed for Kilo. `client_model` names the model id used in the client config when it differs from the engine's name. Two matching configs that disagree give `nil`.
2. The user's `compact_at` setting, as the fallback for everything that cannot be read (other clients, other models).
3. Nothing: no marker.

The reader only looks at the server address and the `limit` numbers of each model, and never uses, stores or logs a key. Another client is one more entry in `ClientConfigReader.sources` and, if it summarises by another rule, its own `compactAt`. Test it with a sample config, like `ClientLimitsTests`.

The bars below the context bar need no setting: a sudden drop is where a client started over, and the cache dots show what that cost.

## Screenshots

The images in `docs/img` are rendered from `DemoProvider`'s fixed data by `./scripts/screenshot.sh`, not captured by hand: the menu is painted into an opaque 2x PNG, and the demo's clock is pinned, so unchanged code gives byte-identical files. After any change to how the menu looks, run the script and commit the images with the change. To add a screenshot, add a `shoot` line to the script and reference the file in the README.

## Adding an engine

1. Create `Sources/StratoscopeCore/Engines/MyEngine/MyEngineProvider.swift` and conform to `EngineProvider`:

   ```swift
   public final class MyEngineProvider: EngineProvider {
       public static let id = "myengine"
       public init(config: Config) { … }
       public func poll(_ completion: @escaping (EngineSnapshot?) -> Void) {
           // fetch the engine's metrics, translate, then call completion once on the main queue.
           // Pass nil if the engine is not running: the menu bar item hides itself.
       }
   }
   ```

2. Register it in `EngineRegistry` (`EngineProvider.swift`): add the id to `available` and a case to `make`.
3. Add a test that feeds a recorded metrics payload through your translation, like `StrataSnapshotTests`.
4. Document the engine's defaults in the README.

On Windows an engine behind a llama-swap router derives from `RoutedProvider` (`Sources/Windows/Core/Engines/`), which owns the router flow; the engine only reads and translates its `/metrics` (`StrataProvider`, `BasaltProvider`). `AutoEngineProvider`, the default, asks the engine's `/health` for its `service` and hands the read to the right one; an engine whose health names nothing (llama-server answers `{"status":"ok"}`) is asked for `/props`, and only a llama-server answers it with its slot count and generation settings. Anything else is read as Strata. `LlamaCppProvider` (llama-server) reads `/props` (build, slot count, sleeping), `/slots` (per slot: running or not, tokens in memory, prompt tokens read and reused, tokens generated; a running slot with 0 generated tokens is reading) and `/metrics` (Prometheus text, only with `--metrics`: speeds, waiting requests, totals); it does not ask `/slots` of a sleeping server, which would wake it. llama-server sends no prompt size and no finished-request list, so those parts stay empty. Basalt's `/slots` is asked only while a request runs: `/metrics` has the prompt size, `/slots` the read progress. Basalt's per-request `hit_rate` is the expert cache's; the cache verdict (dots, bar colour) comes from `prompt_tokens` and `reused`.

Guidelines:

- **Report only what the engine knows.** Prefer `nil` over a guess. Where you must estimate (Strata does not report cache reuse of a running request), say so in a comment.
- **Never block.** `poll` runs every two seconds on the main queue. Use `JSONFetcher` (1.5 s timeout) and call `completion` when done.
- **Keep engine code out of the UI.** If the menu needs something new, add it to `EngineSnapshot` as an optional field and make it useful for every engine.
- Use `DemoProvider` as the smallest example.

## Shared behaviour

What every front end does the same way. The payloads in `Tests/Fixtures` with their `*.expected.json` files (see the README there) pin it down; the C# tests read them, the Swift tests do not yet.

- **Missing values:** a key that is missing or `null` is unknown (`–` or left out, never `0`); a number sent as text is not a number. A missing section (`hardware`, `conversation_cache`, `totals`) is not drawn. Shown on purpose: the title is padded to three digits and reads `0 t/s`, and the running request's cache reuse carries `≈`.
- **Numbers:** English formatting whatever the system language (`1,234,567`, `26.4`), halves round to even, durations `42 s`, `2 min 5 s`, `3 h 12 min`, sizes in GiB with one decimal.
- **Strata sources:** router mode asks `GET <router>/running`, then `GET <router>/upstream/<model>/metrics`; direct mode `GET <url>/metrics`. Nothing else. Status comes from `live.state` and `live.phase`; speeds from `live.prefill_tok_s_mean` (else the progress of `live.prompt_read` between polls) and `live.tok_s`; context from `live.prompt_tokens + live.generated` of `engine.max_context`; requests from `requests[]`; RAM, GPU, CPU from `hardware`; the conversation cache from `conversation_cache`; totals from `totals`.
- **States:** connecting (nothing shown), offline, no model (router answers, `running` empty; nothing is woken), loading (router state is not `ready`), then ready, reading, thinking, writing. An HTTP 429 from the router keeps the last data on screen, marked.
- **Settings:** environment variable, then the settings file, then the default. A router without `url` is asked alone, never `127.0.0.1:8095`.
- **Speed when several requests run together:** the headline decode speed (and the headline prefill speed while reading) is the engine's own total over all requests, for every engine. Strata: `live.tok_s` (the engine's total of the generating requests); when the engine has no total yet, the sum of the derived per-slot speeds (Windows only). Basalt: `live.tok_s`, which the engine sums over its slots. llama.cpp: `/metrics` `llamacpp:predicted_tokens_seconds` (prefill: `prompt_tokens_seconds`), the server-wide rate over all slots; it needs the server's `--metrics` flag. Custom 27B: `/ui/state` `tick.sample.decode_busy_tps` and `prefill_busy_tps` (newer servers: the engine's tokens over all requests per second of the time it spent on that phase in the sample, when the phase ran in it), else `decode_tps` and `prefill_tps` (its counter deltas per second of wall time between samples); the sum of the per-request rates only when the sample has no rate. A request's own speed is shown on its own row. The per-request rows are never added up for the headline unless the engine has no total.
- **Request bars and speed scale:** the readout shows one bar per running request (at most eight; source: the engine's batch slots, else the live request), each as wide as the old progress bar; with several the stack grows taller, centred where the single bar is, as the square root of the count (`H = 6 epx * sqrt(n)`, capped at 32 epx) while each bar gets thinner: the gap is a quarter of the bar (at least 1 device pixel), the bar is what is left (at least 2), leftover pixels make the first bars 1 px taller. `BarRows` in `ReadoutPainter.cs`, `RequestBars.rows` in `SpeedHistory.swift`. Reading: the prompt progress, in orange (the cache colour while it is the only request). Writing: the request's own slot speed against the adaptive scale `SpeedScale` (0 to a maximum), in blue; a speed the engine does not report leaves the track empty. The maximum rises when a value exceeds it, to that value plus 5 %, rounded up to two significant digits (126 becomes 130, 283 becomes 290). It steps back to just above the peak (two significant digits) only after every value stayed below half of it for 25 s (`SpeedScale.FallHoldSeconds`), never below its start (100 t/s). It never changes per sample. The same class scales the history graph axis and the bar charts of the Speed section. `SpeedScale.cs` and `SpeedScale.swift`, tested on both sides.
- **Request rows in the panel:** the per-request rows under the headline share an area of fixed height, so the panel does not change size with the number of requests (an engine with several batch slots keeps that height for one request too). Pitch = area / count, at most the height of one full row; full row (up to two requests), compact row (three and four), bar only (five to eight, numbers in the tooltip). A token limit that every generating request shares is shown once above the rows, otherwise per row. `RunningRows.cs` and `RunningRows.swift`, tested on the Windows side.
- **History graph (panel only):** the last 120 s of speed (`SpeedHistory`), sampled up to every 0.5 s (`SpeedHistory.SampleSeconds`) for direct custom engines. Windows router configurations use full-poll samples only (see [Routed polling](WINDOWS.md#routed-polling)). Orange is the prefill speed (prompt tokens computed per second), blue the decode speed (generated tokens per second), black (the label colour) the total, the throughput of vLLM and OpenTelemetry GenAI: tokens processed per second, prompt plus generated; it is the sum of the two, so prefill is counted. Sources, in this order, the same for every engine: (1) the engine's cumulative counters, as a rate between the reads at which the counter was seen to rise, over about two of the engine's own publishing intervals and held until the next step (`CounterRate`: the custom engine publishes its generation counter in steps of about 100 tokens once a second, so the delta of two neighbouring 0.5 s reads would alternate between 0 and twice the speed; a counter that rises at every read gets the plain short window) (`Totals.PrefillComputedTokens`, `Totals.OutputTokens`; an adapter fills the first only when the counter advances while a request reads, per chunk, never one added once per request). The custom 27B engine: `q27:prefill_tokens_total` (equal to the prompt rows of all steps, `step_rows_total{kind="prompt"}` plus `prompt_second`) and `q27:generation_tokens_total`, read from `/metrics` during full polls behind a router. On Windows direct-only connections, `IEngineProvider.ReadCountersAsync` also reads `/metrics` between full polls; `q27:prompt_tokens_total` is added once per request and is not used. Newer custom servers also keep phase clocks (`q27:prefill_busy_seconds_total`, `q27:decode_busy_seconds_total`, `q27:decode_step_tokens_total`, time.perf_counter seconds in the engine's worker): then Prefill is `q27:prefill_tokens_total` over `q27:prefill_busy_seconds_total` and Decode `q27:decode_step_tokens_total` over `q27:decode_busy_seconds_total`, each between the reads at which the seconds rose (`PhaseRate`; held like `CounterRate`, then 0 while the phase does not run), so the speed is per second of work on that phase and a prompt read in a tenth of a second is not diluted by the read interval; Total stays prompt plus generated tokens per second of wall time and is then not the sum of the two. The legend names the curves "All prefill", "All decode" and "Total": they are the engine over all requests, while the Speed section ("Speed per request") shows each finished request's own speeds. (2) Without counters, the batch slots: the decoding slots' own speeds and the prompt progress of the reading slots between polls. (3) The live request's speeds. A counter that falls (the engine restarted) is a gap; missing counters or a gap over 30 s add no counter rate; idle is 0. Each curve is smoothed with the one-pole filter, its damping defined per second so that it feels the same at any sample rate: `d_sample = d ^ dt`; setting `history_smoothing`: `off` (d 0), `normal` (0.5, default), `high` (0.9), `highest` (0.95); environment `STRATOSCOPE_HISTORY_SMOOTHING`. Per engine: **Strata** has no counter that advances during a request (`totals` are added when a request ends, `serve/server.py`), so its curves use `live.slots[]` (`prompt_read` per chunk, `generated` per token), each decoding slot's speed derived between polls (`SlotRateTracker`); **Basalt** sends no cumulative counter either and uses `/slots` (the slot's own `tok_s`, `prefill_done` between polls); **llama.cpp** adds `tokens_predicted_total` when a slot is reset at the end of a request and `prompt_tokens_total` only when a batch with an output token is flushed (`tools/server/server-context.cpp`), so neither is mapped, and it uses `/slots` (`n_decoded` and `n_prompt_tokens_processed` between polls, `SlotRateTracker`); the custom engine is the only one with per-chunk counters. vLLM's `vllm:prompt_tokens_total` is not mapped and there is no vLLM adapter. Every adapter with batch slots fills `BatchSlot` (state, prompt read and size, generated, own `TokensPerSecond` for each decoding slot) for the per-request bars and the running rows. llama.cpp lists slots when the server has more than one; with one slot its live speeds come from the same counts when the `/metrics` gauge says nothing. The legend is one row (colour mark, name, average over the 120 s window of the raw samples, not the smoothed curve, counting only the samples above 0: the time in which nothing ran does not pull it down, and a curve that did not run shows `–`); there is no axis label (the gridlines are 0, half and the adaptive maximum).

On Windows the decode and total legend values use generated and computed prompt counter deltas over wall time, including idle time. The window ends at the latest reading and uses the closest observed sample at or before the two-minute cutoff. During startup it uses the available observed interval. Autobahn supplies existing intervals in `/ui/state`, which populate the legend on the first refresh. The Windows total curve uses the counter delta over each measured poll interval. For the Windows legend and routed data sources see [Windows history](WINDOWS.md).

## Not built yet

- More than one engine at a time (the registry picks one via `STRATOSCOPE_ENGINE`).
- Localisation. Strings are English literals in `Panels.swift` and `AppDelegate.swift`.
