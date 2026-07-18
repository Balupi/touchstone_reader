/// Plotly.NET charts for Touchstone network-parameter data.
module TouchstoneReader.TouchstonePlot

open System
open System.Numerics
open Plotly.NET
open Plotly.NET.LayoutObjects
open TouchstoneReader.Touchstone

let private toDb (c: Complex) = 20.0 * log10 c.Magnitude
let private toDeg (c: Complex) = c.Phase * 180.0 / Math.PI

/// Points per trace above which lttb kicks in. Comfortably more than any
/// chart's pixel width, so the reduction is invisible at normal zoom levels.
let private maxPointsPerTrace = 1500

/// Fixed per-file palette, assigned explicitly per trace instead of leaving
/// color to Plotly's default per-trace-index cycling — that cycle runs
/// across *all* traces in a subplot grid, not per file, so the same file
/// previously came out a different color in every subplot. Also reused as
/// the web UI's file-list swatch, so what's plotted always matches what's
/// shown there. On-screen this is the only thing distinguishing files (all
/// lines stay solid); the monochrome/print export additionally assigns each
/// file its own dash pattern client-side (interop.js), since color alone
/// can't survive being flattened to black there.
let private filePalette =
    [| "#3298dc"; "#f14668"; "#48c78e"; "#ffdd57"; "#485fc7"; "#00d1b2"; "#ff6b81"; "#9b59b6" |]

/// Deterministic index from a file's label, so its color stays the same
/// across renders and doesn't shift when other files are added/removed —
/// unlike an index into the current file list, which would.
let private stableIndex (n: int) (label: string) =
    let h = hash label
    ((h % n) + n) % n

/// Deterministic per-file color — same file, same color, in every chart it
/// appears in. Also used by the web UI for each file's color swatch.
let fileColor (label: string) = filePalette.[stableIndex filePalette.Length label]

/// A line trace, colored per-file when `label` is a real filename (multi-file
/// web overlays); left to Plotly's own default per-trace-index coloring when
/// `label = ""` (single-file CLI charts, which need each Sij distinguished
/// from the others, not each file — there's only one file).
let private styledLine (label: string) (name: string) (xs: float[]) (ys: float[]) =
    if label = "" then
        Chart.Line(x = xs, y = ys, Name = name)
    else
        Chart.Line(x = xs, y = ys, Name = name, LineColor = Color.fromString (fileColor label))

/// Largest-Triangle-Three-Buckets downsampling: reduces a series to
/// `threshold` points while preferentially keeping visually significant ones
/// (peaks, dips, notches), unlike naive every-Nth-point decimation which can
/// hide a narrow resonance or filter notch entirely. No-op below threshold.
let private lttb (threshold: int) (points: (float * float)[]) =
    let n = points.Length

    if threshold >= n || threshold <= 2 then
        points
    else
        let sampled = ResizeArray<float * float>(threshold)
        sampled.Add points.[0]
        let every = float (n - 2) / float (threshold - 2)
        let mutable a = 0

        for i in 0 .. threshold - 3 do
            let avgRangeStart = int (floor ((float i + 1.0) * every)) + 1
            let avgRangeEnd = min (int (floor ((float i + 2.0) * every)) + 1) n |> max (avgRangeStart + 1)

            let mutable avgX = 0.0
            let mutable avgY = 0.0

            for k in avgRangeStart .. avgRangeEnd - 1 do
                let (x, y) = points.[k]
                avgX <- avgX + x
                avgY <- avgY + y

            avgX <- avgX / float (avgRangeEnd - avgRangeStart)
            avgY <- avgY / float (avgRangeEnd - avgRangeStart)

            let rangeStart = int (floor ((float i + 0.0) * every)) + 1
            let rangeEnd = int (floor ((float i + 1.0) * every)) + 1
            let (ax, ay) = points.[a]
            let mutable maxArea = -1.0
            let mutable nextA = rangeStart

            for k in rangeStart .. rangeEnd - 1 do
                let (px, py) = points.[k]
                let area = abs ((ax - avgX) * (py - ay) - (ax - px) * (avgY - ay)) * 0.5

                if area > maxArea then
                    maxArea <- area
                    nextA <- k

            sampled.Add points.[nextA]
            a <- nextA

        sampled.Add points.[n - 1]
        sampled.ToArray()

/// A rendered chart plus a thunk producing a CSV rendition of the same
/// (non-downsampled) data, for the web app's per-chart CSV download button.
/// `Csv` is a function rather than an already-built string because building
/// it (a full string per point, unlike the chart itself which is capped at
/// maxPointsPerTrace) is real, avoidable work — under WASM this was showing
/// up as multiple extra seconds per render for large real-world sweeps, paid
/// on every render even though the button is clicked rarely. The
/// single-file/CLI chart functions don't need any of this — only the web
/// app's multi-file `*Multi` functions return it.
type ChartResult =
    { Chart: GenericChart.GenericChart
      Csv: unit -> string }

/// CSV with each series as its own "label"/"label" x/y column pair —
/// ragged (shorter series get blank cells) rather than interpolated onto a
/// shared grid, since files can have different frequency points and
/// interpolating would silently alter the real measured values.
let private toCsv (xLabel: string) (yLabel: string) (series: (string * float[] * float[]) list) =
    let maxLen = series |> List.map (fun (_, xs, _) -> xs.Length) |> List.fold max 0

    let header =
        series
        |> List.collect (fun (label, _, _) ->
            [ sprintf "\"%s %s\"" label xLabel |> fun s -> s.Trim()
              sprintf "\"%s %s\"" label yLabel |> fun s -> s.Trim() ])
        |> String.concat ","

    let row r =
        series
        |> List.collect (fun (_, xs, ys) -> if r < xs.Length then [ string xs.[r]; string ys.[r] ] else [ ""; "" ])
        |> String.concat ","

    header + "\n" + ([ for r in 0 .. maxLen - 1 -> row r ] |> String.concat "\n")

/// Fixed colors for the global min/max reference lines — deliberately
/// outside `_colorway` (interop.js) so they never coincide with a data
/// trace's own color and always read as reference lines, not data.
let private minColor = "#e0a12e"
let private maxColor = "#e63946"

/// The global minimum and maximum across several (xs, ys) series in one
/// subplot/chart, as a horizontal dashed line spanning the data's x-range
/// plus a value label sitting at the y-axis — instead of one marker per
/// file, which got noisy fast with several files loaded and didn't scale to
/// "what's the worst case across everything I've loaded".
let private extremumMarkers (xref: string) (yref: string) (unit: string) (series: (string * float[] * float[]) list) =
    let flat =
        [ for (_, xs, ys) in series do
            for k in 0 .. ys.Length - 1 -> xs.[k], ys.[k] ]

    match flat with
    | [] -> [], []
    | _ ->
        let allX = flat |> List.map fst
        let xMin, xMax = List.min allX, List.max allX
        let allY = flat |> List.map snd
        let yMin, yMax = List.min allY, List.max allY

        let line (color: string) (y: float) =
            Shape.init (
                ShapeType = StyleParam.ShapeType.Line,
                X0 = xMin,
                X1 = xMax,
                Y0 = y,
                Y1 = y,
                Xref = xref,
                Yref = yref,
                Line = Line.init (Color = Color.fromString color, Dash = StyleParam.DrawingStyle.Dash, Width = 1.0)
            )

        let label (color: string) (kind: string) (y: float) =
            Annotation.init (
                X = xMin,
                Y = y,
                XRef = xref,
                YRef = yref,
                XAnchor = StyleParam.XAnchorPosition.Right,
                Text = sprintf "%s: %.3g%s" kind y unit,
                ShowArrow = false,
                Font = Font.init (Size = 10.0, Color = Color.fromString color)
            )

        [ line minColor yMin; line maxColor yMax ], [ label minColor "Min" yMin; label maxColor "Max" yMax ]

/// One line trace for Sij (or Yij/Zij/...) of one file, `toY` picking the
/// scalar to plot from each complex value. `label` (e.g. a filename) is
/// prepended to the trace name when overlaying multiple files; pass "" for none.
let private oneParamTrace (label: string) (toY: Complex -> float) (i: int) (j: int) (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    let ys = data.Matrices |> Array.map (fun m -> toY m.[i, j])
    let points = Array.zip freqGHz ys |> lttb maxPointsPerTrace
    let name = sprintf "%A%d%d" data.Option.Parameter i j
    let name = if label = "" then name else sprintf "%s %s" label name
    styledLine label name (points |> Array.map fst) (points |> Array.map snd)

/// Line traces for every Sij (or Yij/Zij/...) of one file.
let private paramTraces (label: string) (toY: Complex -> float) (data: TouchstoneFile) =
    [ for i in 1 .. data.Ports do
        for j in 1 .. data.Ports -> oneParamTrace label toY i j data ]

/// One overlaid chart: magnitude (dB) of every parameter vs frequency (GHz).
let magnitudeChart (data: TouchstoneFile) =
    paramTraces "" toDb data
    |> Chart.combine
    |> Chart.withTitle (sprintf "%A magnitude (dB)" data.Option.Parameter)
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Magnitude (dB)"

/// One overlaid chart: phase (deg) of every parameter vs frequency (GHz).
let phaseChart (data: TouchstoneFile) =
    paramTraces "" toDeg data
    |> Chart.combine
    |> Chart.withTitle (sprintf "%A phase (deg)" data.Option.Parameter)
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Phase (deg)"

/// Magnitude (dB) of every parameter vs frequency (GHz), overlaid across
/// several labeled files (e.g. filenames) for comparison.
let magnitudeChartMulti (files: (string * TouchstoneFile) list) =
    files
    |> List.collect (fun (label, data) -> paramTraces label toDb data)
    |> Chart.combine
    |> Chart.withTitle "Magnitude (dB)"
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Magnitude (dB)"

/// Phase (deg) of every parameter vs frequency (GHz), overlaid across several
/// labeled files (e.g. filenames) for comparison.
let phaseChartMulti (files: (string * TouchstoneFile) list) =
    files
    |> List.collect (fun (label, data) -> paramTraces label toDeg data)
    |> Chart.combine
    |> Chart.withTitle "Phase (deg)"
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Phase (deg)"

/// Grid of small-multiple magnitude (dB) charts, one per Sij (or Yij/Zij/...).
let magnitudeGrid (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    let n = data.Ports
    [ for i in 1 .. n do
        for j in 1 .. n ->
            let ys = data.Matrices |> Array.map (fun m -> toDb m.[i, j])
            Chart.Line(x = freqGHz, y = ys, Name = sprintf "%A%d%d" data.Option.Parameter i j)
            |> Chart.withTitle (sprintf "%A%d%d" data.Option.Parameter i j)
            |> Chart.withXAxisStyle "Frequency (GHz)"
            |> Chart.withYAxisStyle "Magnitude (dB)" ]
    |> Chart.Grid(n, n)
    |> Chart.withSize (350 * n, 300 * n)

/// Conventional VNA quad order: S11 top-left, S21 top-right, S12
/// bottom-left, S22 bottom-right.
let magnitudeQuadOrder = [ (1, 1); (2, 1); (1, 2); (2, 2) ]

/// Grid of subplots for 2-port files, one per selected (i,j) parameter (e.g.
/// `[ (1,1); (2,1) ]` for S11+S21), laid out left-to-right top-to-bottom in
/// up to 2 columns. Each subplot overlays every labeled file's trace for
/// that parameter (`toY` picks the scalar plotted); files that aren't
/// 2-port are skipped. When `showExtrema` is set, each subplot is annotated
/// with the global min/max across all its overlaid files. Returns None if
/// `selected` is empty.
let private quadMulti
    (title: string)
    (unit: string)
    (toY: Complex -> float)
    (showExtrema: bool)
    (selected: (int * int) list)
    (files: (string * TouchstoneFile) list)
    =
    if selected.IsEmpty then
        None
    else
        let files2p = files |> List.filter (fun (_, data) -> data.Ports = 2)

        // Chart.Grid collapses a per-subplot Chart.withTitle into one shared
        // title (only the last one wins), but each subplot keeps its own
        // axes — so the per-cell label goes on the Y axis instead.
        let subplot idx (i, j) =
            let series =
                files2p
                |> List.map (fun (label, data) ->
                    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
                    let ys = data.Matrices |> Array.map (fun m -> toY m.[i, j])
                    (sprintf "%s S%d%d" label i j).Trim(), freqGHz, ys)

            let chart =
                files2p
                |> List.map (fun (label, data) -> oneParamTrace label toY i j data)
                |> Chart.combine
                |> Chart.withXAxisStyle "Frequency (GHz)"
                |> Chart.withYAxisStyle (sprintf "S%d%d" i j)

            let shapes, annotations =
                if showExtrema then
                    let xref = if idx = 0 then "x" else sprintf "x%d" (idx + 1)
                    let yref = if idx = 0 then "y" else sprintf "y%d" (idx + 1)
                    extremumMarkers xref yref unit series
                else
                    [], []

            chart, shapes, annotations, series

        let cols = min 2 selected.Length
        let rows = (selected.Length + cols - 1) / cols

        let results = selected |> List.mapi subplot
        let shapes = results |> List.collect (fun (_, s, _, _) -> s)
        let annotations = results |> List.collect (fun (_, _, a, _) -> a)
        let allSeries = results |> List.collect (fun (_, _, _, s) -> s)

        let chart =
            results
            |> List.map (fun (c, _, _, _) -> c)
            |> Chart.Grid(rows, cols)
            |> Chart.withTitle title
            // Fixed at the full 2x2 quad's footprint regardless of how many
            // of the (at most 4) parameters are actually selected, so
            // toggling one off doesn't shrink the page layout — instead
            // whatever's still selected stretches to fill that same space
            // (e.g. one lone subplot fills the whole area instead of
            // sitting small in a corner). Width is moot: interop.js's
            // _makeResponsive always strips it in favor of autosize.
            |> Chart.withSize (450 * 2, 350 * 2)
            |> Chart.withShapes shapes
            |> Chart.withAnnotations annotations

        Some { Chart = chart; Csv = fun () -> toCsv "Frequency (GHz)" unit allSeries }

/// Grid of magnitude (dB) subplots, each optionally annotated with its
/// global min/max — see quadMulti.
let magnitudeQuadMulti showExtrema selected files = quadMulti "Magnitude (dB)" "dB" toDb showExtrema selected files

/// Grid of phase (deg) subplots — see quadMulti. No min/max annotations:
/// wrapped phase makes a single global extremum meaningless.
let phaseQuadMulti selected files = quadMulti "Phase (deg)" "deg" toDeg false selected files

let private circlePoints (cx: float) (cy: float) (r: float) (n: int) =
    [| for k in 0 .. n ->
        let theta = 2.0 * Math.PI * float k / float n
        (cx + r * cos theta, cy + r * sin theta) |]

/// Keeps the longest run of points lying within the unit disk (|Γ| <= 1),
/// treating the array as circular so a run spanning the wrap-around isn't split.
let private clipToUnitDisk (points: (float * float)[]) =
    let n = points.Length
    let inside i = let (x, y) = points.[i] in x * x + y * y <= 1.0 + 1e-9
    match [| 0 .. n - 1 |] |> Array.tryFind (inside >> not) with
    | None -> points
    | Some cut ->
        let idx = [| for k in 0 .. n - 1 -> (cut + k) % n |]
        let mutable bestStart, bestLen, curStart, curLen = 0, 0, -1, 0
        for k in 0 .. n - 1 do
            if inside idx.[k] then
                if curStart < 0 then curStart <- k
                curLen <- curLen + 1
                if curLen > bestLen then
                    bestStart <- curStart
                    bestLen <- curLen
            else
                curStart <- -1
                curLen <- 0
        [| for k in bestStart .. bestStart + bestLen - 1 -> points.[idx.[k]] |]

// Constant-resistance circle for normalized resistance r: center (r/(1+r), 0), radius 1/(1+r).
// Always internally tangent to the unit circle at Γ=(1,0), so no clipping is needed.
let private resistanceCircle (r: float) = circlePoints (r / (1.0 + r)) 0.0 (1.0 / (1.0 + r)) 400

// Constant-reactance arc for normalized reactance x: center (1, 1/x), radius 1/|x|.
// Always passes through Γ=(1,0); clipped to the part inside the unit disk.
let private reactanceArc (x: float) = clipToUnitDisk (circlePoints 1.0 (1.0 / x) (1.0 / abs x) 400)

let private smithGridColor = Color.fromString "#999999"

let private smithGridLine (pts: (float * float)[]) =
    Chart.Line(
        x = (pts |> Array.map fst),
        y = (pts |> Array.map snd),
        LineColor = smithGridColor,
        LineWidth = 1.0,
        ShowLegend = false
    )

let private smithGrid () =
    [ for r in [ 0.0; 0.2; 0.5; 1.0; 2.0; 5.0 ] -> smithGridLine (resistanceCircle r)
      for x in [ 0.2; 0.5; 1.0; 2.0; 5.0 ] do
          yield smithGridLine (reactanceArc x)
          yield smithGridLine (reactanceArc -x) ]

/// Reflection parameters selectable on the Smith chart: S11, S22.
let smithOrder = [ (1, 1); (2, 2) ]

let private smithTraces (label: string) (selected: (int * int) list) (data: TouchstoneFile) =
    selected
    |> List.filter (fun (i, j) -> i = j && i <= data.Ports)
    |> List.map (fun (i, _) ->
        let points =
            data.Matrices
            |> Array.map (fun m -> let g = m.[i, i] in (g.Real, g.Imaginary))
            |> lttb maxPointsPerTrace

        let name = sprintf "S%d%d" i i
        let name = if label = "" then name else sprintf "%s %s" label name
        styledLine label name (points |> Array.map fst) (points |> Array.map snd))

let private smithLayout (chart: GenericChart.GenericChart) =
    let axisRange = StyleParam.Range.MinMax(-1.15, 1.15)
    let xAxis =
        LinearAxis.init (
            Range = axisRange,
            ScaleAnchor = StyleParam.LinearAxisId.Y 1,
            ShowGrid = false,
            ZeroLine = false,
            Title = Title.init (Text = "Re(Γ)")
        )
    let yAxis =
        LinearAxis.init (Range = axisRange, ShowGrid = false, ZeroLine = false, Title = Title.init (Text = "Im(Γ)"))

    chart
    |> Chart.withXAxis xAxis
    |> Chart.withYAxis yAxis
    |> Chart.withTitle "Smith Chart"
    |> Chart.withSize (700, 700)

/// Smith chart of the input reflection coefficients Sii (S11, S22, ...) for an S-parameter file.
let smithChart (data: TouchstoneFile) =
    if data.Option.Parameter <> S then
        failwith "Smith chart requires S-parameter data."

    Chart.combine (smithGrid () @ smithTraces "" [ for i in 1 .. data.Ports -> (i, i) ] data) |> smithLayout

/// Smith chart overlaying the selected reflection coefficients (e.g.
/// `[ (1,1); (2,2) ]` for S11+S22) of several labeled S-parameter files
/// (e.g. filenames); non-S-parameter files are ignored. No min/max
/// annotations — a 2D trajectory has no single meaningful extremum. Returns
/// None if `selected` is empty or none of the files are S-parameter data.
let smithChartMulti (selected: (int * int) list) (files: (string * TouchstoneFile) list) =
    if selected.IsEmpty then
        None
    else
        let sFiles = files |> List.filter (fun (_, data) -> data.Option.Parameter = S)

        if sFiles.IsEmpty then
            None
        else
            let series =
                [ for (label, data) in sFiles do
                    for (i, j) in selected do
                        if i = j && i <= data.Ports then
                            let gammas = data.Matrices |> Array.map (fun m -> m.[i, i])
                            let name = (sprintf "%s S%d%d" label i i).Trim()
                            name, (gammas |> Array.map (fun g -> g.Real)), (gammas |> Array.map (fun g -> g.Imaginary)) ]

            let chart =
                sFiles
                |> List.collect (fun (label, data) -> smithTraces label selected data)
                |> (@) (smithGrid ())
                |> Chart.combine
                |> smithLayout

            Some { Chart = chart; Csv = fun () -> toCsv "Re(Γ)" "Im(Γ)" series }

/// Unwraps a sequence of angles (radians) so consecutive jumps greater than
/// π get folded by ±2π, producing a continuous curve. Raw S-parameter phase
/// wraps at ±180°; differentiating it unwrapped would produce spurious
/// spikes at every wrap instead of the actual group delay.
let private unwrap (radians: float[]) =
    let result = Array.copy radians
    let mutable offset = 0.0

    for i in 1 .. result.Length - 1 do
        let delta = radians.[i] - radians.[i - 1]

        if delta > Math.PI then
            offset <- offset - 2.0 * Math.PI
        elif delta < -Math.PI then
            offset <- offset + 2.0 * Math.PI

        result.[i] <- radians.[i] + offset

    result

/// Group delay in ns at every frequency point (Hz): -1/(2π) · dφ/df, with φ
/// unwrapped (radians). Central difference in the interior, one-sided at
/// the endpoints. Not downsampled — callers combine/derive from this first.
let private rawGroupDelay (i: int) (j: int) (data: TouchstoneFile) =
    let phases = data.Matrices |> Array.map (fun m -> m.[i, j].Phase) |> unwrap
    let n = phases.Length

    let delayNs =
        Array.init n (fun k ->
            if n < 2 then
                0.0
            else
                let lo, hi = max 0 (k - 1), min (n - 1) (k + 1)
                let dPhi = phases.[hi] - phases.[lo]
                let dF = data.Frequencies.[hi] - data.Frequencies.[lo]
                if dF = 0.0 then 0.0 else -1e9 * dPhi / (2.0 * Math.PI * dF))

    delayNs

/// Savitzky-Golay smoothing, window=7 / quadratic fit, fixed coefficients
/// (the standard published table, e.g. Numerical Recipes §14.9 — verified
/// against a pure quadratic input for exactness, a single-spike input for
/// correct spreading instead of a no-op, and a noisy linear trend for
/// actual noise reduction, before trusting them here). Group delay is a
/// numerical derivative of phase, which amplifies whatever measurement
/// noise is already in the raw phase data — this optionally smooths that
/// back down. The 3 points at each end don't have a full window and are
/// left as-is; negligible for the thousand-plus-point sweeps this is for.
let private savitzkyGolayCoeffs = [| -2.0; 3.0; 6.0; 7.0; 6.0; 3.0; -2.0 |]
let private savitzkyGolayNorm = Array.sum savitzkyGolayCoeffs

let private smoothed (ys: float[]) =
    let half = savitzkyGolayCoeffs.Length / 2

    Array.init ys.Length (fun k ->
        if k < half || k >= ys.Length - half then
            ys.[k]
        else
            let mutable acc = 0.0

            for m in 0 .. savitzkyGolayCoeffs.Length - 1 do
                acc <- acc + savitzkyGolayCoeffs.[m] * ys.[k - half + m]

            acc / savitzkyGolayNorm)

/// `smooth i j data` = rawGroupDelay, optionally Savitzky-Golay smoothed.
let private groupDelaySeries (smooth: bool) (i: int) (j: int) (data: TouchstoneFile) =
    let raw = rawGroupDelay i j data
    if smooth then smoothed raw else raw

let private groupDelayTrace (smooth: bool) (label: string) (i: int) (j: int) (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    let delayNs = groupDelaySeries smooth i j data
    let points = Array.zip freqGHz delayNs |> lttb maxPointsPerTrace
    let name = sprintf "%A%d%d" data.Option.Parameter i j
    let name = if label = "" then name else sprintf "%s %s" label name
    styledLine label name (points |> Array.map fst) (points |> Array.map snd)

/// Linear interpolation of the series (xs, ys) at x; xs must be sorted
/// ascending. Clamps to the nearest endpoint outside the series' range.
let private interpAt (xs: float[]) (ys: float[]) (x: float) =
    let n = xs.Length

    if n = 0 then
        nan
    elif n = 1 || x <= xs.[0] then
        ys.[0]
    elif x >= xs.[n - 1] then
        ys.[n - 1]
    else
        let mutable lo = 0
        let mutable hi = n - 1

        while hi - lo > 1 do
            let mid = (lo + hi) / 2
            if xs.[mid] <= x then lo <- mid else hi <- mid

        let x0, x1 = xs.[lo], xs.[hi]
        let y0, y1 = ys.[lo], ys.[hi]
        if x1 = x0 then y0 else y0 + (y1 - y0) * (x - x0) / (x1 - x0)

/// One (trace, (label, freqGHz, deviation)) pair per file of Sij's group
/// delay deviation (ns) from the pointwise mean across all of `files`. Files
/// on a different frequency grid than the first are linearly interpolated
/// onto it before averaging. Smoothing (if on) happens on each file's own
/// native grid, before interpolation — smooths the measurement itself
/// rather than an already-resampled version of it.
let private groupDelayDeviationSeriesAndTraces (smooth: bool) (i: int) (j: int) (files: (string * TouchstoneFile) list) =
    match files with
    | [] -> [], []
    | (_, refData) :: _ ->
        let refFreqs = refData.Frequencies
        let n = refFreqs.Length

        let interpolated =
            files
            |> List.map (fun (label, data) -> label, data, refFreqs |> Array.map (interpAt data.Frequencies (groupDelaySeries smooth i j data)))

        let mean =
            Array.init n (fun k -> (interpolated |> List.sumBy (fun (_, _, ys) -> ys.[k])) / float interpolated.Length)

        let freqGHz = refFreqs |> Array.map (fun f -> f / 1e9)

        let results =
            interpolated
            |> List.map (fun (label, _data, ys) ->
                let deviation = Array.init n (fun k -> ys.[k] - mean.[k])
                let name = (sprintf "%s S%d%d" label i j).Trim()
                let points = Array.zip freqGHz deviation |> lttb maxPointsPerTrace
                let trace = styledLine label name (points |> Array.map fst) (points |> Array.map snd)
                trace, (name, freqGHz, deviation))

        results |> List.map fst, results |> List.map snd

/// Transmission parameters selectable for group delay: S21, S12.
let groupDelayOrder = [ (2, 1); (1, 2) ]

/// Group delay (ns) of the selected transmission parameters (e.g.
/// `[ (2,1); (1,2) ]` for S21+S12, or the Y/Z/... equivalents) vs frequency
/// (GHz), overlaid across labeled 2-port files, optionally annotated with
/// the global min/max across all of them and optionally Savitzky-Golay
/// smoothed (group delay is a numerical derivative of phase, which
/// amplifies whatever measurement noise is already there). Returns None if
/// `selected` is empty or none of the files are 2-port.
let groupDelayChartMulti
    (showExtrema: bool)
    (smooth: bool)
    (selected: (int * int) list)
    (files: (string * TouchstoneFile) list)
    =
    if selected.IsEmpty then
        None
    else
        let files2p = files |> List.filter (fun (_, data) -> data.Ports = 2)

        if files2p.IsEmpty then
            None
        else
            let series =
                [ for (i, j) in selected do
                    for (label, data) in files2p ->
                        let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
                        (sprintf "%s S%d%d" label i j).Trim(), freqGHz, groupDelaySeries smooth i j data ]

            let shapes, annotations = if showExtrema then extremumMarkers "x" "y" "ns" series else [], []

            let chart =
                files2p
                |> List.collect (fun (label, data) -> selected |> List.map (fun (i, j) -> groupDelayTrace smooth label i j data))
                |> Chart.combine
                |> Chart.withTitle "Group Delay (ns)"
                |> Chart.withXAxisStyle "Frequency (GHz)"
                |> Chart.withYAxisStyle "Group Delay (ns)"
                |> Chart.withShapes shapes
                |> Chart.withAnnotations annotations

            Some { Chart = chart; Csv = fun () -> toCsv "Frequency (GHz)" "Group Delay (ns)" series }

/// Like groupDelayChartMulti, but each file's curve is its deviation (ns)
/// from the pointwise mean across all loaded files, instead of the absolute
/// delay — useful for spotting how much units differ from one another.
/// Files on different frequency grids are linearly interpolated onto the
/// first file's grid before averaging. Optionally annotated with the global
/// min/max deviation. Returns None if `selected` is empty or fewer than two
/// files are 2-port (a single file's deviation from itself is always zero).
let groupDelayDeviationChartMulti
    (showExtrema: bool)
    (smooth: bool)
    (selected: (int * int) list)
    (files: (string * TouchstoneFile) list)
    =
    if selected.IsEmpty then
        None
    else
        let files2p = files |> List.filter (fun (_, data) -> data.Ports = 2)

        if files2p.Length < 2 then
            None
        else
            let results = selected |> List.map (fun (i, j) -> groupDelayDeviationSeriesAndTraces smooth i j files2p)
            let traces = results |> List.collect fst
            let series = results |> List.collect snd
            let shapes, annotations = if showExtrema then extremumMarkers "x" "y" "ns" series else [], []

            let chart =
                traces
                |> Chart.combine
                |> Chart.withTitle "Group Delay Deviation from Mean (ns)"
                |> Chart.withXAxisStyle "Frequency (GHz)"
                |> Chart.withYAxisStyle "Δ Group Delay (ns)"
                |> Chart.withShapes shapes
                |> Chart.withAnnotations annotations

            Some { Chart = chart; Csv = fun () -> toCsv "Frequency (GHz)" "Δ Group Delay (ns)" series }

/// Iterative radix-2 Cooley-Tukey FFT; `input.Length` must be a power of 2.
/// `inverse = true` computes the inverse transform, normalized by 1/n.
let private fft (inverse: bool) (input: Complex[]) : Complex[] =
    let n = input.Length
    let a = Array.copy input
    let mutable j = 0

    for i in 0 .. n - 2 do
        if i < j then
            let tmp = a.[i]
            a.[i] <- a.[j]
            a.[j] <- tmp

        let mutable m = n >>> 1
        while m >= 1 && j >= m do
            j <- j - m
            m <- m >>> 1

        j <- j + m

    let sign = if inverse then 1.0 else -1.0
    let mutable len = 2

    while len <= n do
        let ang = sign * 2.0 * Math.PI / float len
        let wlen = Complex(cos ang, sin ang)
        let mutable i = 0

        while i < n do
            let mutable w = Complex.One

            for k in 0 .. len / 2 - 1 do
                let u = a.[i + k]
                let v = a.[i + k + len / 2] * w
                a.[i + k] <- u + v
                a.[i + k + len / 2] <- u - v
                w <- w * wlen

            i <- i + len

        len <- len <<< 1

    if inverse then a |> Array.map (fun c -> c / Complex(float n, 0.0)) else a

/// Modified Bessel function of the first kind, order 0 — the normalizing
/// term the Kaiser window is built from. Power series; 40 terms comfortably
/// converges for the beta values a window like this uses (single digits).
let private besselI0 (x: float) =
    let halfXSq = (x / 2.0) * (x / 2.0)
    let mutable term = 1.0
    let mutable sum = 1.0

    for k in 1 .. 40 do
        term <- term * halfXSq / (float k * float k)
        sum <- sum + term

    sum

/// Kaiser-window taper for tdrImpedance's one-sided spectrum: unity at k=0
/// (DC) and descending toward 0 as k -> m-1 (Nyquist/fMax), taken as the
/// outward-facing half of a symmetric Kaiser window centered at DC. β trades
/// ringing suppression near the TDR step against rise-time/edge sharpness —
/// higher β = quieter ringing but a softer, slower-rising step. Fixed at 6
/// (no UI control): a moderate choice, noticeably quieter than the raised-
/// cosine taper it replaces without visibly blunting the step.
let private kaiserBeta = 6.0
let private kaiserI0Beta = besselI0 kaiserBeta

let private kaiserTaper (m: int) (k: int) =
    let ratio = float k / float (m - 1)
    besselI0 (kaiserBeta * sqrt (max 0.0 (1.0 - ratio * ratio))) / kaiserI0Beta

/// Fixed FFT length for the whole TDR pipeline (impedance view and gating
/// alike): the time resolution this yields, dt = 1/(2·fMax), is
/// algebraically independent of this constant (see tdrFullSpanNs) — a
/// bigger value only extends how far out in time the result goes before
/// wrapping around, not how sharp a step edge can look.
let private tdrNFft = 4096

/// A file's reflection-coefficient spectrum resampled onto the pipeline's
/// own uniform 0..fMax grid (spacing df, m = nFft/2+1 bins), flat-
/// extrapolated below the lowest measured frequency. Shared by tdrImpedance
/// (which windows this before inverse-FFTing, for a smooth displayed step
/// response) and tdrGatedResponse (which deliberately doesn't — see there).
let private tdrOneSided (freqsHz: float[]) (gamma: Complex[]) =
    let fMin, fMax = freqsHz.[0], freqsHz.[freqsHz.Length - 1]
    let dfMeasured = (fMax - fMin) / float (freqsHz.Length - 1)
    let m = tdrNFft / 2 + 1
    let df = fMax / float (m - 1)

    let oneSided =
        Array.init m (fun k ->
            let f = float k * df
            if f < fMin then
                gamma.[0]
            else
                let idxF = (f - fMin) / dfMeasured
                let lo = int (floor idxF) |> max 0 |> min (freqsHz.Length - 1)
                let hi = min (lo + 1) (freqsHz.Length - 1)
                let frac = idxF - float lo
                if lo = hi then gamma.[lo] else gamma.[lo] * Complex(1.0 - frac, 0.0) + gamma.[hi] * Complex(frac, 0.0))

    oneSided, df, m

/// Real impulse response from a one-sided (0..fMax) spectrum: applies
/// `taper` to each bin, builds the Hermitian-symmetric full nFft spectrum,
/// and inverse-FFTs it. `taper = fun _ g -> g` for no windowing at all.
let private tdrImpulse (taper: int -> Complex -> Complex) (oneSided: Complex[]) =
    let m = oneSided.Length
    let windowed = oneSided |> Array.mapi taper

    let full = Array.zeroCreate<Complex> tdrNFft
    full.[0] <- Complex(windowed.[0].Real, 0.0)
    for k in 1 .. m - 2 do
        full.[k] <- windowed.[k]
        full.[tdrNFft - k] <- Complex.Conjugate windowed.[k]
    full.[m - 1] <- Complex(windowed.[m - 1].Real, 0.0)

    fft true full |> Array.map (fun c -> c.Real)

/// Time (ns) and impedance (Ω) from a reflection-coefficient spectrum
/// `gamma` sampled at `freqsHz` (ascending, need not be uniform). Classic
/// "lowpass equivalent" TDR: resamples onto a uniform grid (tdrOneSided),
/// windows it, inverse-FFTs to an impulse response (tdrImpulse),
/// cumulative-sums to a step response, and converts reflection coefficient
/// to impedance via Z(t) = Z0 · (1+ρ(t)) / (1-ρ(t)).
///
/// The window is unity at DC and only tapers toward Nyquist/fMax — a plain
/// symmetric Hann window (zero at *both* ends) was tried first and zeroed
/// out the DC bin, which made every step response decay back to 0 right
/// after the transient instead of holding at its true plateau: the hard
/// cutoff at fMax is what causes ringing, not DC, so only that end needs
/// tapering. See kaiserTaper for the taper shape itself.
let private tdrImpedance (z0: float) (freqsHz: float[]) (gamma: Complex[]) =
    let oneSided, df, m = tdrOneSided freqsHz gamma
    let impulse = oneSided |> tdrImpulse (fun k g -> g * Complex(kaiserTaper m k, 0.0))
    let dt = 1.0 / (float tdrNFft * df)

    let step = Array.zeroCreate<float> tdrNFft
    let mutable acc = 0.0
    for i in 0 .. tdrNFft - 1 do
        acc <- acc + impulse.[i]
        step.[i] <- acc

    // Only the first half of the (real, time-domain) result is meaningful
    // resolution-wise; the rest mirrors it per the DFT's implicit periodicity.
    let half = tdrNFft / 2
    let timeNs = Array.init half (fun i -> float i * dt * 1e9)
    let impedance = step.[0 .. half - 1] |> Array.map (fun rho -> z0 * (1.0 + rho) / (1.0 - rho))
    timeNs, impedance

/// Native full causal time span (ns) of a file's TDR view — the same
/// `dt = 1/(2·fMax)` derivation tdrImpedance uses, times the number of
/// meaningful (first-half) samples. Exposed for the web UI to bound its
/// time-gate slider; the full span is also that slider's "no gating" default.
let tdrFullSpanNs (data: TouchstoneFile) =
    let fMax = data.Frequencies.[data.Frequencies.Length - 1]
    let half = tdrNFft / 2
    float (half - 1) / (2.0 * fMax) * 1e9

/// Raised-cosine gate over an impulse response's causal (first) half: unity
/// inside [loNs, hiNs], tapering to 0 over `max(10% of the gate width, 3
/// samples)` ns at each edge — a hard rectangular cutoff would reintroduce
/// the same ringing windowing exists to avoid elsewhere in this module. 0
/// everywhere past the causal half (k >= half): that's the numerically
/// negligible "wrapped negative time" tail tdrImpedance already discards
/// for the same reason.
let private gateMask (nFft: int) (dt: float) (loNs: float) (hiNs: float) (k: int) =
    let half = nFft / 2

    if k >= half then
        0.0
    else
        let tNs = float k * dt * 1e9
        let taper = max ((hiNs - loNs) * 0.1) (dt * 1e9 * 3.0)

        if tNs < loNs - taper || tNs > hiNs + taper then 0.0
        elif tNs < loNs then 0.5 * (1.0 + cos (Math.PI * (loNs - tNs) / taper))
        elif tNs > hiNs then 0.5 * (1.0 + cos (Math.PI * (tNs - hiNs) / taper))
        else 1.0

/// Gates a file's TDR impulse response to [loNs, hiNs] ns (None = the full
/// causal record — a useful sanity check too, since it closely reproduces
/// the original Γ(f)) and forward-FFTs it back to a frequency response, for
/// isolating one reflection/discontinuity from others sharing the same
/// line. Deliberately builds its own *unwindowed* impulse response rather
/// than reusing tdrImpedance's: the Kaiser taper there exists to smooth the
/// *displayed step response* and, tried here first, baked its own high-
/// frequency roll-off straight into this reconstruction — caught by a
/// round-trip test with no gate applied (should reproduce the original
/// spectrum almost exactly; it didn't, until the window was dropped from
/// this path). Frequencies returned are the pipeline's own uniform grid
/// (0..fMax, spacing df), not the original measurement's.
let private tdrGatedResponse (gateNs: (float * float) option) (freqsHz: float[]) (gamma: Complex[]) =
    let oneSided, df, m = tdrOneSided freqsHz gamma
    let impulse = oneSided |> tdrImpulse (fun _ g -> g)
    let dt = 1.0 / (float tdrNFft * df)
    let half = tdrNFft / 2
    let loNs, hiNs = defaultArg gateNs (0.0, float (half - 1) * dt * 1e9)

    let gated = impulse |> Array.mapi (fun k v -> Complex(v * gateMask tdrNFft dt loNs hiNs k, 0.0))
    let spectrum = fft false gated
    let freqsOutHz = Array.init m (fun k -> float k * df)
    freqsOutHz, spectrum.[0 .. m - 1]

/// Reflection parameters selectable for TDR: S11, S22 (same pairing as the Smith chart).
let tdrOrder = [ (1, 1); (2, 2) ]

/// Vertical dashed guide lines marking a time gate's bounds on the TDR
/// Impedance chart, spanning the full plot height (Yref "paper") regardless
/// of the impedance axis' own range. `Editable = true` lets Plotly's own
/// shape-drag handling move a line when the pointer grabs it directly,
/// without taking over the rest of the plot area's normal box-zoom drag —
/// interop.js listens for the resulting `plotly_relayout` event and feeds
/// the new position back into SetTdrGate. Always emitted in [lo; hi] order,
/// so the web UI can identify which shape is which by position alone.
/// Empty when `gateNs` is None.
///
/// X0/X1 are offset from `x` by a tiny, visually-imperceptible epsilon
/// rather than both exactly `x`: a perfectly vertical (or horizontal) line
/// shape hits a known Plotly.js hit-detection bug where editable dragging
/// never actually engages — the cursor never changes and every click falls
/// through to the plot's own zoom — regardless of `editable` or
/// `config.edits.shapePosition` (surfaces identically through the R
/// wrapper, see ropensci/plotly#1532; same underlying plotly.js drag code).
/// A slight slant sidesteps it entirely. interop.js reads the midpoint of
/// x0/x1 back out, so the epsilon never shows up in the reported gate value.
///
/// Distinct colors per line (not both the same grey) so "lo" and "hi" read
/// apart at a glance — helpful since interop.js also clamps each to never
/// cross the other while dragging, which would otherwise make it unclear
/// which line is which once they're close together.
let private gateLoColor = "#17a2b8"
let private gateHiColor = "#c2185b"

let private gateBoundaryShapes (gateNs: (float * float) option) =
    match gateNs with
    | None -> []
    | Some(lo, hi) ->
        let epsilon = 1e-6

        let vline (color: string) x =
            Shape.init (
                ShapeType = StyleParam.ShapeType.Line,
                X0 = x - epsilon,
                X1 = x + epsilon,
                Y0 = 0.0,
                Y1 = 1.0,
                Xref = "x",
                Yref = "paper",
                Editable = true,
                Line = Line.init (Color = Color.fromString color, Dash = StyleParam.DrawingStyle.Dot, Width = 1.5)
            )

        [ vline gateLoColor lo; vline gateHiColor hi ]

/// Not lttb-downsampled unlike the other *Trace helpers: the native point
/// count here is fixed at half of tdrNFft (2048), not the thousands-of-
/// points a real VNA sweep can have, so it's well within Plotly's comfort
/// zone already. lttb picks points by global significance (e.g. the step
/// edge), which looks jagged once zoomed into a region it didn't optimize
/// for — this chart is exactly the one people zoom into.
let private tdrSeriesAndTrace (label: string) (i: int) (data: TouchstoneFile) =
    let gamma = data.Matrices |> Array.map (fun m -> m.[i, i])
    let timeNs, impedance = tdrImpedance data.Option.R data.Frequencies gamma
    let name = (sprintf "%s S%d%d" label i i).Trim()
    let trace = styledLine label name timeNs impedance
    trace, (name, timeNs, impedance)

/// Time-domain impedance (Ω) from the selected reflection coefficients (e.g.
/// `[ (1,1); (2,2) ]` for S11+S22) of several labeled S-parameter files, each
/// converted independently via inverse FFT of its own Γ(f) — see
/// tdrImpedance. `gateNs`, if set, is drawn as a pair of vertical guide
/// lines (see gateBoundaryShapes) rather than applied to the plotted curve
/// itself — the actual gating happens in the frequency-domain reconstruction
/// (tdrGatedChartMulti); this is just a visual aid for choosing its bounds.
/// Non-S-parameter files are ignored. Returns None if `selected` is empty or
/// none of the files are S-parameter data.
let tdrChartMulti
    (showExtrema: bool)
    (gateNs: (float * float) option)
    (selected: (int * int) list)
    (files: (string * TouchstoneFile) list)
    =
    if selected.IsEmpty then
        None
    else
        let sFiles = files |> List.filter (fun (_, data) -> data.Option.Parameter = S)

        if sFiles.IsEmpty then
            None
        else
            let results =
                [ for (label, data) in sFiles do
                    for (i, j) in selected do
                        if i = j && i <= data.Ports then
                            tdrSeriesAndTrace label i data ]

            let traces = results |> List.map fst
            let series = results |> List.map snd
            let extremaShapes, annotations = if showExtrema then extremumMarkers "x" "y" "Ω" series else [], []
            let shapes = extremaShapes @ gateBoundaryShapes gateNs

            let chart =
                traces
                |> Chart.combine
                |> Chart.withTitle "TDR Impedance (Ω)"
                |> Chart.withXAxisStyle "Time (ns)"
                |> Chart.withYAxisStyle "Impedance (Ω)"
                |> Chart.withShapes shapes
                |> Chart.withAnnotations annotations

            Some { Chart = chart; Csv = fun () -> toCsv "Time (ns)" "Impedance (Ω)" series }

let private tdrGatedTrace (label: string) (gateNs: (float * float) option) (i: int) (data: TouchstoneFile) =
    let gamma = data.Matrices |> Array.map (fun m -> m.[i, i])
    let freqsOutHz, gammaOut = tdrGatedResponse gateNs data.Frequencies gamma
    let freqGHz = freqsOutHz |> Array.map (fun f -> f / 1e9)
    let db = gammaOut |> Array.map toDb
    let name = (sprintf "%s S%d%d" label i i).Trim()
    let trace = styledLine label name freqGHz db
    trace, (name, freqGHz, db)

/// Magnitude (dB) of the gated (see tdrGatedResponse) reflection
/// coefficients for the selected S11/S22 of several labeled S-parameter
/// files — the frequency-domain counterpart of the TDR Impedance section's
/// time gate: isolates whatever's inside [loNs, hiNs] (None = ungated, the
/// full record) from other reflections/discontinuities sharing the same
/// line. Non-S-parameter files are ignored. Returns None if `selected` is
/// empty or none of the files are S-parameter data.
let tdrGatedChartMulti
    (gateNs: (float * float) option)
    (selected: (int * int) list)
    (files: (string * TouchstoneFile) list)
    =
    if selected.IsEmpty then
        None
    else
        let sFiles = files |> List.filter (fun (_, data) -> data.Option.Parameter = S)

        if sFiles.IsEmpty then
            None
        else
            let results =
                [ for (label, data) in sFiles do
                    for (i, j) in selected do
                        if i = j && i <= data.Ports then
                            tdrGatedTrace label gateNs i data ]

            let traces = results |> List.map fst
            let series = results |> List.map snd

            let chart =
                traces
                |> Chart.combine
                |> Chart.withTitle "TDR Gated Magnitude (dB)"
                |> Chart.withXAxisStyle "Frequency (GHz)"
                |> Chart.withYAxisStyle "Magnitude (dB)"

            Some { Chart = chart; Csv = fun () -> toCsv "Frequency (GHz)" "Magnitude (dB)" series }

/// Opens magnitude + phase overlay charts (and, for S-parameters, a Smith chart) in the default browser.
let show (data: TouchstoneFile) =
    magnitudeChart data |> Chart.show
    phaseChart data |> Chart.show
    if data.Option.Parameter = S then
        smithChart data |> Chart.show
