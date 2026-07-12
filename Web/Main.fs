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

type Model = { Files: LoadedFile list }

let initModel = { Files = [] }

type Message =
    | FileDropped of fileName: string * content: string
    | ClearFiles

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

        { Files = files }
    | ClearFiles -> initModel

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

let private fileTag (f: LoadedFile) =
    match f.Data with
    | Ok data ->
        div {
            attr.``class`` "notification is-info mt-2"
            sprintf "%s — %s" f.FileName (summary data)
        }
    | Error msg ->
        div {
            attr.``class`` "notification is-danger mt-2"
            sprintf "%s — %s" f.FileName msg
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
                    fileTag f

                let ok = okFiles model

                if not ok.IsEmpty then
                    concat {
                        div {
                            attr.id "chart-magnitude"
                            attr.``class`` "mt-4"
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
    let mutable lastRenderedKey: string option = None

    let view model dispatch =
        currentModel <- model
        renderView model dispatch

    override this.Program = Program.mkSimple (fun _ -> initModel) update view

    [<JSInvokable>]
    member this.OnFileDropped(fileName: string, content: string) =
        this.Dispatch(FileDropped(fileName, content))

    override this.OnAfterRenderAsync(firstRender: bool) =
        let baseTask = base.OnAfterRenderAsync(firstRender)

        task {
            do! baseTask

            if firstRender then
                let objRef = DotNetObjectReference.Create(this)
                do! this.JSRuntime.InvokeVoidAsync("touchstoneInterop.setupDropZone", "drop-zone", objRef).AsTask()

            let ok = okFiles currentModel
            let key = ok |> List.map fst |> String.concat "|"

            if key <> "" && lastRenderedKey <> Some key then
                lastRenderedKey <- Some key

                let render (divId: string) (chart: GenericChart.GenericChart) : Task =
                    this.JSRuntime
                        .InvokeVoidAsync("touchstoneInterop.renderChart", divId, GenericChart.toFigureJson chart)
                        .AsTask()

                do! render "chart-magnitude" (magnitudeQuadMulti ok)
                do! render "chart-phase" (phaseChartMulti ok)

                match smithChartMulti ok with
                | Some chart -> do! render "chart-smith" chart
                | None -> ()
            elif key = "" then
                lastRenderedKey <- None
        }
        :> Task

[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    builder.RootComponents.Add<App>("#app")
    builder.Build().RunAsync() |> ignore
    0
