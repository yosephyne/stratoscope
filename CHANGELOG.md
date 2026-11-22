# Changelog

## Unreleased

- MTPLX (MLX engine with multi-token prediction, also its M1 fork `mtplx-m1`) connector on the Mac: model, context window, MTP depth and profile from `/health`, finished requests from `/metrics` (prompt, cached tokens, read, speeds, finish reason). The server has no live progress, so a running request shows as "running" without a speed. Recognised behind llama-swap like the other engines; setting value `engine: mtplx`. Checked against a live server only, no recorded payload test yet.

- Windows reuses each full poll's metrics for routed history. The half-second history tick sends no requests through the router, including when the previously observed model is unloading or another model is starting.

- Panel (engines with several slots): one fixed row per engine slot, each showing the request's own speed (decode blue on the taskbar scale, prefill orange), empty rows stay as empty tracks, the live block has the same height idle and busy; the history legend says "Avg". Windows: a failed UI Automation read of the taskbar buttons no longer moves the readout (the last good read is kept). Mac: scale and legend mirrored, unbuilt.

- Taskbar and menu bar readout: one small bar per running request (up to eight) instead of one. A request that reads shows its prompt progress, one that writes shows its own speed against an adaptive scale (the maximum rises with a margin and steps back after 25 s below half); the writing dot became a blue bar. The total speed stays beside it.
- Panel: a two minute history graph, sampled every 0.5 s with prefill speed (orange), decode speed (blue) and total throughput, prompt plus generated tokens per second (black). Setting `history_smoothing` (`off`, `normal`, `high`, `highest`), also in the settings page. The macOS part is written but not yet built.

- llama.cpp (`llama-server`) connector, on Windows and the Mac: state, slots with the tokens they hold, prompt tokens read and reused per request, context, and with `--metrics` the speeds and totals. A sleeping server is shown as asleep and not woken. Setting value `engine: llamacpp`.
- Custom 27B engine: one busy state from `/metrics`, and reading and writing per request from `/ui/state` while requests run. Setting value `engine: custom`.
- Basalt engines: state, prefill progress and speeds, context, finished requests with their cache verdict, engine details, one entry per batch slot, RAM, CPU and the video card, each only where Basalt reports it. The Mac shows the same as Windows. Setting value `engine: basalt`.
- The kind of engine is asked of the engine itself, so one router can serve Strata, Basalt, llama.cpp and the custom engine.

## v0.3.1 · 2026-11-09

- Release files: the Mac download is `Stratoscope-<version>-macos.dmg`, the `.sha256` files are gone (GitHub shows each file's sha256), and the release text is shorter.
- "Open settings file" creates a settings file of commented examples, on Windows and on the Mac. Choosing a theme keeps the comments, and a setting the app does not know is named.
- The panel section "Prompt cache" is now "Conversation cache", on both systems.

## v0.3.0 · 2026-11-09

- Mac: a settings menu with Start at login, Theme, Open settings file, Reload settings and Check for updates.
- Mac: finds the server by itself, like Windows. It shows "No engine found" for a minute, then the item hides.
- Windows: the readout checks its colours against the taskbar as it really looks, and the grey ring is thicker.
- Windows: Settings and Check for updates are rows of the panel, with a settings page drawn like the rest of it. The system context menu is gone, it stayed light on a dark Windows.
- Windows: installer (`Stratoscope-<version>-windows-setup.exe`).

## v0.2.0 · 2026-11-09

- Windows 10 and 11 (x64): a readout in the taskbar and the same panel, released as `Stratoscope-<version>-windows.zip`. See [docs/WINDOWS.md](docs/WINDOWS.md).

## v0.1.4 · 2026-11-08

- Parked conversations whose details can't be read (no log) show "In use" instead of "Free".
- New setting `client_model`: the model id your client config uses, for setups where it differs from the name the engine reports.
- Client configs are also looked up in `$XDG_CONFIG_HOME`.
- README: a table of other setups and what to set for each.

## v0.1.3 · 2026-11-08

- The summarise marker on the context bar now finds its value itself: it reads the context and output size of the running model from the Kilo and OpenCode configs that point at the same server, and uses context minus output. Where nothing can be read, or two configs disagree, `compact_at` is the fallback, and without it there is no marker. `client_configs: none` turns the reading off.

## v0.1.2 · 2026-11-08

- Status words and labels start with a capital letter ("Ready", "Reading", "Hit rate", …).

## v0.1.1 · 2026-11-08

- Context: room left below the limit, and one bar per finished request showing the context size after it, for the last 12 requests.
- The marker for where your client summarises is now optional. Set `compact_at` to your client's threshold to see it; without a value no marker is drawn. The explanatory text under the bar is gone.
- Menu labels are written out ("Prefill", "Decode") instead of single letters.
- Screenshots come from fixed demo data and are reproducible with `./scripts/screenshot.sh`.

## v0.1.0 · 2026-11-08

First version.

- Menu bar item with live speed (`247 t/s`) and a progress bar while a prompt is read, colored by prompt-cache quality.
- Menu: current request, context bar with the room left, an optional marker where your client summarises, and the context size after each of the last 12 requests, speed of the last 12 requests, RAM/GPU/CPU, prompt cache and parked conversations, totals.
- Engine-neutral snapshot model and an adapter protocol. Adapters: **Strata**, **Demo**.
- Configuration through environment variables or `~/.config/stratoscope.json`.
- Download as a DMG from GitHub Releases. Pushing a `v*` tag builds a universal (Apple Silicon and Intel) DMG with a checksum and publishes it.
- README screenshots are rendered from demo data (`./scripts/screenshot.sh`).
