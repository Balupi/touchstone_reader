/// Parser for Touchstone RF network-parameter files (.s1p, .s2p, .s3p, .s4p, .sNp),
/// supporting both legacy (v1.0/1.1) and v2.0 (keyword-based) formats.
module TouchstoneReader.Touchstone

open System
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
      Matrices: Complex[,][] }  // one (ports+1)x(ports+1) matrix per frequency, 1-indexed

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

/// Reads any Touchstone file (v1.x legacy or v2.0 keyword-based).
/// v2.0 support covers Version / Number of Ports / Reference / Matrix Format /
/// Two-Port Data Order / Network Data / End. Noise-data blocks are skipped.
let read (path: string) : TouchstoneFile =
    let lines =
        File.ReadAllLines path
        |> Array.map stripComment
        |> Array.map (fun l -> l.Trim())
        |> Array.filter (fun l -> l.Length > 0)

    let isV2 =
        lines |> Array.exists (fun l -> l.StartsWith("[Version]", StringComparison.OrdinalIgnoreCase))

    let mutable ports = if isV2 then 0 else portsFromExtension path
    let mutable opt = OptionLine.Default
    let mutable references: float list = []
    let mutable matrixFmt = Full
    let mutable twoPortOrder = Order21_12
    let mutable inNetworkData = not isV2   // legacy files: everything non-# is data
    let mutable inNoiseData = false
    let networkTokens = ResizeArray<string>()

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
            if inNetworkData then
                networkTokens.AddRange(line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries))
            // noise-data rows are intentionally skipped

    if ports <= 0 then failwith "Could not determine number of ports (missing [Number of Ports] or bad filename)."

    let order = entryOrder ports matrixFmt twoPortOrder
    let valuesPerFreq = 1 + order.Length * 2
    let allValues = networkTokens |> Seq.map float |> Seq.toArray

    if allValues.Length % valuesPerFreq <> 0 then
        eprintfn "Warning: token count (%d) isn't a multiple of expected row size (%d); trailing data may be dropped."
            allValues.Length valuesPerFreq

    let numFreqs = allValues.Length / valuesPerFreq
    let freqs = Array.zeroCreate<float> numFreqs
    let matrices = Array.zeroCreate<Complex[,]> numFreqs

    for f in 0 .. numFreqs - 1 do
        let baseIdx = f * valuesPerFreq
        freqs.[f] <- allValues.[baseIdx] * freqMultiplier opt.FreqUnit
        let m = Array2D.create (ports + 1) (ports + 1) Complex.Zero // 1-indexed
        order |> List.iteri (fun k (r, c) ->
            let a = allValues.[baseIdx + 1 + 2 * k]
            let b = allValues.[baseIdx + 1 + 2 * k + 1]
            m.[r, c] <- toComplex opt.Format a b)
        matrices.[f] <- m

    { Ports = ports; Option = opt; References = references; Frequencies = freqs; Matrices = matrices }
