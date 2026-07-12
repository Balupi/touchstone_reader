module TouchstoneReader.Web.Main

open System.Threading.Tasks
open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Microsoft.JSInterop
open Bolero
open Elmish
open Plotly.NET
open TouchstoneReader.TouchstonePlot
open TouchstoneReader.Web.State
open TouchstoneReader.Web.View

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
                do! this.JSRuntime.InvokeVoidAsync("touchstoneInterop.bindThemeListener").AsTask()

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
                    let gdMode = currentModel.GroupDelayMode

                    Some
                        {| Ok = ok
                           MagSelected = magSelected
                           PhaseSelected = phaseSelected
                           SmithSelected = smithSelected
                           GdSelected = gdSelected
                           GdMode = gdMode
                           MagKey = keyOf magSelected
                           PhaseKey = keyOf phaseSelected
                           SmithKey = keyOf smithSelected
                           GdKey = keyOf gdSelected + "##" + string gdMode |}

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

                        let render (divId: string) (result: ChartResult) : Task =
                            this.JSRuntime
                                .InvokeVoidAsync(
                                    "touchstoneInterop.renderChart",
                                    divId,
                                    GenericChart.toFigureJson result.Chart,
                                    result.Csv
                                )
                                .AsTask()

                        if needsMagnitude then
                            match magnitudeQuadMulti w.MagSelected w.Ok with
                            | Some result -> do! render "chart-magnitude" result
                            | None -> ()

                        if needsPhase then
                            match phaseQuadMulti w.PhaseSelected w.Ok with
                            | Some result -> do! render "chart-phase" result
                            | None -> ()

                        if needsSmith then
                            match smithChartMulti w.SmithSelected w.Ok with
                            | Some result -> do! render "chart-smith" result
                            | None -> ()

                        if needsGroupDelay then
                            let result =
                                match w.GdMode with
                                | Absolute -> groupDelayChartMulti w.GdSelected w.Ok
                                | DeviationFromMean -> groupDelayDeviationChartMulti w.GdSelected w.Ok

                            match result with
                            | Some result -> do! render "chart-group-delay" result
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
