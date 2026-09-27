# TouchstoneReader

Reads Touchstone RF network-parameter files (`.s1p`, `.s2p`, `.s3p`, `.s4p`, `.sNp`) —
both legacy (v1.0/1.1) and v2.0 keyword-based formats — and plots them with Plotly.NET.

## Run

```
dotnet run -- path/to/file.s2p
```

Prints a summary and opens magnitude (dB) and phase (deg) charts, overlaying every
parameter (S11, S21, ... or Y/Z/H/G equivalents), in your default browser. For
S-parameter files, also opens a Smith chart of the input reflection coefficients
(S11, S22, ...).

## Web frontend

```
dotnet run --project Web/TouchstoneReader.Web.fsproj
```

Starts a local dev server (default `http://localhost:5000`) with a page to drag
and drop one or more Touchstone files onto. Everything runs client-side (Blazor
WebAssembly via [Bolero](https://fsbolero.io), Elmish architecture, Bulma
styling) — files are parsed in the browser and never uploaded anywhere. Drop
several files at once to overlay them for comparison; each can be removed
individually. Dropping a folder searches it (including subfolders) for
Touchstone-looking files and loads each one found, named with its path
relative to the dropped folder (e.g. `sub/device1.s2p`) so files with the
same name in different subfolders don't collide.

### Files

The file list is kept in natural alphanumeric order — digit runs compare by
value, so `c2.s2p` sorts before `c10.s2p` — regardless of the order files were
dropped in or the order their reads happened to finish. Colors are then handed
out sequentially down that list from a 16-color palette following the DIN 47100
wire-color code (white, brown, green, yellow, grey, pink, blue, red, then the
same eight again as lighter twins), and each file keeps its color across every
chart, with a matching swatch in the file list.

Clicking a file's swatch — or its legend entry on any chart — hides that file's
curves everywhere at once; the swatch fades to show it's hidden.
Double-clicking a legend entry isolates that one file and hides all the others
(double-click again to bring them back). Hovering a legend entry highlights
that file's curve and dims the rest, without changing anything.

Each file's own collapsible "Details" holds:

- Port count, point count, parameter/format/reference impedance.
- Any header comment lines (`!...`) from before the file's data starts —
  where instrument/calibration/date info usually lives, if present.
- A two-handle frequency-range slider (plus plain number inputs, which snap
  to the nearest actual data point) to crop what's plotted/exported for
  that file. A "Link range with other files" checkbox keeps several files'
  sliders in sync — dragging one moves every other linked file's range to
  match; unchecking it lets that one file's range move independently.
- For files with an even port count above two (4, 6, 8, …), a **port layout**
  switch: which ports face
  which end of the assembly, either `1..N/2 | N/2+1..N` (through paths S31,
  S42 on a 4-port file) or `1-2, 3-4, ...` (through paths S21, S43). A
  Touchstone file carries none of this — the numbering is whatever the
  measurement setup used — so there is deliberately **no default**: until it's
  stated, the file is flagged in the list and the parameter groups below
  (Through/NEXT/FEXT) and the Group Delay section stay unavailable rather than
  guessing, which would relabel crosstalk as insertion loss. 1- and 2-port
  files need no statement, and an odd port count has no second end to lay
  anything across, so neither shows the switch.

### Charts

Grouped into five collapsible sections (Magnitude open by default, the rest
collapsed), each with its own parameter picker (S11+S21 selected by default)
scoped to what it can show. What the picker offers follows the port count of
the loaded files, not a fixed 2-port assumption: a 2-port file gets the four
familiar toggle buttons, and an N-port file gets buttons for whole parameter
groups — **Reflection** (Sii), **Through**, **NEXT** (crosstalk measured at
the same end) and **FEXT** (crosstalk measured at the opposite end) — plus the
full N×N matrix behind a toggle, where each cell's tooltip names what that
parameter is under the stated port layout. Group buttons select the forward
direction only, since a passive assembly is reciprocal; S13 next to S31 is a
click away in the matrix for anyone using the difference as a
measurement-quality check.

With several files loaded, the selection is shared and each subplot simply
overlays the files that have that parameter — a 2-port and a 4-port file can
be open together, contributing to S11/S21 and to S31/S42 respectively. Grids
stop at 9 subplots (past that the cells stop being readable) and say how many
were left out.

- **Magnitude (dB)** / **Phase (deg)** — any Sij, laid out as a grid: the
  conventional VNA quad (S11, S21, S12, S22) for a 2-port file, two columns
  up to four parameters and three beyond that. The grid's reserved footprint
  never falls below that quad's, so fewer selected parameters stretch to fill
  it instead of shrinking the page layout. A 1-port reflection measurement
  gets its S11 chart here too.
- **Smith Chart** — the reflection coefficients (S11, S22, ... the whole
  diagonal, whatever the port count). Its own nested,
  independently-collapsible **VSWR** sub-section plots the same reflection
  coefficients' magnitude as `(1+|Γ|)/(1-|Γ|)` against frequency instead —
  a frequency-domain scalar reading of the same data the Smith chart already
  shows as a complex trajectory.
- **Group Delay (ns)** — each line's through path (S21/S12 on a 2-port file;
  S31/S42 and their reverses on a 4-port file under `1..N/2 | N/2+1..N`),
  `-1/(2π) · dφ/df` with the phase unwrapped first. Crosstalk paths are
  deliberately not offered, and the section explains itself instead of
  plotting anything while a file's port layout is unstated. Switchable between each
  file's absolute curve and its deviation from the pointwise mean across all
  loaded files (useful for spotting how much units differ from one
  another), and optional Savitzky-Golay smoothing — group delay is a
  numerical derivative of phase, which amplifies whatever measurement noise
  is already in the raw data.
- **TDR Impedance (Ω)** — the reflection coefficients (S11, S22, ... one per
  port) converted from the frequency domain to a
  time-domain impedance profile via inverse FFT: extrapolated flat down to
  DC, resampled onto a uniform grid, tapered with a Kaiser window (unity at
  DC so the reconstructed step response holds its true plateau, tapering off
  only toward the high-frequency end where the hard measurement-bandwidth
  cutoff actually causes ringing), then converted from reflection
  coefficient to impedance via `Z(t) = Z0·(1+ρ(t))/(1-ρ(t))`. A two-handle
  time-gate slider — also shown as a pair of colored guide lines directly on
  the curve, draggable there instead of using the slider/number inputs, and
  clamped so they can't cross each other — marks the range used by its own
  nested, independently-collapsible **Gated Magnitude (dB)** sub-section,
  which forward-FFTs the gated time window back to a frequency response,
  isolating whichever reflection/discontinuity falls inside the gate from
  others sharing the same line. Ungated (the default) it reproduces the
  ordinary reflection magnitude as a sanity check.

### Limit lines and limit test

The Magnitude section holds a collapsible **Limit Lines** sub-section with one
table per selected parameter, in the same column order a Keysight NVA uses:
TYPE (OFF / MIN / MAX), begin and end stimulus (GHz), begin and end response
(dB). Each row is one straight segment of the mask and tests only the
frequencies it spans; a table that is empty or all OFF tests nothing. The mask
is drawn dashed on the magnitude chart, points that violate it are marked with
red X markers, and each file in the list gets a **PASS**/**FAIL** badge for the
tables covering it. Only visible curves are judged — hiding a file via its
swatch removes its badge rather than leaving a stale verdict beside it.

Tables are shared across all loaded files (comparing several assemblies against
one spec is the point) and are kept in the browser's local storage, so they
survive a reload. A restored table says so in the section's summary, since a
spec carried over from a previous session decides PASS/FAIL for whatever files
happen to be loaded now, and can be discarded from there. With files of
different port counts loaded, note that a table is keyed by (i,j) alone: S21 is
the insertion loss of a 2-port file but near-end crosstalk of a 4-port one
under `1..N/2 | N/2+1..N`, and one shared S21 table would test both.

Magnitude, Group Delay, TDR, and VSWR can each show a min/max reference line
(with the value labeled at the axis) for the global extreme across every
loaded file, off by default and toggled independently per section. Every chart's legend shows one
entry per file (not one per parameter); every chart has a CSV-export button
(full-precision, not the downsampled display data) next to its parameter
toggles, in addition to the same option in Plotly's own toolbar.

A status line reports parsing/rendering progress, and large sweeps (into the
thousands of points) are downsampled per trace via Largest-Triangle-Three-
Buckets before charting, so the page stays responsive without losing narrow
resonances or notches. Charts follow the browser's light/dark theme; the
download button on each chart always exports a white-background print-style
JPEG regardless of the on-screen theme, keeping each file's own color.

### Deploy to GitHub Pages

`.github/workflows/deploy-pages.yml` publishes the web app and deploys it as
a GitHub Pages project site on every push to `web-frontend` (or via manual
dispatch from the Actions tab). One-time setup: in the repo's **Settings →
Pages**, set **Source** to **GitHub Actions**. The site then lives at
`https://<owner>.github.io/<repo>/`.

## Use as a library

```fsharp
open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot

let data = Touchstone.read "amplifier.s2p"
// data.Ports, data.Frequencies (Hz), data.Matrices (Complex[,] per frequency, 1-indexed)

magnitudeChart data |> Chart.show
magnitudeGrid data |> Chart.show   // small-multiples, one chart per Sij
smithChart data |> Chart.show      // S-parameters only: Sii on a Smith grid
```

## Stripline impedance calculator

```
dotnet run --project StriplineCalc/StriplineCalc.fsproj
```

A separate, standalone Bolero web app (`StriplineCalc/`), deployed alongside
the Touchstone frontend at `https://<owner>.github.io/<repo>/stripline/`, so
it also works in the browser on tablets and phones. It computes the
characteristic impedance (plus delay, C' and L' per meter) of an asymmetric
(offset) stripline: a trace of width `w` and thickness `t` between two
ground planes, separated from them by dielectric heights `h1` and `h2`, each
side with its own relative permittivity (`εr1`/`εr2`, e.g. core vs.
prepreg — a capacitance-weighted effective permittivity combines them, and
εeff is shown alongside the results). Lengths can be in any consistent
unit — only the ratios matter. It can also solve for the width that hits a
target impedance.

Method: Cohn's symmetric-stripline solution (exact elliptic-integral form for
zero thickness, his narrow/wide-strip corrections for finite thickness), with
the offset handled as the parallel combination of the two symmetric
half-structures (spacings `2·h1+t` and `2·h2+t`) as in Wadell's *Transmission
Line Design Handbook*. Warnings are shown when the geometry leaves the
approximations' validity range. The math lives in `Stripline.fs` (pure, no
dependencies), usable as a library via `TouchstoneReader.Stripline.impedance`
and `widthForImpedance`.

A "Verify numerically (FDM)" button cross-checks the closed form with a
2D electrostatic finite-difference solve of the actual cross-section
(`StriplineFdm.fs`): box-integration stencil on a tensor grid whose lines
land exactly on the conductor faces and dielectric interface, SOR iteration,
capacitance from the field energy, two resolutions Richardson-extrapolated
with the grid spread reported as an uncertainty estimate. Validated to
&lt;0.1 % against Cohn's exact solution; it has none of the closed form's
validity limits, so it's the better answer for strongly asymmetric or
high-contrast geometries (a few seconds per solve under interpreted WASM).

## Coverage

- Legacy v1.x: any `.sNp`, comment lines (`!`), option line (`#`), `DB`/`MA`/`RI`,
  any frequency unit, the 2-port legacy ordering quirk (S11, S21, S12, S22).
- v2.0: `[Version]`, `[Number of Ports]`, `[Reference]`, `[Matrix Format]`
  (Full/Lower/Upper), `[Two-Port Data Order]`, `[Network Data]` / `[End]`.
- Any port count, in the CLI and the web frontend alike: the parameters each chart
  offers, their display order and the defaults are derived from the file's port count
  (`PortMap.fs`), not hardcoded for 2 ports. What a parameter *means* — through path,
  near-end or far-end crosstalk — additionally needs the port layout stated per file,
  since no Touchstone file carries it.
- Not covered: noise-data blocks are parsed past but not exposed/plotted; mixed-mode
  parameters (SDD21 and friends); limit tables are keyed per (i,j), not per parameter
  group, so one table means different things to files of different port counts.

## License

MIT — see [LICENSE](LICENSE).

This project depends on [Bolero](https://fsbolero.io) (Apache License 2.0) for
the web frontend, and [Plotly.NET](https://plotly.net) and
[plotly.js](https://plotly.com/javascript/) (both MIT) for charting. Their own
licenses apply to those libraries; using TouchstoneReader under the MIT
license above doesn't change that.
