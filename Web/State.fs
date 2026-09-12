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
/// since '1' < '2'). Everything else compares ordinally. Used to keep the
/// loaded file list - and so every plot color derived from its order, see
/// TouchstonePlot.setFileOrder - in a stable, human-expected order.
///
/// Walks both strings in a single pass instead of splitting them into
/// chunks first: model.Files is re-sorted on every individual FileDropped,
/// so a batch drop runs this O(n^2 log n) times, and an allocating
/// comparator (regex matches + lists + BigInteger parses) dominated load
/// time past ~50 files - 500 files spent ~3.8 s comparing names against
/// ~2 s actually parsing them. This allocates nothing and measures ~120x
/// faster, which puts name sorting back into the noise at any file count
/// a folder drop can realistically produce.
///
/// Digit runs of equal value but different written length ("7" vs "07")
/// compare equal, leaving their relative order to List.sortWith's
/// stability.
let naturalCompare (a: string) (b: string) =
    let mutable i = 0
    let mutable j = 0
    let mutable result = 0

    while result = 0 && i < a.Length && j < b.Length do
        if System.Char.IsDigit a.[i] && System.Char.IsDigit b.[j] then
            // Both sides are at a digit run: compare the two runs by value.
            // Skipping leading zeros first means the run with more remaining
            // digits is the larger number, and runs of equal length then
            // compare correctly digit by digit.
            let mutable startA = i
            let mutable startB = j

            while startA < a.Length && a.[startA] = '0' do
                startA <- startA + 1

            while startB < b.Length && b.[startB] = '0' do
                startB <- startB + 1

            let mutable endA = startA
            let mutable endB = startB

            while endA < a.Length && System.Char.IsDigit a.[endA] do
                endA <- endA + 1

            while endB < b.Length && System.Char.IsDigit b.[endB] do
                endB <- endB + 1

            if endA - startA <> endB - startB then
                result <- compare (endA - startA) (endB - startB)
            else
                let mutable k = 0

                while result = 0 && k < endA - startA do
                    if a.[startA + k] <> b.[startB + k] then
                        result <- compare a.[startA + k] b.[startB + k]

                    k <- k + 1

            i <- endA
            j <- endB
        elif a.[i] <> b.[j] then
            result <- compare a.[i] b.[j]
        else
            i <- i + 1
            j <- j + 1

    // Ran out of one side without a decision: whichever string still has
    // characters left is the longer one, and sorts after ("c1.s2p" before
    // "c1x.s2p").
    if result <> 0 then result else compare (a.Length - i) (b.Length - j)

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
