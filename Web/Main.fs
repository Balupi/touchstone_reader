module TouchstoneReader.Web.Main

open System.Threading.Tasks
open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Microsoft.JSInterop
open Bolero
open Bolero.Html
open Elmish
open Plotly.NET
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

type Model =
    { Files: LoadedFile list
      MagnitudeSelected: Set<int * int>
      PhaseSelected: Set<int * int>
      SmithSelected: Set<int * int>
      GroupDelaySelected: Set<int * int>
      Status: string option }

let initModel =
    { Files = []
      MagnitudeSelected = Set.ofList magnitudeQuadOrder
      PhaseSelected = Set.ofList magnitudeQuadOrder
      SmithSelected = Set.ofList smithOrder
      GroupDelaySelected = Set.ofList groupDelayOrder
      Status = None }

type Message =
    | FileDropped of fileName: string * content: string
    | RemoveFile of fileName: string
    | ClearFiles
    | ToggleParam of chart: ChartKind * i: int * j: int
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
    | SetStatus status -> { model with Status = status }

/// The Ok files, paired with their filename for use as an overlay chart label.
let private okFiles (model: Model) =
    model.Files
    |> List.choose (fun f ->
        match f.Data with
        | Ok data -> Some(f.FileName, data)
        | Error _ -> None)

let private fileSummary (data: TouchstoneFile) =
    sprintf
        "%d-port, %d frequency points, %A parameters (%A, R=%.0f Ω)"
        data.Ports
        data.Frequencies.Length
        data.Option.Parameter
        data.Option.Format
        data.Option.R

let private fileTag (dispatch: Dispatch<Message>) (f: LoadedFile) =
    let cls =
        match f.Data with
        | Ok _ -> "notification is-info mt-2"
        | Error _ -> "notification is-danger mt-2"

    let text =
        match f.Data with
        | Ok data -> sprintf "%s — %s" f.FileName (fileSummary data)
        | Error msg -> sprintf "%s — %s" f.FileName msg

    div {
        attr.``class`` cls

        button {
            attr.``class`` "delete"
            on.click (fun _ -> dispatch (RemoveFile f.FileName))
        }

        text
    }

let private paramToggle (dispatch: Dispatch<Message>) (chart: ChartKind) (selected: Set<int * int>) (i, j) =
    let isOn = selected.Contains(i, j)

    button {
        attr.``class`` (if isOn then "button is-small is-info mr-2" else "button is-small mr-2")
        on.click (fun _ -> dispatch (ToggleParam(chart, i, j)))
        sprintf "S%d%d" i j
    }

/// A collapsible <details> section with its own parameter toggle row and
/// chart container. `open`'s toggle event doesn't rebuild the Plotly chart,
/// so interop.js resizes it on expand — otherwise a chart drawn while
/// hidden renders at 0 size and never fixes itself.
let private chartSection
    (dispatch: Dispatch<Message>)
    (title: string)
    (isOpenByDefault: bool)
    (chart: ChartKind)
    (order: (int * int) list)
    (selected: Set<int * int>)
    (divId: string)
    =
    details {
        attr.``class`` "chart-section box mt-4"

        if isOpenByDefault then attr.``open`` true else attr.empty ()

        summary {
            attr.``class`` "title is-5"
            attr.style "cursor: pointer;"
            title
        }

        div {
            attr.``class`` "field is-grouped mb-3"

            for pij in order do
                paramToggle dispatch chart selected pij
        }

        if selected.IsEmpty then
            p {
                attr.``class`` "has-text-grey"
                "Select at least one parameter above."
            }
        else
            div { attr.id divId }
    }

let renderView (model: Model) (dispatch: Dispatch<Message>) =
    div {
        attr.``class`` "container mt-5"

        h1 {
            attr.``class`` "title"
            "TouchstoneReader"
        }

        p {
            attr.``class`` "subtitle"
            "Drop one or more .sNp Touchstone files to plot them overlaid."
        }

        // Always present (never inserted/removed) so it doesn't shift sibling
        // positions in Blazor's diff — that would make it repatch the chart
        // divs below and wipe out the content Plotly injected into them,
        // since Blazor has no idea that content is there. Visibility is
        // toggled with a style instead.
        p {
            attr.``class`` "has-text-warning mb-2"
            attr.style (if model.Status.IsSome then "" else "display: none")
            sprintf "⏳ %s" (defaultArg model.Status "")
        }

        label {
            attr.id "drop-zone"
            attr.``for`` "file-input"
            attr.``class`` "box has-text-centered"
            attr.style "border: 2px dashed #999; padding: 3rem; cursor: pointer; display: block;"

            p {
                attr.``class`` "is-size-5"
                "Drag & drop Touchstone files here, or click to browse"
            }

            input {
                attr.id "file-input"
                attr.``type`` "file"
                attr.multiple true
                attr.style "display: none"
            }
        }

        if not model.Files.IsEmpty then
            concat {
                div {
                    attr.``class`` "level mt-4"

                    div {
                        attr.``class`` "level-left"

                        p {
                            attr.``class`` "subtitle mb-0"
                            sprintf "%d file(s) loaded" model.Files.Length
                        }
                    }

                    div {
                        attr.``class`` "level-right"

                        button {
                            attr.``class`` "button is-small"
                            on.click (fun _ -> dispatch ClearFiles)
                            "Clear"
                        }
                    }
                }

                for f in model.Files do
                    fileTag dispatch f

                let ok = okFiles model
                let has2Port = ok |> List.exists (fun (_, data) -> data.Ports = 2)

                if not ok.IsEmpty then
                    concat {
                        if has2Port then
                            concat {
                                chartSection
                                    dispatch
                                    "Magnitude (dB)"
                                    true
                                    MagnitudeChart
                                    magnitudeQuadOrder
                                    model.MagnitudeSelected
                                    "chart-magnitude"

                                chartSection
                                    dispatch
                                    "Phase (deg)"
                                    false
                                    PhaseChart
                                    magnitudeQuadOrder
                                    model.PhaseSelected
                                    "chart-phase"
                            }

                        if ok |> List.exists (fun (_, data) -> data.Option.Parameter = S) then
                            chartSection
                                dispatch
                                "Smith Chart"
                                false
                                SmithChart
                                smithOrder
                                model.SmithSelected
                                "chart-smith"

                        if has2Port then
                            chartSection
                                dispatch
                                "Group Delay (ns)"
                                false
                                GroupDelayChart
                                groupDelayOrder
                                model.GroupDelaySelected
                                "chart-group-delay"
                    }
            }
    }

type App() =
    inherit ProgramComponent<Model, Message>()

    let mutable currentModel = initModel
    let mutable lastMagnitudeKey: string option = None
    let mutable lastPhaseKey: string option = None
    let mutable lastSmithKey: string option = None
    let mutable lastGroupDelayKey: string option = None
    // Guards against the render triggered by our own SetStatus dispatch
    // re-entering this method (Blazor calls OnAfterRenderAsync after every
    // render) and racing to redo or prematurely clear the same work.
    let mutable isRendering = false

    let view model dispatch =
        currentModel <- model
        renderView model dispatch

    override this.Program = Program.mkSimple (fun _ -> initModel) update view

    [<JSInvokable>]
    member this.OnFileDropped(fileName: string, content: string) =
        task {
            this.Dispatch(SetStatus(Some(sprintf "Parsing %s…" fileName)))
            // WASM is single-threaded: without yielding here, the browser never
            // gets a chance to paint the status before parsing blocks it.
            do! Task.Delay 1
            this.Dispatch(FileDropped(fileName, content))
        }
        :> Task

    override this.OnAfterRenderAsync(firstRender: bool) =
        let baseTask = base.OnAfterRenderAsync(firstRender)

        task {
            do! baseTask

            if firstRender then
                let objRef = DotNetObjectReference.Create(this)
                do! this.JSRuntime.InvokeVoidAsync("touchstoneInterop.setupDropZone", "drop-zone", objRef).AsTask()

            // Binds the collapse/expand resize fix on any <details> that
            // appeared since the last render; no-ops on ones already bound.
            do! this.JSRuntime.InvokeVoidAsync("touchstoneInterop.setupCollapsibleCharts").AsTask()

            /// Snapshot of the four sections' state right now: which files are
            /// loaded plus each section's own selection, combined into one key
            /// per section so each can redraw independently of the others.
            let pendingWork () =
                let ok = okFiles currentModel

                if ok.IsEmpty then
                    None
                else
                    let filesKey = ok |> List.map fst |> String.concat "|"

                    let keyOf (selected: (int * int) list) =
                        filesKey + "##" + (selected |> List.map (fun (i, j) -> sprintf "%d%d" i j) |> String.concat ",")

                    let magSelected = magnitudeQuadOrder |> List.filter currentModel.MagnitudeSelected.Contains
                    let phaseSelected = magnitudeQuadOrder |> List.filter currentModel.PhaseSelected.Contains
                    let smithSelected = smithOrder |> List.filter currentModel.SmithSelected.Contains
                    let gdSelected = groupDelayOrder |> List.filter currentModel.GroupDelaySelected.Contains

                    Some
                        {| Ok = ok
                           MagSelected = magSelected
                           PhaseSelected = phaseSelected
                           SmithSelected = smithSelected
                           GdSelected = gdSelected
                           MagKey = keyOf magSelected
                           PhaseKey = keyOf phaseSelected
                           SmithKey = keyOf smithSelected
                           GdKey = keyOf gdSelected |}

            if not isRendering then
                match pendingWork () with
                | None ->
                    lastMagnitudeKey <- None
                    lastPhaseKey <- None
                    lastSmithKey <- None
                    lastGroupDelayKey <- None

                    if currentModel.Status.IsSome then
                        this.Dispatch(SetStatus None)
                | Some w when
                    lastMagnitudeKey = Some w.MagKey
                    && lastPhaseKey = Some w.PhaseKey
                    && lastSmithKey = Some w.SmithKey
                    && lastGroupDelayKey = Some w.GdKey
                    ->
                    if currentModel.Status.IsSome then
                        this.Dispatch(SetStatus None)
                | Some _ ->
                    isRendering <- true
                    this.Dispatch(SetStatus(Some "Downsampling & rendering charts…"))
                    // WASM is single-threaded: without yielding here, the browser
                    // never gets a chance to paint the status before the
                    // downsampling/JSON-building below blocks it. Files dropped
                    // together may still be trickling in while we yield, so the
                    // actual render below re-reads currentModel from scratch
                    // instead of trusting this pre-yield snapshot.
                    do! Task.Delay 1

                    match pendingWork () with
                    | None -> ()
                    | Some w ->
                        // Each section redraws only when its own files+selection
                        // key changed, so toggling one section's buttons doesn't
                        // force the other three to redraw along with it.
                        let needsMagnitude = lastMagnitudeKey <> Some w.MagKey
                        let needsPhase = lastPhaseKey <> Some w.PhaseKey
                        let needsSmith = lastSmithKey <> Some w.SmithKey
                        let needsGroupDelay = lastGroupDelayKey <> Some w.GdKey
                        lastMagnitudeKey <- Some w.MagKey
                        lastPhaseKey <- Some w.PhaseKey
                        lastSmithKey <- Some w.SmithKey
                        lastGroupDelayKey <- Some w.GdKey

                        let render (divId: string) (chart: GenericChart.GenericChart) : Task =
                            this.JSRuntime
                                .InvokeVoidAsync("touchstoneInterop.renderChart", divId, GenericChart.toFigureJson chart)
                                .AsTask()

                        if needsMagnitude then
                            match magnitudeQuadMulti w.MagSelected w.Ok with
                            | Some chart -> do! render "chart-magnitude" chart
                            | None -> ()

                        if needsPhase then
                            match phaseQuadMulti w.PhaseSelected w.Ok with
                            | Some chart -> do! render "chart-phase" chart
                            | None -> ()

                        if needsSmith then
                            match smithChartMulti w.SmithSelected w.Ok with
                            | Some chart -> do! render "chart-smith" chart
                            | None -> ()

                        if needsGroupDelay then
                            match groupDelayChartMulti w.GdSelected w.Ok with
                            | Some chart -> do! render "chart-group-delay" chart
                            | None -> ()

                    this.Dispatch(SetStatus None)
                    isRendering <- false
        }
        :> Task

[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    builder.RootComponents.Add<App>("#app")
    builder.Build().RunAsync() |> ignore
    0
