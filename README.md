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
and drop a Touchstone file onto — it's parsed entirely in the browser (Blazor
WebAssembly via [Bolero](https://fsbolero.io), Elmish architecture, Bulma
styling) and renders the same magnitude, phase, and Smith charts as the CLI.
No file is ever uploaded anywhere.

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
