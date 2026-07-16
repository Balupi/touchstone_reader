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
individually.

### Files

Each loaded file gets a deterministic color (hashed from its filename, so
it's stable regardless of load order) that's used consistently for that
file's traces across every chart, plus a matching swatch in the file list.
Clicking a file's swatch — or its legend entry on any chart — hides that
file's curves everywhere at once; the swatch fades to show it's hidden.
Each file's own collapsible "Details" holds:

- Port count, point count, parameter/format/reference impedance.
- Any header comment lines (`!...`) from before the file's data starts —
  where instrument/calibration/date info usually lives, if present.
- A two-handle frequency-range slider (plus plain number inputs, which snap
  to the nearest actual data point) to crop what's plotted/exported for
  that file. A "Link range with other files" checkbox keeps several files'
  sliders in sync — dragging one moves every other linked file's range to
  match; unchecking it lets that one file's range move independently.

### Charts

Grouped into five collapsible sections (Magnitude open by default, the rest
collapsed), each with its own S-parameter toggle buttons (S11+S21 selected
by default) scoped to what it can show:

- **Magnitude (dB)** / **Phase (deg)** — S11/S21/S12/S22, laid out as a
  VNA-style quad grid. The grid's reserved footprint stays constant
  regardless of selection, so fewer selected parameters stretch to fill it
  instead of shrinking the page layout.
- **Smith Chart** — S11/S22 (the reflection coefficients).
- **Group Delay (ns)** — S21/S12 (the transmission coefficients),
  `-1/(2π) · dφ/df` with the phase unwrapped first. Switchable between each
  file's absolute curve and its deviation from the pointwise mean across all
  loaded files (useful for spotting how much units differ from one
  another), and optional Savitzky-Golay smoothing — group delay is a
  numerical derivative of phase, which amplifies whatever measurement noise
  is already in the raw data.
- **TDR Impedance (Ω)** — S11/S22 converted from the frequency domain to a
  time-domain impedance profile via inverse FFT: extrapolated flat down to
  DC, resampled onto a uniform grid, tapered with a Kaiser window (unity at
  DC so the reconstructed step response holds its true plateau, tapering off
  only toward the high-frequency end where the hard measurement-bandwidth
  cutoff actually causes ringing), then converted from reflection
  coefficient to impedance via `Z(t) = Z0·(1+ρ(t))/(1-ρ(t))`.

Magnitude, Group Delay, and TDR can each show a min/max reference line (with
the value labeled at the axis) for the global extreme across every loaded
file, toggled independently per section. Every chart's legend shows one
entry per file (not one per parameter); every chart has a CSV-export button
(full-precision, not the downsampled display data) next to its parameter
toggles, in addition to the same option in Plotly's own toolbar.

A status line reports parsing/rendering progress, and large sweeps (into the
thousands of points) are downsampled per trace via Largest-Triangle-Three-
Buckets before charting, so the page stays responsive without losing narrow
resonances or notches. Charts follow the browser's light/dark theme; the
download button on each chart always exports a black-on-white print-style
JPEG regardless of the on-screen theme, with a distinct dash pattern per
file so they stay distinguishable once color is gone.

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

## Coverage

- Legacy v1.x: any `.sNp`, comment lines (`!`), option line (`#`), `DB`/`MA`/`RI`,
  any frequency unit, the 2-port legacy ordering quirk (S11, S21, S12, S22).
- v2.0: `[Version]`, `[Number of Ports]`, `[Reference]`, `[Matrix Format]`
  (Full/Lower/Upper), `[Two-Port Data Order]`, `[Network Data]` / `[End]`.
- Not covered: noise-data blocks are parsed past but not exposed/plotted; mixed-mode
  parameters.

## License

MIT — see [LICENSE](LICENSE).

This project depends on [Bolero](https://fsbolero.io) (Apache License 2.0) for
the web frontend, and [Plotly.NET](https://plotly.net) and
[plotly.js](https://plotly.com/javascript/) (both MIT) for charting. Their own
licenses apply to those libraries; using TouchstoneReader under the MIT
license above doesn't change that.
