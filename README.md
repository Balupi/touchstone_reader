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

Results are grouped into four collapsible sections (Magnitude open by
default, the rest collapsed), each with its own S-parameter toggle buttons
scoped to what it can show:

- **Magnitude (dB)** / **Phase (deg)** — S11/S21/S12/S22, laid out as a
  VNA-style quad grid (1–4 panels depending on the selection).
- **Smith Chart** — S11/S22 (the reflection coefficients).
- **Group Delay (ns)** — S21/S12 (the transmission coefficients),
  `-1/(2π) · dφ/df` with the phase unwrapped first.

A status line reports parsing/rendering progress, and large sweeps (into the
thousands of points) are downsampled per trace via Largest-Triangle-Three-
Buckets before charting, so the page stays responsive without losing narrow
resonances or notches. Charts follow the browser's light/dark theme; the
download button on each chart always exports a black-on-white print-style
JPEG regardless of the on-screen theme.

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
