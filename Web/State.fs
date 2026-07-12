/// Elmish model, messages, and update function for the Touchstone web app.
module TouchstoneReader.Web.State

open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot

type LoadedFile =
    { FileName: string
      Data: Result<TouchstoneFile, string> }

/// Which collapsible section a ToggleParam message applies to.
type ChartKind =
    | MagnitudeChart
    | PhaseChart
    | SmithChart
    | GroupDelayChart

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
      GroupDelayMode: DisplayMode
      ShowMagnitudeExtrema: bool
      ShowGroupDelayExtrema: bool
      Status: string option }

let initModel =
    { Files = []
      MagnitudeSelected = Set.ofList magnitudeQuadOrder
      PhaseSelected = Set.ofList magnitudeQuadOrder
      SmithSelected = Set.ofList smithOrder
      GroupDelaySelected = Set.ofList groupDelayOrder
      GroupDelayMode = Absolute
      ShowMagnitudeExtrema = true
      ShowGroupDelayExtrema = true
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
    | SetStatus of string option

let update message model =
    match message with
    | FileDropped(fileName, content) ->
        let result =
            try
                Ok(parse fileName content)
            with ex ->
                Error ex.Message

        let entry = { FileName = fileName; Data = result }

        let files =
            if model.Files |> List.exists (fun f -> f.FileName = fileName) then
                model.Files |> List.map (fun f -> if f.FileName = fileName then entry else f)
            else
                model.Files @ [ entry ]

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
    | SetGroupDelayMode mode -> { model with GroupDelayMode = mode }
    | SetShowExtrema(MagnitudeChart, show) -> { model with ShowMagnitudeExtrema = show }
    | SetShowExtrema(GroupDelayChart, show) -> { model with ShowGroupDelayExtrema = show }
    | SetShowExtrema(_, _) -> model
    | SetStatus status -> { model with Status = status }

/// The Ok files, paired with their filename for use as an overlay chart label.
let okFiles (model: Model) =
    model.Files
    |> List.choose (fun f ->
        match f.Data with
        | Ok data -> Some(f.FileName, data)
        | Error _ -> None)
