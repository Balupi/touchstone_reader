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
    let mutable lastTdrKey: string option = None
    let mutable lastTdrGatedKey: string option = None
    // Guards against the render triggered by our own SetStatus dispatch
    // re-entering this method (Blazor calls OnAfterRenderAsync after every
    // render) and racing to redo or prematurely clear the same work.
    let mutable isRendering = false
    // Each chart's CSV thunk, kept instead of a built string so it's only
    // materialized on demand (GetCsv, called from the download button) —
    // building it eagerly for every render was real, mostly-wasted work for
    // large real-world sweeps (see ChartResult in TouchstonePlot.fs).
    let mutable lastCsvThunks: Map<string, unit -> string> = Map.empty

    let view model dispatch =
        currentModel <- model
        renderView model dispatch

    override this.Program = Program.mkSimple (fun _ -> initModel) update view

    /// Builds the CSV for one chart on demand — called from interop.js's
    /// download button instead of the CSV being pre-built on every render.
    [<JSInvokable>]
    member this.GetCsv(divId: string) : string =
        match lastCsvThunks.TryFind divId with
        | Some thunk -> thunk ()
        | None -> ""

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
                    // Includes each file's (now-windowed) point count and
                    // bounds, not just its name, so narrowing a file's
                    // frequency-range slider invalidates every section's
                    // cache the same way changing the file selection does.
                    let filesKey =
                        ok
                        |> List.map (fun (name, data) ->
                            let n = data.Frequencies.Length
                            let lo = if n > 0 then data.Frequencies.[0] else 0.0
                            let hi = if n > 0 then data.Frequencies.[n - 1] else 0.0
                            sprintf "%s@%d:%g-%g" name n lo hi)
                        |> String.concat "|"

                    let keyOf (selected: (int * int) list) =
                        filesKey + "##" + (selected |> List.map (fun (i, j) -> sprintf "%d%d" i j) |> String.concat ",")

                    let magSelected = magnitudeQuadOrder |> List.filter currentModel.MagnitudeSelected.Contains
                    let phaseSelected = magnitudeQuadOrder |> List.filter currentModel.PhaseSelected.Contains
                    let smithSelected = smithOrder |> List.filter currentModel.SmithSelected.Contains
                    let gdSelected = groupDelayOrder |> List.filter currentModel.GroupDelaySelected.Contains
                    let tdrSelected = tdrOrder |> List.filter currentModel.TdrSelected.Contains
                    let gdMode = currentModel.GroupDelayMode
                    let showMagExtrema = currentModel.ShowMagnitudeExtrema
                    let showGdExtrema = currentModel.ShowGroupDelayExtrema
                    let showTdrExtrema = currentModel.ShowTdrExtrema
                    let smoothGd = currentModel.SmoothGroupDelay
                    let tdrGateNs = currentModel.TdrGateNs

                    Some
                        {| Ok = ok
                           MagSelected = magSelected
                           PhaseSelected = phaseSelected
                           SmithSelected = smithSelected
                           GdSelected = gdSelected
                           TdrSelected = tdrSelected
                           GdMode = gdMode
                           ShowMagExtrema = showMagExtrema
                           ShowGdExtrema = showGdExtrema
                           ShowTdrExtrema = showTdrExtrema
                           SmoothGd = smoothGd
                           TdrGateNs = tdrGateNs
                           MagKey = keyOf magSelected + "##" + string showMagExtrema
                           PhaseKey = keyOf phaseSelected
                           SmithKey = keyOf smithSelected
                           GdKey = keyOf gdSelected + "##" + string gdMode + "##" + string showGdExtrema + "##" + string smoothGd
                           TdrKey = keyOf tdrSelected + "##" + string showTdrExtrema + "##" + string tdrGateNs
                           TdrGatedKey = keyOf tdrSelected + "##" + string tdrGateNs |}

            if not isRendering then
                match pendingWork () with
                | None ->
                    lastMagnitudeKey <- None
                    lastPhaseKey <- None
                    lastSmithKey <- None
                    lastGroupDelayKey <- None
                    lastTdrKey <- None
                    lastTdrGatedKey <- None
                    lastCsvThunks <- Map.empty

                    if currentModel.Status.IsSome then
                        this.Dispatch(SetStatus None)
                | Some w when
                    lastMagnitudeKey = Some w.MagKey
                    && lastPhaseKey = Some w.PhaseKey
                    && lastSmithKey = Some w.SmithKey
                    && lastGroupDelayKey = Some w.GdKey
                    && lastTdrKey = Some w.TdrKey
                    && lastTdrGatedKey = Some w.TdrGatedKey
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
                        let needsTdr = lastTdrKey <> Some w.TdrKey
                        let needsTdrGated = lastTdrGatedKey <> Some w.TdrGatedKey
                        lastMagnitudeKey <- Some w.MagKey
                        lastPhaseKey <- Some w.PhaseKey
                        lastSmithKey <- Some w.SmithKey
                        lastGroupDelayKey <- Some w.GdKey
                        lastTdrKey <- Some w.TdrKey
                        lastTdrGatedKey <- Some w.TdrGatedKey

                        let render (divId: string) (result: ChartResult) : Task =
                            lastCsvThunks <- lastCsvThunks |> Map.add divId result.Csv

                            this.JSRuntime
                                .InvokeVoidAsync("touchstoneInterop.renderChart", divId, GenericChart.toFigureJson result.Chart)
                                .AsTask()

                        if needsMagnitude then
                            match magnitudeQuadMulti w.ShowMagExtrema w.MagSelected w.Ok with
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
                                | Absolute -> groupDelayChartMulti w.ShowGdExtrema w.SmoothGd w.GdSelected w.Ok
                                | DeviationFromMean ->
                                    groupDelayDeviationChartMulti w.ShowGdExtrema w.SmoothGd w.GdSelected w.Ok

                            match result with
                            | Some result -> do! render "chart-group-delay" result
                            | None -> ()

                        if needsTdr then
                            match tdrChartMulti w.ShowTdrExtrema w.TdrGateNs w.TdrSelected w.Ok with
                            | Some result -> do! render "chart-tdr" result
                            | None -> ()

                        if needsTdrGated then
                            match tdrGatedChartMulti w.TdrGateNs w.TdrSelected w.Ok with
                            | Some result -> do! render "chart-tdr-gated" result
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
