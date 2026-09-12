/// Elmish model, messages, and update function for the Touchstone web app.
module TouchstoneReader.Web.State

open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot

type LoadedFile =
    { FileName: string
      Data: Result<TouchstoneFile, string>
      /// GHz sub-range to display for this file; None means the full sweep
      /// (its first/last frequency point), which is also the default.
      FreqRangeGHz: (float * float) option
      /// Bumped on every SetFreqRange/ResetFreqRange touching this file.
      /// View.fs keys the frequency-bound number inputs on it, forcing
      /// Blazor to reset their displayed value even when a typed value
      /// snaps to the number already shown — its normal diffing skips that,
      /// since from its perspective the rendered value didn't change even
      /// though the live DOM (what the user actually typed) did.
      FreqRangeGen: int
      /// Whether this file's frequency-range slider is part of the linked
      /// group: editing any linked file's slider applies the same GHz
      /// bounds to every other linked file. Unchecking it (in that file's
      /// own Details section, next to its slider) takes just that one file
      /// out of the group without affecting the rest. Default true, so
      /// multiple files start out synced.
      FreqRangeLinked: bool }

/// Which collapsible section a ToggleParam message applies to.
type ChartKind =
    | MagnitudeChart
    | PhaseChart
    | SmithChart
    | GroupDelayChart
    | TdrChart
    | VswrChart

/// Show each file's absolute curve, or (only meaningful with 2+ files) its
/// deviation from the pointwise mean across the loaded files.
type DisplayMode =
    | Absolute
    | DeviationFromMean

type Model =
    { Files: LoadedFile list
      MagnitudeSelected: Set<int * int>
      PhaseSelected: Set<int * int>
      SmithSelected: Set<int * int>
      GroupDelaySelected: Set<int * int>
      TdrSelected: Set<int * int>
      GroupDelayMode: DisplayMode
      ShowMagnitudeExtrema: bool
      ShowGroupDelayExtrema: bool
      ShowTdrExtrema: bool
      /// VSWR (nested under the Smith Chart section) shares SmithSelected
      /// rather than having its own — the two are the same S11/S22 data,
      /// just a complex trajectory vs. a frequency-domain scalar — but
      /// keeps its own extrema-marker visibility, since Smith itself has
      /// none (a 2D trajectory has no single meaningful min/max).
      ShowVswrExtrema: bool
      /// Savitzky-Golay smoothing of the group delay curves (both display
      /// modes) — off by default, since it's not the raw measured data.
      SmoothGroupDelay: bool
      /// Time-gate (ns) applied to the TDR Gated Magnitude chart — None
      /// means the full causal record, i.e. no gating (see
      /// tdrFullSpanNs/tdrGatedChartMulti). Also drawn as guide lines on the
      /// TDR Impedance chart, to calibrate the gate against the step.
      TdrGateNs: (float * float) option
      /// Bumped on every SetTdrGate/ResetTdrGate — same stale-DOM-value fix
      /// as LoadedFile.FreqRangeGen, applied to the gate's number inputs.
      TdrGateGen: int
      Status: string option }

let initModel =
    { Files = []
      // S11 + S21 by default: the reflection/transmission pair most people
      // check first; S12/S22 are a click away via the parameter toggles.
      MagnitudeSelected = Set.ofList [ (1, 1); (2, 1) ]
      PhaseSelected = Set.ofList [ (1, 1); (2, 1) ]
      SmithSelected = Set.ofList [ (1, 1) ]
      GroupDelaySelected = Set.ofList [ (2, 1) ]
      TdrSelected = Set.ofList [ (1, 1) ]
      GroupDelayMode = Absolute
      ShowMagnitudeExtrema = true
      ShowGroupDelayExtrema = true
      ShowTdrExtrema = true
      ShowVswrExtrema = true
      SmoothGroupDelay = false
      TdrGateNs = None
      TdrGateGen = 0
      Status = None }

type Message =
    | FileDropped of fileName: string * content: string
    | RemoveFile of fileName: string
    | ClearFiles
    | ToggleParam of chart: ChartKind * i: int * j: int
    | SetGroupDelayMode of DisplayMode
    /// Only meaningful for MagnitudeChart/GroupDelayChart — the other two
    /// chart kinds never show min/max markers (see TouchstonePlot.fs).
    | SetShowExtrema of chart: ChartKind * show: bool
    | SetSmoothGroupDelay of bool
    | SetTdrGate of loNs: float * hiNs: float
    | ResetTdrGate
    | SetFreqRange of fileName: string * loGHz: float * hiGHz: float
    | ResetFreqRange of fileName: string
    | SetFreqRangeLinked of fileName: string * linked: bool
    | SetStatus of string option

/// Compares two filenames "naturally": a run of digits compares by its
/// numeric value rather than character-by-character, so e.g. "c2.s2p"
/// sorts before "c10.s2p" (a plain ordinal compare would put "c10" first,
/// since '1' < '2'). Non-digit runs still compare ordinally. Used to keep
/// the loaded file list - and so every plot color derived from its order,
/// see TouchstonePlot.setFileOrder - in a stable, human-expected order.
let naturalCompare (a: string) (b: string) =
    let chunks (s: string) =
        System.Text.RegularExpressions.Regex.Matches(s, @"\d+|\D+")
        |> Seq.cast<System.Text.RegularExpressions.Match>
        |> Seq.map (fun m -> m.Value)
        |> List.ofSeq

    let rec compareChunks (xs: string list) (ys: string list) =
        match xs, ys with
        | [], [] -> 0
        | [], _ -> -1
        | _, [] -> 1
        | x :: xs', y :: ys' ->
            let bothDigits =
                x.Length > 0 && y.Length > 0 && System.Char.IsDigit x.[0] && System.Char.IsDigit y.[0]

            let c =
                if bothDigits then
                    compare (System.Numerics.BigInteger.Parse x) (System.Numerics.BigInteger.Parse y)
                else
                    System.String.CompareOrdinal(x, y)

            if c <> 0 then c else compareChunks xs' ys'

    compareChunks (chunks a) (chunks b)

let update message model =
    match message with
    | FileDropped(fileName, content) ->
        let result =
            try
                Ok(parse fileName content)
            with ex ->
                Error ex.Message

        let entry =
            { FileName = fileName
              Data = result
              FreqRangeGHz = None
              FreqRangeGen = 0
              FreqRangeLinked = true }

        let files =
            if model.Files |> List.exists (fun f -> f.FileName = fileName) then
                model.Files |> List.map (fun f -> if f.FileName = fileName then entry else f)
            else
                model.Files @ [ entry ]
            // Keeps the file list - and so every plot color derived from its
            // order - in natural alphanumeric order regardless of the order
            // files were dropped/selected in, and regardless of the order
            // their (async) FileReader reads happen to complete in.
            |> List.sortWith (fun a b -> naturalCompare a.FileName b.FileName)

        { model with Files = files }
    | RemoveFile fileName ->
        { model with Files = model.Files |> List.filter (fun f -> f.FileName <> fileName) }
    | ClearFiles -> initModel
    | ToggleParam(chart, i, j) ->
        let toggle (selected: Set<int * int>) =
            if selected.Contains(i, j) then
                Set.remove (i, j) selected
            else
                Set.add (i, j) selected

        match chart with
        | MagnitudeChart -> { model with MagnitudeSelected = toggle model.MagnitudeSelected }
        | PhaseChart -> { model with PhaseSelected = toggle model.PhaseSelected }
        | SmithChart -> { model with SmithSelected = toggle model.SmithSelected }
        | GroupDelayChart -> { model with GroupDelaySelected = toggle model.GroupDelaySelected }
        | TdrChart -> { model with TdrSelected = toggle model.TdrSelected }
        | VswrChart -> model
    | SetGroupDelayMode mode -> { model with GroupDelayMode = mode }
    | SetShowExtrema(MagnitudeChart, show) -> { model with ShowMagnitudeExtrema = show }
    | SetShowExtrema(GroupDelayChart, show) -> { model with ShowGroupDelayExtrema = show }
    | SetShowExtrema(TdrChart, show) -> { model with ShowTdrExtrema = show }
    | SetShowExtrema(VswrChart, show) -> { model with ShowVswrExtrema = show }
    | SetShowExtrema(_, _) -> model
    | SetSmoothGroupDelay smooth -> { model with SmoothGroupDelay = smooth }
    | SetTdrGate(loNs, hiNs) ->
        { model with
            TdrGateNs = Some(min loNs hiNs, max loNs hiNs)
            TdrGateGen = model.TdrGateGen + 1 }
    | ResetTdrGate -> { model with TdrGateNs = None; TdrGateGen = model.TdrGateGen + 1 }
    | SetFreqRange(fileName, loGHz, hiGHz) ->
        let lo, hi = min loGHz hiGHz, max loGHz hiGHz

        // Edits to a linked file propagate to every other linked file, same
        // as before; edits to a file that's opted out of the group only
        // ever affect that one file, regardless of what the rest are doing.
        let editedIsLinked =
            model.Files
            |> List.tryFind (fun f -> f.FileName = fileName)
            |> Option.map (fun f -> f.FreqRangeLinked)
            |> Option.defaultValue false

        let apply f =
            if f.FileName = fileName || (editedIsLinked && f.FreqRangeLinked) then
                { f with
                    FreqRangeGHz = Some(lo, hi)
                    FreqRangeGen = f.FreqRangeGen + 1 }
            else
                f

        { model with Files = model.Files |> List.map apply }
    | ResetFreqRange fileName ->
        let editedIsLinked =
            model.Files
            |> List.tryFind (fun f -> f.FileName = fileName)
            |> Option.map (fun f -> f.FreqRangeLinked)
            |> Option.defaultValue false

        let apply f =
            if f.FileName = fileName || (editedIsLinked && f.FreqRangeLinked) then
                { f with
                    FreqRangeGHz = None
                    FreqRangeGen = f.FreqRangeGen + 1 }
            else
                f

        { model with Files = model.Files |> List.map apply }
    | SetFreqRangeLinked(fileName, linked) ->
        { model with
            Files =
                model.Files
                |> List.map (fun f -> if f.FileName = fileName then { f with FreqRangeLinked = linked } else f) }
    | SetStatus status -> { model with Status = status }

/// The Ok files, paired with their filename for use as an overlay chart
/// label, windowed down to each file's selected frequency range (if any).
let okFiles (model: Model) =
    let files =
        model.Files
        |> List.choose (fun f ->
            match f.Data with
            | Ok data ->
                let windowedData =
                    match f.FreqRangeGHz with
                    | Some(lo, hi) -> windowed (lo * 1e9) (hi * 1e9) data
                    | None -> data

                Some(f.FileName, windowedData)
            | Error _ -> None)

    // Fixes each file's plot color to its position in this list (see
    // TouchstonePlot.setFileOrder) so the color used by any chart built
    // from this result, and the color shown in the file-list swatch (also
    // derived from this same function), always agree.
    setFileOrder (files |> List.map fst)
    files
