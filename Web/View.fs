/// Bolero view for the Touchstone web app.
module TouchstoneReader.Web.View

open Bolero
open Bolero.Html
open Elmish
open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot
open TouchstoneReader.Web.State

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
/// hidden renders at 0 size and never fixes itself. `extraControls` renders
/// between the toggle row and the chart (e.g. Group Delay's absolute/
/// deviation switch); pass `empty ()` for none.
let private chartSection
    (dispatch: Dispatch<Message>)
    (title: string)
    (isOpenByDefault: bool)
    (chart: ChartKind)
    (order: (int * int) list)
    (selected: Set<int * int>)
    (extraControls: Node)
    (divStyle: string)
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

        extraControls

        if selected.IsEmpty then
            p {
                attr.``class`` "has-text-grey"
                "Select at least one parameter above."
            }
        else
            div {
                attr.id divId
                attr.style divStyle
            }
    }

/// Absolute-vs-deviation-from-mean switch for the Group Delay section; only
/// meaningful (and shown) once 2+ files are actually comparable. Styled as a
/// single connected (`has-addons`) segmented control behind a "Display:"
/// label, so it doesn't read as more S-parameter toggle buttons like the
/// individually-clickable ones above it.
let private groupDelayModeToggle (dispatch: Dispatch<Message>) (mode: DisplayMode) (comparableFileCount: int) =
    if comparableFileCount < 2 then
        empty ()
    else
        div {
            attr.``class`` "field is-grouped is-align-items-center mb-3"

            p {
                attr.``class`` "mr-2 has-text-grey"
                "Display:"
            }

            div {
                attr.``class`` "buttons has-addons mb-0"

                button {
                    attr.``class`` (if mode = Absolute then "button is-small is-info is-selected" else "button is-small")
                    on.click (fun _ -> dispatch (SetGroupDelayMode Absolute))
                    "Absolute"
                }

                button {
                    attr.``class``
                        (if mode = DeviationFromMean then
                             "button is-small is-info is-selected"
                         else
                             "button is-small")

                    on.click (fun _ -> dispatch (SetGroupDelayMode DeviationFromMean))
                    "Δ from mean"
                }
            }
        }

let renderView (model: Model) (dispatch: Dispatch<Message>) =
    div {
        attr.``class`` "container mt-5 px-4"
        // Wider than Bulma's default breakpoint-capped container (960px):
        // the magnitude/phase quad grid alone is already 900px, leaving
        // little room to breathe. Still centers and shrinks fine below this.
        attr.style "max-width: 1400px;"

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

            attr.style
                "border: 2px dashed #999; padding: 3rem; cursor: pointer; display: block; transition: border-color 0.15s ease, background-color 0.15s ease;"

            p {
                attr.``class`` "is-size-1 mb-2"
                "📤"
            }

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
                                    (empty ())
                                    ""
                                    "chart-magnitude"

                                chartSection
                                    dispatch
                                    "Phase (deg)"
                                    false
                                    PhaseChart
                                    magnitudeQuadOrder
                                    model.PhaseSelected
                                    (empty ())
                                    ""
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
                                (empty ())
                                "max-width: 700px; margin: 0 auto;"
                                "chart-smith"

                        if has2Port then
                            let comparableCount = ok |> List.filter (fun (_, data) -> data.Ports = 2) |> List.length

                            chartSection
                                dispatch
                                "Group Delay (ns)"
                                false
                                GroupDelayChart
                                groupDelayOrder
                                model.GroupDelaySelected
                                (groupDelayModeToggle dispatch model.GroupDelayMode comparableCount)
                                ""
                                "chart-group-delay"
                    }
            }
    }
