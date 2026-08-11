# CLAUDE.md

Guidance for Claude Code in this repository. FieldPlay is the follow-up project planned in
`Projektvorschlag.md` (read that first — it defines the architecture, milestones M0–M5, and the
verification strategy). Conventions here are carried over from
[touchstone_reader](https://github.com/Balupi/touchstone_reader)'s CLAUDE.md, where they were
hard-won; the Mibo/tooling notes below were learned setting this repo up.

## Commands

- Build: `dotnet build` — no test suite or linter yet; the build plus running the app is the loop.
- Run: `dotnet run` (needs a display). Headless: `xvfb-run -a dotnet run` works with software GL,
  and `xwd -root | convert xwd:- shot.png` against a background `Xvfb` display captures a real
  screenshot for verification.
- Format: `dotnet tool restore && dotnet fantomas .` (pinned in `dotnet-tools.json` by the template).

## Mibo notes

- **`AGENTS.md` (from the Mibo template) is the API map** — per-topic doc links and the mandatory
  architecture pattern (routed sub-systems) once `update` grows. Follow it; don't reinvent building
  blocks Mibo already ships. When an exact signature is needed and the docs site is unreachable,
  reflection over the NuGet-cached assemblies works (`Assembly.LoadFrom` on
  `~/.nuget/packages/mibo.core/<ver>/lib/net10.0/Mibo.Core.dll` in `dotnet fsi`).
- **FSharp.Core must be pinned with `<PackageReference Update=...>`** (not `Include` — F# projects
  get an implicit reference, and `Include` produces a duplicate-reference warning instead of an
  override). Mibo 4.1 requires a newer FSharp.Core than the SDK's implicit one; without the pin
  every build warns NU1605.
- **Draw DSL**: view fills a `RenderBuffer2D` via fluent `.fillRect(...)/.text(...)` calls and ends
  with `.drop()`. `text` needs an explicit font — `Raylib.GetFontDefault()` is fine (only valid
  once the window exists, i.e. inside view, not at module init).
- The "Failed to initialize playback device" ALSA warning on machines without a sound card is
  harmless noise, not a bug.

## Environment notes (Claude Code remote sessions)

- The NuGet **search** endpoint (`azuresearch-*.nuget.org`) can be blocked while the package
  **download** endpoints work. `dotnet new install Mibo.Templates` then fails with "no NuGet feeds
  are configured"; the fix is downloading the `.nupkg` from
  `api.nuget.org/v3-flatcontainer/...` and installing from the local file. Plain `dotnet restore`
  is unaffected.
- The .NET SDK installs from the Ubuntu archive (`apt-get install dotnet-sdk-10.0`) when
  Microsoft's own download hosts are blocked.
- Mibo's docs site (`angelmunoz.github.io`) may be blocked — see the reflection fallback above.

## F# style

Same rules as touchstone_reader, abbreviated:

- Discriminated unions, never enums. `sprintf`, not string interpolation.
- Functions 10–25 lines; split view/update functions before they sprawl. `private` by default.
- Pure domain code (scene types, `FieldFdm`) in framework-free files — no Mibo/raylib imports there.
- Types and functions in one `module` per file; no `namespace` + nested `module` split without reason.
- 2-space indentation and the fluent-DSL layout follow the Mibo template (enforced by fantomas),
  not touchstone_reader's 4-space style — stay consistent with the template here.

## Verification

Same discipline as touchstone_reader: **synthetic ground truth before graphics.** Milestone M1's
solver checks (parallel-plate capacitor, stripline regression against touchstone_reader's verified
`StriplineFdm`, seed invariance, degenerate-edge handling) must pass as console checks before UI
work builds on the solver. Mibo also ships a headless mode for running the MVU loop in virtual
time — the right tool once loop-level tests exist. If Expecto is added: `dotnet run`, not
`dotnet test`.

## Workflow

- `main` is the default branch.
- **Never commit without being explicitly asked** — same rule as touchstone_reader.
- Commit messages explain *why*, not *what*.
