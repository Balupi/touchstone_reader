/// Parser for Touchstone RF network-parameter files (.s1p, .s2p, .s3p, .s4p, .sNp),
/// supporting both legacy (v1.0/1.1) and v2.0 (keyword-based) formats.
module TouchstoneReader.Touchstone

open System
open System.Globalization
open System.IO
open System.Numerics

type FreqUnit = Hz | KHz | MHz | GHz
type Parameter = S | Y | Z | H | G
type Format = DB | MA | RI
type MatrixFormat = Full | Lower | Upper
type TwoPortOrder = Order21_12 | Order12_21

type OptionLine =
    { FreqUnit: FreqUnit
      Parameter: Parameter
      Format: Format
      R: float }
    static member Default =
        { FreqUnit = GHz; Parameter = S; Format = MA; R = 50.0 }

type TouchstoneFile =
    { Ports: int
      Option: OptionLine
      References: float list
      Frequencies: float[]      // Hz
      /// One array per network parameter, each holding that parameter's value
      /// at every frequency: Sij lives in `Entries.[(i-1) * Ports + (j-1)]`,
      /// or just `entry data i j`. Stored this way rather than as one matrix
      /// per frequency because every consumer here reads a single parameter
      /// across the whole sweep — a trace, a CSV series, an FFT input — and
      /// none needs a whole matrix at one frequency. Each read is then one
      /// contiguous array instead of a walk across thousands of tiny 2D
      /// arrays, and the parser stops allocating one of those per point.
      Entries: Complex[][]
      Comments: string list }   // '!' lines before the first non-comment line, e.g. instrument/date info

/// One parameter across the whole sweep: Sij (or Yij/Zij/...) at every
/// frequency. 1-indexed, like the file format itself.
let entry (data: TouchstoneFile) (i: int) (j: int) =
    data.Entries.[(i - 1) * data.Ports + (j - 1)]

let private freqMultiplier = function
    | Hz -> 1.0 | KHz -> 1e3 | MHz -> 1e6 | GHz -> 1e9

let private parseFreqUnit (s: string) =
    match s.ToUpperInvariant() with
    | "HZ" -> Hz | "KHZ" -> KHz | "MHZ" -> MHz | "GHZ" -> GHz
    | _ -> failwithf "Unknown frequency unit: %s" s

let private parseParameter (s: string) =
    match s.ToUpperInvariant() with
    | "S" -> S | "Y" -> Y | "Z" -> Z | "H" -> H | "G" -> G
    | _ -> failwithf "Unknown parameter: %s" s

let private parseFormat (s: string) =
    match s.ToUpperInvariant() with
    | "DB" -> DB | "MA" -> MA | "RI" -> RI
    | _ -> failwithf "Unknown format: %s" s

let private stripComment (line: string) =
    let idx = line.IndexOf '!'
    if idx >= 0 then line.Substring(0, idx) else line

let private portsFromExtension (path: string) =
    let ext = Path.GetExtension(path).ToLowerInvariant() // ".s2p"
    let m = Text.RegularExpressions.Regex.Match(ext, @"^\.s(\d+)p$")
    if m.Success then int m.Groups.[1].Value
    else failwithf "Cannot determine port count from filename '%s' (expected .sNp)." path

let private toComplex (format: Format) (a: float) (b: float) =
    match format with
    | RI -> Complex(a, b)
    | MA -> Complex.FromPolarCoordinates(a, b * Math.PI / 180.0)
    | DB ->
        let mag = 10.0 ** (a / 20.0)
        Complex.FromPolarCoordinates(mag, b * Math.PI / 180.0)

/// Order of (row,col) pairs in which value-pairs appear for one frequency point.
let private entryOrder (ports: int) (matrixFmt: MatrixFormat) (twoPortOrder: TwoPortOrder) =
    match ports with
    | 1 -> [ (1, 1) ]
    | 2 ->
        match twoPortOrder with
        | Order21_12 -> [ (1, 1); (2, 1); (1, 2); (2, 2) ] // legacy default
        | Order12_21 -> [ (1, 1); (1, 2); (2, 1); (2, 2) ]
    | n ->
        match matrixFmt with
        | Full  -> [ for i in 1 .. n do for j in 1 .. n -> (i, j) ]
        | Lower -> [ for i in 1 .. n do for j in 1 .. i -> (i, j) ]
        | Upper -> [ for i in 1 .. n do for j in i .. n -> (i, j) ]

/// Parses Touchstone file content already in memory (v1.x legacy or v2.0
/// keyword-based). `fileName` is only used to infer the port count from a
/// legacy .sNp extension. v2.0 support covers Version / Number of Ports /
/// Reference / Matrix Format / Two-Port Data Order / Network Data / End.
/// Noise-data blocks are skipped.
let parse (fileName: string) (content: string) : TouchstoneFile =
    let rawLines = content.Replace("\r\n", "\n").Split('\n')

    // Touchstone has no dedicated metadata section, but by convention the
    // instrument/calibration/date info (if any) lives in '!' comment lines
    // before the file settles into its option/data lines — so that's the
    // block worth surfacing. Comments elsewhere (inline on a data line, or
    // interspersed later) are just noise and stay discarded via stripComment.
    let comments =
        rawLines
        |> Array.map (fun l -> l.Trim())
        |> Array.takeWhile (fun l -> l = "" || l.StartsWith "!")
        |> Array.filter (fun l -> l.StartsWith "!")
        |> Array.map (fun l -> l.TrimStart('!').Trim())
        |> Array.toList

    // One pass rather than three: on an 11,000-point sweep each extra pass is
    // another 11,000-element array built only to be thrown away. stripComment
    // and Trim both hand back the original instance when there's nothing to
    // cut, so a well-formed data line still isn't copied here.
    let lines =
        rawLines
        |> Array.choose (fun raw ->
            let line = (stripComment raw).Trim()
            if line.Length = 0 then None else Some line)

    let isV2 =
        lines |> Array.exists (fun l -> l.StartsWith("[Version]", StringComparison.OrdinalIgnoreCase))

    let mutable ports = if isV2 then 0 else portsFromExtension fileName
    let mutable opt = OptionLine.Default
    let mutable references: float list = []
    let mutable matrixFmt = Full
    let mutable twoPortOrder = Order21_12
    let mutable inNetworkData = not isV2   // legacy files: everything non-# is data
    let mutable inNoiseData = false

    /// Every number in the network-data rows, in file order. Parsed straight
    /// out of the line instead of being collected as strings first: an
    /// 11,000-point 2-port sweep is 99,000 tokens, and turning each into its
    /// own string — then walking them all again through a Seq — cost more
    /// than the rest of this function put together.
    let networkValues = ResizeArray<float>(8192)

    let addNumbers (line: string) =
        let mutable pos = 0

        while pos < line.Length do
            while pos < line.Length && (line.[pos] = ' ' || line.[pos] = '\t') do
                pos <- pos + 1

            let start = pos

            while pos < line.Length && line.[pos] <> ' ' && line.[pos] <> '\t' do
                pos <- pos + 1

            if pos > start then
                networkValues.Add(
                    Double.Parse(line.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture)
                )

    for line in lines do
        if line.StartsWith("#") then
            let tokens = line.Substring(1).Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            let mutable i = 0
            let mutable freqUnit = opt.FreqUnit
            let mutable param = opt.Parameter
            let mutable fmt = opt.Format
            let mutable r = opt.R
            while i < tokens.Length do
                let t = tokens.[i].ToUpperInvariant()
                match t with
                | "HZ" | "KHZ" | "MHZ" | "GHZ" -> freqUnit <- parseFreqUnit t; i <- i + 1
                | "S" | "Y" | "Z" | "H" | "G" -> param <- parseParameter t; i <- i + 1
                | "DB" | "MA" | "RI" -> fmt <- parseFormat t; i <- i + 1
                | "R" -> r <- float tokens.[i + 1]; i <- i + 2
                | _ -> i <- i + 1
            opt <- { FreqUnit = freqUnit; Parameter = param; Format = fmt; R = r }
        elif line.StartsWith("[") then
            let close = line.IndexOf ']'
            let key = line.Substring(1, close - 1).Trim().ToLowerInvariant()
            let rest = line.Substring(close + 1).Trim()
            match key with
            | "version" -> ()
            | "number of ports" -> ports <- int rest
            | "reference" ->
                references <-
                    rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map float |> Array.toList
            | "matrix format" ->
                matrixFmt <-
                    match rest.ToLowerInvariant() with
                    | "lower" -> Lower | "upper" -> Upper | _ -> Full
            | "two-port data order" ->
                twoPortOrder <- if rest.Trim() = "12_21" then Order12_21 else Order21_12
            | "network data" -> inNetworkData <- true; inNoiseData <- false
            | "noise data" -> inNoiseData <- true; inNetworkData <- false
            | "end" -> inNetworkData <- false; inNoiseData <- false
            | _ -> () // Number of Frequencies, Number of Noise Frequencies, etc. -> ignored
        else
            if inNetworkData then addNumbers line
            // noise-data rows are intentionally skipped

    if ports <= 0 then failwith "Could not determine number of ports (missing [Number of Ports] or bad filename)."

    let order = entryOrder ports matrixFmt twoPortOrder |> List.toArray
    let valuesPerFreq = 1 + order.Length * 2

    if networkValues.Count % valuesPerFreq <> 0 then
        eprintfn "Warning: token count (%d) isn't a multiple of expected row size (%d); trailing data may be dropped."
            networkValues.Count valuesPerFreq

    let numFreqs = networkValues.Count / valuesPerFreq
    let freqs = Array.zeroCreate<float> numFreqs
    // Entries a file doesn't carry — a Lower/Upper matrix format leaves half
    // of them out — stay Complex.Zero, exactly as the per-frequency matrices
    // did, since those were created filled.
    let entries = Array.init (ports * ports) (fun _ -> Array.zeroCreate<Complex> numFreqs)
    let multiplier = freqMultiplier opt.FreqUnit

    for f in 0 .. numFreqs - 1 do
        let baseIdx = f * valuesPerFreq
        freqs.[f] <- networkValues.[baseIdx] * multiplier

        for k in 0 .. order.Length - 1 do
            let r, c = order.[k]
            let a = networkValues.[baseIdx + 1 + 2 * k]
            let b = networkValues.[baseIdx + 2 + 2 * k]
            entries.[(r - 1) * ports + (c - 1)].[f] <- toComplex opt.Format a b

    { Ports = ports
      Option = opt
      References = references
      Frequencies = freqs
      Entries = entries
      Comments = comments }

/// Reads and parses a Touchstone file from disk.
let read (path: string) : TouchstoneFile = parse path (File.ReadAllText path)

/// A copy of `data` restricted to the points whose frequency (Hz) falls
/// within [loHz, hiHz] inclusive. Used by the web UI's per-file frequency
/// range slider — charts and CSV export just see a smaller file, so no
/// chart-building code needs to know about range selection at all.
///
/// Assumes ascending frequencies, which Touchstone sweeps are and which the
/// rest of this codebase already relies on anyway (group delay divides by
/// `f[k] - f[k-1]`, lttb assumes x is ordered, the range slider takes its
/// bounds from the first and last point). The kept points are then one
/// contiguous run, so this finds its two ends and slices, instead of
/// testing every point and copying them one by one. Worth it because the
/// web UI re-windows every loaded file several times per render: measured
/// 1.54 ms -> 0.13 ms per call on an 11,000-point file, where the previous
/// `Array.indexed |> filter |> map` allocated a tuple per point plus three
/// intermediate arrays.
let windowed (loHz: float) (hiHz: float) (data: TouchstoneFile) =
    let freqs = data.Frequencies
    let mutable lo = 0

    while lo < freqs.Length && freqs.[lo] < loHz do
        lo <- lo + 1

    let mutable hi = freqs.Length - 1

    while hi >= lo && freqs.[hi] > hiHz do
        hi <- hi - 1

    // hi < lo means nothing fell in range; the loops leave hi = lo - 1 at
    // worst, so the length stays non-negative without a clamp.
    let len = hi - lo + 1

    if len = freqs.Length then
        // The range covers the whole sweep, which is what a slider dragged
        // back to its ends looks like. Copying every parameter array to say
        // "all of it" is the one case where this function can be free, and
        // since the per-parameter arrays hold values rather than references
        // now, that copy is real work: four arrays of Complex instead of one
        // of pointers.
        data
    else
        { data with
            Frequencies = Array.sub freqs lo len
            Entries = data.Entries |> Array.map (fun series -> Array.sub series lo len) }
