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
