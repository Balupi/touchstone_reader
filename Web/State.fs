/// Elmish model, messages, and update function for the Touchstone web app.
module TouchstoneReader.Web.State

open TouchstoneReader.Touchstone
open TouchstoneReader.LimitTest
open TouchstoneReader.PortMap
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
      /// Files currently hidden via their file-list color swatch, mirrored
      /// here from interop.js's own _hiddenFiles (which stays the source of
      /// truth for what the charts show). Read only by the limit test: a
      /// curve that isn't on screen isn't tested, and gets no verdict badge.
      /// Deliberately absent from every chart cache key in Main.fs — hiding a
      /// file must stay a cheap view re-render, not a chart rebuild.
      HiddenFiles: Set<string>
      /// One Keysight-style limit table per magnitude parameter, keyed by
      /// (i, j) — the instrument keeps limits per trace, and a return-loss
      /// mask has nothing to do with an insertion-loss one. Shared by every
      /// loaded file: comparing several assemblies against one spec is the
      /// whole point here. Persisted to localStorage from Main.fs.
      LimitTables: Map<int * int, LimitSegment list>
      /// True while the tables are the ones restored from the browser at
      /// startup and nothing has been edited since. Only the label in the
      /// Limit Lines summary reads it: a spec carried over from a previous
      /// session decides PASS/FAIL for whatever files are loaded now, which
      /// may not be the files it was written for, so it says where it came
      /// from instead of quietly applying itself.
      LimitsRestored: bool
      /// How each file's ports map onto the two ends of the assembly, keyed
      /// by file name. A Touchstone file carries none of this — port
      /// numbering is whatever the measurement setup used — so there is
      /// deliberately no default: a file with more than 2 ports has no entry
      /// here until it's been stated, and until then the Through/NEXT/FEXT
      /// groups (and the whole Group Delay section) stay unavailable for it
      /// rather than guessing. Guessing would relabel someone's crosstalk as
      /// insertion loss. 1- and 2-port files never need an entry: with one
      /// line there is nothing to lay out, and both layouts agree.
      PortLayouts: Map<string, PortLayout>
      /// Which chart sections have their full N x N parameter matrix
      /// expanded. Only relevant past a handful of parameters — a 2-port
      /// file's four buttons are shown flat, with no matrix to expand.
      MatrixOpen: Set<ChartKind>
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
      // Min/max markers start off: they're a reading aid for one question
      // ("what's the worst value anywhere in this set?"), not something every
      // chart needs to open with, and on the magnitude chart they now share
      // the plot with the limit mask, which is the boundary that actually
      // matters. Each chart's own switch turns them back on.
      ShowMagnitudeExtrema = false
      ShowGroupDelayExtrema = false
      ShowTdrExtrema = false
      ShowVswrExtrema = false
      SmoothGroupDelay = false
      TdrGateNs = None
      TdrGateGen = 0
      HiddenFiles = Set.empty
      LimitTables = Map.empty
      LimitsRestored = false
      PortLayouts = Map.empty
      MatrixOpen = Set.empty
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
    /// Replaces every limit table at once — used to bring in what was stored
    /// in the browser on startup.
    /// Pushed in from interop.js when a file-list swatch is clicked.
    | SetHiddenFiles of Set<string>
    | SetLimitTables of Map<int * int, LimitSegment list>
    | AddLimitSegment of i: int * j: int
    /// Replaces one row wholesale rather than carrying a field selector: the
    /// editor rebuilds the row from its own inputs anyway.
    | SetLimitSegment of i: int * j: int * index: int * segment: LimitSegment
    | RemoveLimitSegment of i: int * j: int * index: int
    | ClearLimitTable of i: int * j: int
    /// Drops every table, which also clears the stored copy (an empty
    /// serialization removes the localStorage entry, see Main.fs).
    | ClearAllLimitTables
    /// States how one file's ports face each other. Never set implicitly.
    | SetPortLayout of fileName: string * layout: PortLayout
    /// Turns a whole parameter group (Reflection / Through / NEXT / FEXT) on
    /// or off at once: on unless every member is already selected, in which
    /// case off, which is how a "select all" checkbox behaves.
    | ToggleParamGroup of chart: ChartKind * group: ParamGroup
    /// Expands/collapses a section's full N x N parameter matrix.
    | ToggleMatrix of chart: ChartKind

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

/// The frequency span (GHz) covered by the loaded files, for seeding a new
/// limit row. Uses every Ok file's native sweep, not the windowed view: a
/// limit table describes the spec, not whatever range happens to be on
/// screen right now.
let unionStimulusGHz (model: Model) =
    let bounds =
        model.Files
        |> List.choose (fun f ->
            match f.Data with
            | Ok data when data.Frequencies.Length > 0 ->
                Some(data.Frequencies.[0] / 1e9, data.Frequencies.[data.Frequencies.Length - 1] / 1e9)
            | _ -> None)

    match bounds with
    | [] -> None
    | _ -> Some(bounds |> List.map fst |> List.min, bounds |> List.map snd |> List.max)

/// The port count the parameter pickers are built for: the largest among the
/// loaded, parseable files. The selection is shared across files, and a
/// parameter only some of them have simply doesn't appear in the others'
/// subplots (see TouchstonePlot.quadMulti), so offering the union is right.
/// Falls back to 2 with nothing loaded, which keeps the pickers looking
/// exactly as they always have before the first file arrives.
let pickerPorts (model: Model) =
    let counts =
        model.Files
        |> List.choose (fun f ->
            match f.Data with
            | Ok data -> Some data.Ports
            | Error _ -> None)

    match counts with
    | [] -> 2
    | _ -> List.max counts

/// The layout the parameter groups are named after: the stated layout of the
/// first file that has `pickerPorts` ports. None means it hasn't been stated
/// yet, and the caller must not guess — a through path named under the wrong
/// layout is someone's crosstalk relabelled as insertion loss. 1- and 2-port
/// files need no statement: with a single line there is nothing to lay out,
/// and both layouts agree on S21, so those answer Some outright.
let pickerLayout (model: Model) =
    let ports = pickerPorts model

    if ports <= 2 then
        Some EndsSplit
    else
        model.Files
        |> List.tryFind (fun f ->
            match f.Data with
            | Ok data -> data.Ports = ports
            | Error _ -> false)
        |> Option.bind (fun f -> model.PortLayouts.TryFind f.FileName)

/// Layout used purely to order the magnitude/phase matrix. That matrix
/// covers every parameter under either layout — only the sequence differs —
/// so a file whose layout hasn't been stated still gets a complete picker.
/// Nothing that *names* a path may use this; see pickerLayout.
let orderingLayout (model: Model) = defaultArg (pickerLayout model) EndsSplit

/// The parameters a section offers. Magnitude and Phase offer the whole
/// matrix; Smith, VSWR and TDR the reflection diagonal; Group Delay the
/// through paths — the one list that needs the layout to have been stated,
/// hence empty until it is.
let paramOrderFor (model: Model) (chart: ChartKind) =
    let ports = pickerPorts model

    match chart with
    | MagnitudeChart
    | PhaseChart -> magnitudeParamOrder (orderingLayout model) ports
    | SmithChart
    | VswrChart -> smithParamOrder ports
    | TdrChart -> tdrParamOrder ports
    | GroupDelayChart ->
        match pickerLayout model with
        | Some layout -> groupDelayParamOrder layout ports
        | None -> []

/// The selection a section reads. VSWR shares the Smith chart's.
let selectionFor (model: Model) (chart: ChartKind) =
    match chart with
    | MagnitudeChart -> model.MagnitudeSelected
    | PhaseChart -> model.PhaseSelected
    | SmithChart
    | VswrChart -> model.SmithSelected
    | GroupDelayChart -> model.GroupDelaySelected
    | TdrChart -> model.TdrSelected

let private withSelection (chart: ChartKind) (selected: Set<int * int>) model =
    match chart with
    | MagnitudeChart -> { model with MagnitudeSelected = selected }
    | PhaseChart -> { model with PhaseSelected = selected }
    | SmithChart
    | VswrChart -> { model with SmithSelected = selected }
    | GroupDelayChart -> { model with GroupDelaySelected = selected }
    | TdrChart -> { model with TdrSelected = selected }

/// A group's parameters as far as one section offers them — what its group
/// button switches. Empty means the button has nothing to do and is hidden.
///
/// Forward direction only (S31, not S13): a passive assembly is reciprocal, so
/// the reverse of each path is nominally the same measurement, and selecting
/// both would double the subplots for no new information. The reverses stay
/// individually reachable in the matrix, which is where someone comparing S31
/// against S13 as a measurement-quality check would go anyway.
let groupParamsFor (model: Model) (chart: ChartKind) (group: ParamGroup) =
    let ports = pickerPorts model
    let offered = paramOrderFor model chart |> Set.ofList

    let members =
        match pickerLayout model, group with
        // The diagonal is a reflection under either layout, so this one group
        // can be offered before anything has been stated about the ports.
        | None, Reflection -> reflectionParams ports
        | None, _ -> []
        | Some layout, _ -> groupMembers layout ports false group

    members |> List.filter offered.Contains

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
        { model with
            Files = model.Files |> List.filter (fun f -> f.FileName <> fileName)
            // Dropped with the file rather than kept around: a file reloaded
            // later may well be a different measurement under the same name,
            // and a silently remembered layout is the one thing this feature
            // exists to avoid.
            PortLayouts = Map.remove fileName model.PortLayouts }
    | ClearFiles -> initModel
    | ToggleParam(chart, i, j) ->
        let toggle (selected: Set<int * int>) =
            if selected.Contains(i, j) then
                Set.remove (i, j) selected
            else
                Set.add (i, j) selected

        withSelection chart (toggle (selectionFor model chart)) model
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
    | SetHiddenFiles files -> { model with HiddenFiles = files }
    | SetLimitTables tables -> { model with LimitTables = tables; LimitsRestored = true }
    | AddLimitSegment(i, j) ->
        let existing = model.LimitTables |> Map.tryFind (i, j) |> Option.defaultValue []

        // A new row spans whatever the loaded files actually cover, so it
        // starts out testing something instead of sitting at 0-0 GHz.
        let lo, hi =
            match unionStimulusGHz model with
            | Some(lo, hi) -> lo, hi
            | None -> 0.0, 0.0

        { model with
            LimitTables = model.LimitTables |> Map.add (i, j) (existing @ [ newSegment lo hi ])
            LimitsRestored = false }
    | SetLimitSegment(i, j, index, segment) ->
        let existing = model.LimitTables |> Map.tryFind (i, j) |> Option.defaultValue []

        let updated = existing |> List.mapi (fun k s -> if k = index then segment else s)

        { model with
            LimitTables = model.LimitTables |> Map.add (i, j) updated
            LimitsRestored = false }
    | RemoveLimitSegment(i, j, index) ->
        let remaining =
            model.LimitTables
            |> Map.tryFind (i, j)
            |> Option.defaultValue []
            |> List.mapi (fun k s -> k, s)
            |> List.filter (fun (k, _) -> k <> index)
            |> List.map snd

        { model with
            LimitTables =
                if remaining.IsEmpty then
                    model.LimitTables |> Map.remove (i, j)
                else
                    model.LimitTables |> Map.add (i, j) remaining
            LimitsRestored = false }
    | ClearLimitTable(i, j) ->
        { model with
            LimitTables = model.LimitTables |> Map.remove (i, j)
            LimitsRestored = false }
    | ClearAllLimitTables -> { model with LimitTables = Map.empty; LimitsRestored = false }
    | SetPortLayout(fileName, layout) ->
        { model with PortLayouts = Map.add fileName layout model.PortLayouts }
    | ToggleParamGroup(chart, group) ->
        let members = groupParamsFor model chart group

        if members.IsEmpty then
            model
        else
            let selected = selectionFor model chart
            // "Select all" semantics: the button only clears the group once
            // every one of its members is already on, so a half-selected
            // group completes rather than emptying.
            let next =
                if members |> List.forall selected.Contains then
                    members |> List.fold (fun s p -> Set.remove p s) selected
                else
                    members |> List.fold (fun s p -> Set.add p s) selected

            withSelection chart next model
    | ToggleMatrix chart ->
        { model with
            MatrixOpen =
                if model.MatrixOpen.Contains chart then
                    Set.remove chart model.MatrixOpen
                else
                    Set.add chart model.MatrixOpen }

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
