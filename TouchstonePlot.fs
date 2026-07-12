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

/// One line trace for Sij (or Yij/Zij/...) of one file, `toY` picking the
/// scalar to plot from each complex value. `label` (e.g. a filename) is
/// prepended to the trace name when overlaying multiple files; pass "" for none.
let private oneParamTrace (label: string) (toY: Complex -> float) (i: int) (j: int) (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    let ys = data.Matrices |> Array.map (fun m -> toY m.[i, j])
    let points = Array.zip freqGHz ys |> lttb maxPointsPerTrace
    let name = sprintf "%A%d%d" data.Option.Parameter i j
    let name = if label = "" then name else sprintf "%s %s" label name
    Chart.Line(x = (points |> Array.map fst), y = (points |> Array.map snd), Name = name)

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
            |> Chart.withTitle (sprintf "%A%d%d" data.Option.Parameter i j) ]
    |> Chart.Grid(n, n)
    |> Chart.withSize (350 * n, 300 * n)

/// Conventional VNA quad order: S11 top-left, S21 top-right, S12
/// bottom-left, S22 bottom-right.
let magnitudeQuadOrder = [ (1, 1); (2, 1); (1, 2); (2, 2) ]

/// Grid of magnitude (dB) subplots for 2-port S-parameter files, one per
/// selected (i,j) parameter (e.g. `[ (1,1); (2,1) ]` for S11+S21), laid out
/// left-to-right top-to-bottom in up to 2 columns. Each subplot overlays
/// every labeled file's trace for that parameter; files that aren't 2-port
/// are skipped. Returns None if `selected` is empty.
let magnitudeQuadMulti (selected: (int * int) list) (files: (string * TouchstoneFile) list) =
    if selected.IsEmpty then
        None
    else
        let files2p = files |> List.filter (fun (_, data) -> data.Ports = 2)

        // Chart.Grid collapses a per-subplot Chart.withTitle into one shared
        // title (only the last one wins), but each subplot keeps its own
        // axes — so the per-cell label goes on the Y axis instead.
        let subplot (i, j) =
            files2p
            |> List.map (fun (label, data) -> oneParamTrace label toDb i j data)
            |> Chart.combine
            |> Chart.withYAxisStyle (sprintf "S%d%d" i j)

        let cols = min 2 selected.Length
        let rows = (selected.Length + cols - 1) / cols

        selected
        |> List.map subplot
        |> Chart.Grid(rows, cols)
        |> Chart.withTitle "Magnitude (dB)"
        |> Chart.withSize (450 * cols, 350 * rows)
        |> Some

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

let private smithTraces (label: string) (data: TouchstoneFile) =
    [ for i in 1 .. data.Ports ->
        let points =
            data.Matrices
            |> Array.map (fun m -> let g = m.[i, i] in (g.Real, g.Imaginary))
            |> lttb maxPointsPerTrace

        let name = sprintf "S%d%d" i i
        let name = if label = "" then name else sprintf "%s %s" label name
        Chart.Line(x = (points |> Array.map fst), y = (points |> Array.map snd), Name = name) ]

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

    Chart.combine (smithGrid () @ smithTraces "" data) |> smithLayout

/// Smith chart overlaying the input reflection coefficients of several labeled
/// S-parameter files (e.g. filenames); non-S-parameter files are ignored.
/// Returns None if none of the files are S-parameter data.
let smithChartMulti (files: (string * TouchstoneFile) list) =
    let sFiles = files |> List.filter (fun (_, data) -> data.Option.Parameter = S)

    if sFiles.IsEmpty then
        None
    else
        sFiles
        |> List.collect (fun (label, data) -> smithTraces label data)
        |> (@) (smithGrid ())
        |> Chart.combine
        |> smithLayout
        |> Some

/// Opens magnitude + phase overlay charts (and, for S-parameters, a Smith chart) in the default browser.
let show (data: TouchstoneFile) =
    magnitudeChart data |> Chart.show
    phaseChart data |> Chart.show
    if data.Option.Parameter = S then
        smithChart data |> Chart.show
