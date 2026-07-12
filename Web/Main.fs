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

type Model =
    { Files: LoadedFile list
      SelectedParams: Set<int * int>
      Status: string option }

let initModel =
    { Files = []
      SelectedParams = Set.ofList magnitudeQuadOrder
      Status = None }

type Message =
    | FileDropped of fileName: string * content: string
    | RemoveFile of fileName: string
    | ClearFiles
    | ToggleParam of i: int * j: int
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
    | ToggleParam(i, j) ->
        let key = (i, j)

        let selected =
            if model.SelectedParams.Contains key then
                Set.remove key model.SelectedParams
            else
                Set.add key model.SelectedParams

        { model with SelectedParams = selected }
    | SetStatus status -> { model with Status = status }

/// The Ok files, paired with their filename for use as an overlay chart label.
let private okFiles (model: Model) =
    model.Files
    |> List.choose (fun f ->
        match f.Data with
        | Ok data -> Some(f.FileName, data)
        | Error _ -> None)

let private summary (data: TouchstoneFile) =
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
        | Ok data -> sprintf "%s — %s" f.FileName (summary data)
        | Error msg -> sprintf "%s — %s" f.FileName msg

    div {
        attr.``class`` cls

        button {
            attr.``class`` "delete"
            on.click (fun _ -> dispatch (RemoveFile f.FileName))
        }

        text
    }

let private paramToggle (dispatch: Dispatch<Message>) (selected: Set<int * int>) (i, j) =
    let isOn = selected.Contains(i, j)

    button {
        attr.``class`` (if isOn then "button is-small is-info mr-2" else "button is-small mr-2")
        on.click (fun _ -> dispatch (ToggleParam(i, j)))
        sprintf "S%d%d" i j
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
                                div {
                                    attr.``class`` "field is-grouped mt-4"

                                    for pij in magnitudeQuadOrder do
                                        paramToggle dispatch model.SelectedParams pij
                                }

                                if model.SelectedParams.IsEmpty then
                                    p {
                                        attr.``class`` "has-text-grey"
                                        "Select at least one S-parameter above to show the magnitude plot."
                                    }
                                else
                                    div {
                                        attr.id "chart-magnitude"
                                        attr.``class`` "mt-2"
                                    }
                            }

                        div {
                            attr.id "chart-phase"
                            attr.``class`` "mt-4"
                        }

                        if ok |> List.exists (fun (_, data) -> data.Option.Parameter = S) then
                            div {
                                attr.id "chart-smith"
                                attr.``class`` "mt-4"
                            }
                    }
            }
    }

type App() =
    inherit ProgramComponent<Model, Message>()

    let mutable currentModel = initModel
    let mutable lastFilesKey: string option = None
    let mutable lastMagnitudeKey: string option = None
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

            /// Snapshot of what would need (re)rendering right now.
            let pendingWork () =
                let ok = okFiles currentModel

                if ok.IsEmpty then
                    None
                else
                    let filesKey = ok |> List.map fst |> String.concat "|"
                    let selected = magnitudeQuadOrder |> List.filter currentModel.SelectedParams.Contains

                    let magnitudeKey =
                        filesKey
                        + "##"
                        + (selected |> List.map (fun (i, j) -> sprintf "%d%d" i j) |> String.concat ",")

                    Some(ok, selected, filesKey, magnitudeKey)

            if not isRendering then
                match pendingWork () with
                | None ->
                    lastFilesKey <- None
                    lastMagnitudeKey <- None

                    if currentModel.Status.IsSome then
                        this.Dispatch(SetStatus None)
                | Some(_, _, filesKey, magnitudeKey) when
                    lastFilesKey = Some filesKey && lastMagnitudeKey = Some magnitudeKey
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
                    | Some(ok, selected, filesKey, magnitudeKey) ->
                        let needsMagnitude = lastMagnitudeKey <> Some magnitudeKey
                        let needsFiles = lastFilesKey <> Some filesKey
                        lastMagnitudeKey <- Some magnitudeKey
                        lastFilesKey <- Some filesKey

                        let render (divId: string) (chart: GenericChart.GenericChart) : Task =
                            this.JSRuntime
                                .InvokeVoidAsync("touchstoneInterop.renderChart", divId, GenericChart.toFigureJson chart)
                                .AsTask()

                        if needsMagnitude then
                            match magnitudeQuadMulti selected ok with
                            | Some chart -> do! render "chart-magnitude" chart
                            | None -> ()

                        if needsFiles then
                            do! render "chart-phase" (phaseChartMulti ok)

                            match smithChartMulti ok with
                            | Some chart -> do! render "chart-smith" chart
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
