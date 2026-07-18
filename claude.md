# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository, and — since it's
meant to carry forward into a follow-up project — with Bolero/Blazor WASM F# projects in general.

**How to read this file**: the first sections are specific to what this project actually is (a CLI +
a Bolero/Blazor WebAssembly frontend sharing pure F# domain code) and are hard-won from building it.
The last section, "General F# conventions", is a trimmed-down carryover of a wider team template that
mostly does **not** apply here (no API layer, no database, no test suite) — keep it as reference for
if a follow-up project grows one of those, don't apply it speculatively to this one.

## Project shape

- `Touchstone.fs` / `TouchstonePlot.fs` at the repo root: pure F#, no UI dependencies. Parses Touchstone
  RF files and builds Plotly.NET charts. Shared between the CLI and the web frontend via
  `<Compile Include="..\Touchstone.fs" />`-style linking in the `.fsproj`, not a separate shared-library
  project — small, dependency-light files don't need that ceremony.
- CLI (`TouchstoneReader.fsproj`, net10.0): thin wrapper, opens charts in the system browser via
  `Chart.show`.
- Web (`Web/TouchstoneReader.Web.fsproj`, net8.0 — capped by Bolero's latest dependency group, not a
  free choice): Bolero (Elmish-on-Blazor-WebAssembly), split into `State.fs` (Model/Message/update),
  `View.fs` (Bolero.Html view tree), `Main.fs` (the `ProgramComponent` + JS interop glue).
- `Web/wwwroot/interop.js`: the non-Elmish escape hatch — anything that has to touch Plotly.js directly
  (rendering, theming, CSV/image download) lives here, called via `IJSRuntime.InvokeVoidAsync` /
  `[<JSInvokable>]`.

## Why Bolero, not Fable

No Node.js/npm in this environment, so the classic Fable+Elmish+webpack pipeline was never an option.
Bolero gets the same MVU architecture (a real `Elmish` NuGet package) compiled straight from F# to WASM
via plain `dotnet build` — no JS toolchain needed. If a follow-up project needs richer JS-ecosystem
access, that tradeoff should be revisited consciously, not assumed away again by default.

## Bolero/Blazor gotchas learned the hard way

These cost real debugging time in this project — check them first before assuming a bug is something
else entirely.

- **Stale input values after a snap/transform.** If a message handler transforms a typed value and the
  result equals what's already rendered (e.g. typing `0`, snapping to the nearest valid data point,
  which happens to already be the current value), Blazor's diff sees "old rendered value == new
  rendered value" and skips updating the DOM — even though the browser's live input still shows
  whatever the user actually typed. Fix: key the input on something that changes on every commit (a
  per-item generation counter), forcing element replacement instead of a patch. See
  `LoadedFile.FreqRangeGen` / `attr.key` usage in `Web/View.fs`.
- **DOM diffing can wipe out externally-injected content.** Plotly.js draws directly into a `<div>`
  Blazor doesn't know about. If a *sibling* element is conditionally inserted/removed (e.g. a status
  banner appearing above the chart), Blazor's diff can shift/repatch the chart's div and Plotly's
  content vanishes. Fix: always render the element, toggle visibility via `style="display:none"`
  instead of conditional rendering.
- **WASM is single-threaded.** Without a `do! Task.Delay 1` yield, the browser never gets a chance to
  paint a status message before synchronous CPU-heavy work (downsampling, chart building) blocks the
  thread.
- **`on.change` vs `on.input`**: `on.input` fires on every keystroke/drag-tick; `on.change` fires once
  on commit (blur/release). Anything that triggers real downstream work (re-render, re-fetch) should
  use `on.change` — using `on.input` for the frequency-range slider would have re-rendered on every
  pixel of drag.
- **A private function/value can shadow a Bolero element name** (`summary`, `label`, etc.) and produce
  a confusing, unrelated-looking compile error ("sequence expressions must be `seq {}`"). If a Bolero
  DSL block won't compile for no obvious reason, check for a naming collision first.
- **`Chart.Grid` collapses per-subplot `Chart.withTitle`** into a single shared title (only the last one
  wins) — use `Chart.withYAxisStyle` per subplot for a per-cell label instead.
- **Browsers cache `interop.js` (and to a lesser extent `index.html`) aggressively**, even across
  `dotnet run` restarts. A manual `?v=N` query param on the `<script>` tag, bumped whenever
  `interop.js` changes, is the reliable fix — a plain reload often isn't enough.
- **Reserved-word attributes need double-backtick escaping** in Bolero's `attr` module — e.g. the
  `type`, `class`, and `open` attributes, since those are F# keywords.

## Plotly.NET / Plotly.js notes

- **A subplot grid's *shape* and its *size* are independent settings** — `Chart.Grid(rows, cols)`
  controls how many subplots exist and must match the actual number of traces/subplots given to it;
  `Chart.withSize(width, height)` controls how much total space the figure gets, and doesn't have to
  scale with `rows`/`cols`. Fixing the size at the *maximum* possible grid footprint while letting
  `Chart.Grid`'s row/column counts still track the current (possibly smaller) selection means whatever
  *is* selected stretches to fill that fixed space, instead of the whole figure shrinking every time
  something's deselected. See `quadMulti` in `TouchstonePlot.fs` — always sized for the full 2×2 quad
  regardless of how many of the (up to 4) parameters are actually selected.
- **Collapsing several traces to one legend entry**: give every trace that
  belongs together the same `legendgroup`, set `showlegend = false` on all
  but one representative trace per group (renamed to just the shared label,
  e.g. the filename instead of "filename S11"), and set
  `layout.legend.groupclick = 'togglegroup'`. Clicking that one entry then
  hides/shows every trace in the group at once, across subplots too — not
  just the single trace the entry happens to be attached to. Done entirely
  as a post-processing step over the figure JSON in `interop.js`
  (`_dedupeLegendByFile`), not in the F# chart-building code, since it only
  needs the trace names Plotly.NET already produces.
- **Client-side-only trace state doesn't survive `Plotly.react` with new
  data.** Toggling a trace's `visible` via `Plotly.restyle` (e.g. a custom
  "hide this file" button outside Plotly's own legend) only changes the
  live chart object — the next `Plotly.react` call with freshly-serialized
  JSON from F# has no idea that visibility choice was ever made, and resets
  it. Re-apply any such client-only state every time new figure JSON comes
  in, not just when the user makes the choice. See `_hiddenFiles` /
  `_applyHiddenFiles`, applied inside `renderChart` itself in `interop.js`.

## Performance

- Blazor WASM runs **interpreted by default** (no AOT) — .NET IL is executed instruction-by-instruction
  by a WASM-compiled interpreter, not compiled to native WASM ahead of time. Measured directly in this
  project: the same parsing/downsampling code took ~16s in the browser vs. near-instant via
  `dotnet fsi` natively, for two real 11,000-point VNA files.
- AOT compilation (`<RunAOTCompilation>true</RunAOTCompilation>`) would close much of that gap for
  CPU-bound code, at the cost of a much longer publish build and a larger shipped bundle. Requires
  `dotnet workload install wasm-tools` (not installed as of this writing) and gets applied only to
  Release/publish builds — get the user's explicit go-ahead before installing new tooling like this.
- **Prefer lazy over eager for anything expensive that isn't always needed.** `ChartResult.Csv` is a
  `unit -> string` thunk, not a pre-built string, specifically because building it (a full string per
  data point) on every render — even though the download button is clicked rarely — was measurably
  wasted work. Look for the same shape before assuming "just compute it upfront" is fine in a WASM
  context.

## F# style

- **Discriminated unions, never enums.** Consistent across this codebase (`Parameter`, `ChartKind`,
  `DisplayMode`, `Format`, ...) — keep it that way.
- **`sprintf`, not string interpolation (`$"..."`).** Consistent throughout; avoids the whole class of
  interpolated-string compiler warnings by not using the feature, rather than working around it case
  by case.
- **Function size**: aim for 10–25 lines. A sequential parser with real accumulated mutable state (see
  `Touchstone.parse`, ~107 lines) is a legitimate exception; a Bolero view-tree composition function or
  an Elmish lifecycle handler mixing several unrelated concerns is not — split those into named pieces.
  `Web/Main.fs`'s `OnAfterRenderAsync` (~140 lines) and `Web/View.fs`'s `renderView` (~175 lines) are
  known current violations in this codebase, flagged for cleanup, not a pattern to copy into a new
  project.
- **`private` by default** inside a module; only the handful of functions a caller (CLI or Web)
  actually needs are public. See `TouchstonePlot.fs` — most of it is `private` chart-building plumbing
  behind a handful of public `*Chart` / `*ChartMulti` entry points.
- Types and functions both live in the same `module X.Y` per file — no separate `namespace` + nested
  `module` split. Deliberate for a project this size; don't introduce that split without a concrete
  reason to.

## Testing / verification

No automated test suite exists yet (no Expecto, no test project). Verification for the web frontend has
been entirely through the Preview MCP tools: drag-and-drop synthetic and real files via `preview_eval`,
then check via `preview_screenshot` / `preview_inspect` / `preview_console_logs`. Notes for next time:

- `preview_eval` has a hard ~30s timeout — for WASM work that can legitimately take longer (large real
  files), poll with short repeated calls instead of one long blocking `await`/`setTimeout`.
- The browser tab (and its cache) persists across `preview_stop`/`preview_start` cycles — after any
  change to `interop.js` or `index.html`, navigate with a cache-busting query
  (`location.href = '/?cachebust=' + Date.now()`) before trusting what's rendered.
- Check for a stray `dotnet ... blazor-devserver.dll` process already holding the port before assuming
  `preview_start`'s "port in use" error means something else is wrong.
- **Simulating a click on a Plotly legend entry**: a synthetic click on the visible `.legend .traces`
  group doesn't trigger Plotly's own handler. Target the (invisible) `.legend .legendtoggle` hit-area
  element instead — that's what Plotly actually binds the click listener to.
- **Checking axis labels means reading `chartDiv.layout.xaxis.title`/`.yaxis.title` (and `xaxis2`,
  `yaxis2`, ... per subplot after `Chart.Grid`) live in the browser, not just reading the F# chart-
  building code.** A source-level read of `quadMulti` in `TouchstonePlot.fs` looked complete — each
  subplot had a `Chart.withYAxisStyle` call — but only the *rendered* layout showed the X axis had no
  title at all, since nothing ever called the X-axis equivalent for it. `preview_eval` returning
  `Object.keys(layout).filter(k => k.startsWith('xaxis') || k.startsWith('yaxis'))` mapped to their
  `.title` is a quick way to audit every axis on a chart at once, across every subplot Plotly.NET's
  `Chart.Grid` numbered.
- If this or a follow-up project adds Expecto: `dotnet run`, not `dotnet test`, is the right way to
  invoke it — but that's not set up anywhere in this repo yet.

## Workflow

- Work happens on the `web-frontend` branch; `main` is the original CLI-only history.
- **Never commit without being explicitly asked** ("commit it") — held for every single feature across
  this whole project, no exceptions made.
- Claude does not push — the user pushes themselves after reviewing.
- Commit messages explain *why*, not *what* — the diff already shows what changed.
- Deploy is automatic: `.github/workflows/deploy-pages.yml` builds and publishes to GitHub Pages on
  every push to `web-frontend`.

## Worth understanding before extending this further

A few concepts that came up and are worth being comfortable with, not just pattern-matching around:

- **Elmish/MVU** (Model–View–Update): one immutable `Model`, a closed `Message` union, a pure
  `update: Message -> Model -> Model`, and a `view` function re-run on every state change.
  `Web/State.fs` + `Web/View.fs` is the whole pattern in under 800 lines combined — worth reading end
  to end once rather than pattern-matching against it piecemeal.
- **LTTB (Largest-Triangle-Three-Buckets) downsampling** (`TouchstonePlot.fs`, `lttb`): why it's used
  instead of naive every-Nth-point decimation (it preserves narrow resonances/notches a naive stride
  would skip right over), and its single-pass, O(n) shape.
- **Why AOT matters for WASM specifically** — see Performance above; worth actually understanding the
  interpreter-vs-ahead-of-time-compiled distinction rather than treating "enable AOT" as a magic flag
  to flip later.
- **The Blazor stale-DOM-value diffing bug** above — this class of bug (rendered value unchanged, but
  live DOM changed by the user) will resurface in any Blazor project with typed/transformed input;
  recognizing it fast saves real debugging time.
- **Opt-out group-sync pattern**: when several items can be optionally kept in sync (here, each file's
  frequency-range slider — see `LoadedFile.FreqRangeLinked` / `SetFreqRange` in `State.fs`), a single
  global "linked" flag forces all-or-nothing. A per-item bool instead, combined with a small rule —
  *"always update the edited item itself; also update every other item if **both** the edited item and
  that other item are flagged as linked"* — lets any item opt out individually without a bigger
  architecture change. Compact enough to reach for whenever "sync some things, but not necessarily all
  of them" comes up again.
- **Numerical differentiation amplifies whatever noise is already in the input.** Group delay is
  `-1/(2π) · dφ/df`, a numerical derivative of measured phase — any measurement jitter gets amplified by
  it, worse with finer frequency steps (smaller `df` divisor). This isn't a bug to chase; it's inherent
  to differentiating real data. It gets *more visible*, not worse in absolute terms, once a shared trend
  is subtracted out (e.g. deviation-from-mean mode) — removing the common part shrinks the y-axis range
  the noise has to compete with. Recognize this shape (small-signal noise dominating a plot right after
  a "subtract the mean/trend" step) before assuming the subtraction logic itself is wrong.
- **Verify a smoothing/filter library with a spike-response test before trusting it.** The first NuGet
  package tried for Savitzky-Golay smoothing here (SignalSharp 0.1.10) looked right on paper (MIT, pure
  managed, clean API) but its `Apply` was silently a no-op for every interior point — a single-sample
  spike passed straight through unchanged instead of spreading across the window. A pure quadratic
  input (should reproduce exactly) or a single spike (should visibly spread/attenuate, not persist) are
  cheap, fast sanity checks that would catch this class of bug immediately; a "smoothed vs. original,
  do the numbers look plausible" glance would not have. Ended up hand-rolling the well-known window=7
  quadratic coefficient table instead (see `smoothed` in `TouchstonePlot.fs`) rather than trust an
  unverified small community package.
- **Windowing a one-sided, DC-anchored spectrum needs a DC-preserving taper, not a textbook symmetric
  window.** TDR's impedance step response (`tdrImpedance` in `TouchstonePlot.fs`) first came out wrong —
  collapsing back to 0 right after the transient instead of holding at the true plateau — because a
  plain Hann window (zero at *both* ends by construction) zeroed the spectrum's DC bin, and DC content is
  exactly what a step response's plateau height is carried by. The ringing this kind of window is meant
  to suppress actually comes from the hard cutoff at the *high*-frequency end (fMax), not from DC, so the
  fix is a taper that's unity at DC and only descends toward Nyquist — see `kaiserTaper`, the
  DC-preserving outward half of a symmetric Kaiser window (tunable ringing-vs-rise-time tradeoff via
  β, fixed at 6 here). Diagnosed by bisecting the pipeline stage-by-stage against a synthetic 50Ω→75Ω
  step at a known delay until windowed vs. unwindowed runs disagreed — the same "validate with synthetic
  ground truth" discipline as the Savitzky-Golay case above, just applied to a whole pipeline instead of
  one function.
- **FFT time-domain resolution in this kind of "lowpass equivalent" reconstruction is fixed by the
  highest frequency in the spectrum, not by the FFT length.** In `tdrImpedance`, `dt = 1/(2·fMax)`
  algebraically regardless of `nFft` — increasing `nFft` only extends how far out in time the result
  goes (more room before wraparound aliasing), it does not sharpen the step edge. Reach for more
  *measured* bandwidth (higher fMax), not a bigger FFT, if finer time resolution is ever needed.
- **lttb downsampling is a per-chart judgment call, not something to apply uniformly to every trace.**
  It was already skipped for small first-class datasets before, but got added by default to a new TDR
  chart out of habit — worth reconsidering, since TDR's native point count (2048, fixed by the FFT
  length) is nowhere near the thousands-of-points regime lttb exists for. Downsampling a chart whose
  whole point is being zoomed into (to see a step's sharpness/ringing) is actively counterproductive:
  lttb picks points by whole-curve significance, so a zoomed-in view shows the straight lines between
  *those* points, not a locally accurate curve — it looks jagged exactly where users look closest.
- **A window built for one purpose can silently contaminate a second use of the same pipeline stage.**
  TDR gating (`tdrGatedResponse` in `TouchstonePlot.fs`) forward-FFTs a gated impulse response back to a
  frequency response, and at first reused `tdrImpedance`'s Kaiser-windowed impulse response to do it. That
  window exists to smooth the *displayed step response* — a legitimate, one-way use — but baked its own
  high-frequency roll-off straight into the round-tripped reconstruction, caught by a sanity test that
  gates *nothing* (the full record) and checks the result reproduces the original spectrum: it didn't,
  until `tdrGatedResponse` was given its own unwindowed impulse response (`tdrOneSided` factored out and
  shared, `tdrImpulse` takes the taper as a parameter so each caller picks its own). Any time a
  time-domain/frequency-domain round trip reuses a windowed intermediate built for a *different*,
  one-directional purpose, check whether that window's effect actually survives being transformed back —
  it usually does, silently.

---

## General F# conventions (apply once this needs an API, database, or test suite)

Carried over from a wider team template. None of it is exercised by this project today — no API layer,
no database, no test project — but keep it as reference for if a follow-up project grows one.

### List literals mixing `match`/`if` need explicit layout
```fsharp
// ❌ offside-rule errors
let items = [
    match value with
    | Case1 -> "a"
    if cond then "b" else ""
]

// ✅
let items =
    [ match value with
      | Case1 -> "a"
      if cond then "b" else "" ]
```

### `match` in a `let` binding: put `match` on its own line
```fsharp
// ❌ offside error
let result = match x with
    | A -> 1

// ✅
let result =
    match x with
    | A -> 1
```

### Types in namespaces, functions in modules
For larger multi-file solutions: domain types/contracts at namespace level for shared accessibility,
implementation logic in modules. (This project deliberately doesn't do this split — see F# style above.)

### API error handling
If an API layer is added: every call returns `Async<Result<T, string>>`; never `failwith` in an API
implementation, always `Error "description"`. Client side: `match! api.call() with | Ok v -> ... | Error e -> ...`.

### Services as records, not classes
```fsharp
// ❌ not idiomatic F#
type DataService(provider: IProvider) =
    member _.process data = ...

// ✅
type DataService = { process: Data -> Async<Result<Output, string>> }
let createDataService (provider: IProvider) : DataService =
    { process = fun data -> async { ... } }
```
Exception here too: a type that *must* be a class for framework reasons (e.g. Bolero's
`ProgramComponent`) isn't a violation of this — it's not a hand-rolled service, it's satisfying a base
class contract.

### Expecto testing
`dotnet run`, not `dotnet test` (Expecto's filtering/output don't work right under `dotnet test`).
Always `testCaseAsync`, never `testCase` + `Async.RunSynchronously` (blocks threads, can deadlock).

### String interpolation compiler warnings
Avoid `$"..."` with embedded `if`/complex expressions inside single/verbatim-quoted strings (warning
3373) — use a `let` binding first, or a triple-quoted string. Escape literal `%` as `%%` when a format
specifier is also present (warning 3376). This project sidesteps the whole category by using `sprintf`
instead of string interpolation everywhere — consider doing the same in a new project rather than
managing these case by case.

### Security: parameterized queries
If a database is added: never build query strings via interpolation; always use parameterized query
APIs (`.WithParameter("@x", value)` or equivalent) to avoid injection.

### Naming
Never bake implementation-phase references ("Phase1", "Phase2") into file/type/function names or
comments — they lose meaning once the phase is history. Name things after what they do.

### Logging
If structured logging is added: pass values as structured parameters (`Log.Information("... {Id}", id)`),
not pre-formatted into the message string.
