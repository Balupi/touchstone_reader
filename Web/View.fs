/// Bolero view for the Touchstone web app.
module TouchstoneReader.Web.View

open System
open System.Globalization
open Bolero
open Bolero.Html
open Elmish
open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot
open TouchstoneReader.Web.State

/// Range-input `.Value` is always a plain "."-decimal string per the HTML
/// spec, regardless of page locale — parse/format with InvariantCulture
/// rather than F#'s `string`/`float`, which pick up the current culture and
/// would emit e.g. "1,5" under a German locale, silently breaking the
/// round-trip back into the input's `value` attribute.
let private parseInvariant (v: obj) =
    Double.Parse(string v, CultureInfo.InvariantCulture)

/// Like parseInvariant, but for the free-typed number inputs (frequency
/// bounds): unlike a range input's value, what's typed there can be blank
/// or not-yet-a-number mid-edit, so this doesn't throw on that.
let private tryParseInvariant (v: obj) =
    match Double.TryParse(string v, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, x -> Some x
    | false, _ -> None

let private formatInvariant (v: float) = v.ToString(CultureInfo.InvariantCulture)

/// Rounded to the same precision the frequency-range UI displays, so a
/// typed value and the number it's echoed back as always agree.
let private formatGHz (v: float) = formatInvariant (Math.Round(v, 3))

/// The actual data point (Hz, converted to GHz) closest to `targetGHz` —
/// used so typing a frequency-range bound snaps to a real, plottable point
/// on that file's sweep instead of an arbitrary value nothing lives at.
let private snapToNearestFreqGHz (data: TouchstoneFile) (targetGHz: float) =
    let targetHz = targetGHz * 1e9
    (data.Frequencies |> Array.minBy (fun f -> abs (f - targetHz))) / 1e9

let private fileSummaryTags (data: TouchstoneFile) =
    [ sprintf "%d-port" data.Ports
      sprintf "%d pts" data.Frequencies.Length
      sprintf "%A" data.Option.Parameter
      sprintf "%A" data.Option.Format
      sprintf "R=%.0f Ω" data.Option.R ]

/// A small filled circle in the file's plot color (see TouchstonePlot.fileColor),
/// so the color used for a file's traces on every chart is identifiable at a
/// glance in the file list too, without needing to check the legend.
let private colorSwatch (fileName: string) =
    span {
        attr.style (
            sprintf
                "display:inline-block; width:0.8em; height:0.8em; border-radius:50%%; background-color:%s; margin-right:0.5rem; flex-shrink:0;"
                (fileColor fileName)
        )
    }

/// The union of every loaded (successfully parsed) file's own native
/// frequency range — the shared slider bounds while ranges are linked, so a
/// linked slider can reach every loaded file's full extent rather than
/// being capped at whichever file happens to be narrowest.
let private unionFreqRangeGHz (files: LoadedFile list) =
    let bounds =
        files
        |> List.choose (fun f ->
            match f.Data with
            | Ok data when data.Frequencies.Length > 0 ->
                Some(data.Frequencies.[0] / 1e9, data.Frequencies.[data.Frequencies.Length - 1] / 1e9)
            | _ -> None)

    match bounds with
    | [] -> None
    | _ -> Some(bounds |> List.map fst |> List.min, bounds |> List.map snd |> List.max)

/// Per-file frequency-range crop, as one slider with two handles: two native
/// range inputs stacked exactly on top of each other (`.range-dual` in
/// index.html hides each input's own track and touch-target, leaving only
/// its thumb interactive, so the overlap doesn't block either handle from
/// being dragged). Bounded by the file's actual sweep (or, while ranges are
/// linked, the union of every loaded file's sweep — see unionFreqRangeGHz),
/// defaulting to the full range. Changes commit on release (`on.change`, not
/// `on.input`) since every change re-downsamples and re-renders every chart
/// this file appears in — doing that on every drag tick would be janky.
/// `update` (State.fs) clamps lo/hi so the handles can't cross, and fans a
/// linked change out to every file.
let private freqRangeSlider
    (dispatch: Dispatch<Message>)
    (fileName: string)
    (data: TouchstoneFile)
    (range: (float * float) option)
    (gen: int)
    (linked: bool)
    (unionRange: (float * float) option)
    =
    if data.Frequencies.Length < 2 then
        empty ()
    else
        let ownLo = data.Frequencies.[0] / 1e9
        let ownHi = data.Frequencies.[data.Frequencies.Length - 1] / 1e9
        let fullLo, fullHi = if linked then defaultArg unionRange (ownLo, ownHi) else (ownLo, ownHi)
        // Clamped to the *current* slider bounds: a stored range picked up
        // while linked to a wider union can exceed a narrower file's own
        // bounds once unlinked again. Cosmetic only — windowed() (State.fs)
        // already intersects with the file's actual data regardless, so
        // this just keeps the label/thumbs from showing a value the slider
        // itself can't reach.
        let lo, hi = defaultArg range (fullLo, fullHi)
        let lo, hi = max lo fullLo |> min fullHi, max hi fullLo |> min fullHi
        let step = formatInvariant ((fullHi - fullLo) / 500.0)
        let pct v = (v - fullLo) / (fullHi - fullLo) * 100.0

        div {
            attr.``class`` "mt-3"

            div {
                attr.``class`` "is-flex is-align-items-center is-justify-content-space-between"

                div {
                    attr.``class`` "is-flex is-align-items-center"

                    p {
                        attr.``class`` "mr-2 is-size-7 has-text-grey"
                        "Frequenzbereich:"
                    }

                    input {
                        attr.key (sprintf "%s-lo-%d" fileName gen)
                        attr.``class`` "input is-small"
                        attr.style "width: 5rem;"
                        attr.``type`` "number"
                        attr.step "any"
                        attr.value (formatGHz lo)

                        on.change (fun e ->
                            match tryParseInvariant e.Value with
                            | Some typed -> dispatch (SetFreqRange(fileName, snapToNearestFreqGHz data typed, hi))
                            | None -> ())
                    }

                    p {
                        attr.``class`` "mx-2 is-size-7 has-text-grey"
                        "–"
                    }

                    input {
                        attr.key (sprintf "%s-hi-%d" fileName gen)
                        attr.``class`` "input is-small"
                        attr.style "width: 5rem;"
                        attr.``type`` "number"
                        attr.step "any"
                        attr.value (formatGHz hi)

                        on.change (fun e ->
                            match tryParseInvariant e.Value with
                            | Some typed -> dispatch (SetFreqRange(fileName, lo, snapToNearestFreqGHz data typed))
                            | None -> ())
                    }

                    p {
                        attr.``class`` "ml-2 is-size-7 has-text-grey"
                        "GHz"
                    }
                }

                if range.IsSome then
                    button {
                        attr.``class`` "button is-white is-size-7 p-0"
                        attr.style "height: auto; border: none;"
                        on.click (fun _ -> dispatch (ResetFreqRange fileName))
                        "Zurücksetzen"
                    }
            }

            div {
                attr.``class`` "range-dual-wrap"

                div { attr.``class`` "range-dual-track" }

                div {
                    attr.``class`` "range-dual-selected"
                    attr.style (sprintf "left: %.3f%%; width: %.3f%%;" (pct lo) (pct hi - pct lo))
                }

                input {
                    attr.``class`` "range-dual"
                    attr.``type`` "range"
                    attr.min (formatInvariant fullLo)
                    attr.max (formatInvariant fullHi)
                    attr.step step
                    attr.value (formatInvariant lo)
                    on.change (fun e -> dispatch (SetFreqRange(fileName, parseInvariant e.Value, hi)))
                }

                input {
                    attr.``class`` "range-dual"
                    attr.``type`` "range"
                    attr.min (formatInvariant fullLo)
                    attr.max (formatInvariant fullHi)
                    attr.step step
                    attr.value (formatInvariant hi)
                    on.change (fun e -> dispatch (SetFreqRange(fileName, lo, parseInvariant e.Value)))
                }
            }
        }

let private fileTag
    (dispatch: Dispatch<Message>)
    (linked: bool)
    (unionRange: (float * float) option)
    (f: LoadedFile)
    =
    let deleteButton =
        button {
            attr.``class`` "delete"
            on.click (fun _ -> dispatch (RemoveFile f.FileName))
        }

    match f.Data with
    | Ok data ->
        div {
            attr.``class`` "box mt-2 py-2 px-3"

            div {
                attr.``class`` "is-flex is-align-items-center"
                colorSwatch f.FileName

                span {
                    attr.``class`` "has-text-weight-semibold mr-auto"
                    f.FileName
                }

                deleteButton
            }

            // Collapsed by default so a list of several files stays compact;
            // the per-file metadata and header comments are a click away
            // instead of always taking up a whole notification block each.
            details {
                summary {
                    attr.``class`` "has-text-grey is-size-7 mt-1"
                    attr.style "cursor: pointer;"
                    "Details"
                }

                div {
                    attr.``class`` "tags mt-2 mb-0"

                    for t in fileSummaryTags data do
                        span {
                            attr.``class`` "tag"
                            t
                        }
                }

                freqRangeSlider dispatch f.FileName data f.FreqRangeGHz f.FreqRangeGen linked unionRange

                if not data.Comments.IsEmpty then
                    div {
                        attr.``class`` "content is-small mt-2 mb-0"

                        for c in data.Comments do
                            p {
                                attr.``class`` "mb-1"
                                c
                            }
                    }
            }
        }
    | Error msg ->
        article {
            attr.``class`` "message is-danger mt-2"

            div {
                attr.``class`` "message-header"
                p { f.FileName }
                deleteButton
            }

            div {
                attr.``class`` "message-body"
                msg
            }
        }

let private paramToggle (dispatch: Dispatch<Message>) (chart: ChartKind) (selected: Set<int * int>) (i, j) =
    let isOn = selected.Contains(i, j)

    button {
        attr.``class`` (if isOn then "button is-small is-info mr-2" else "button is-small mr-2")
        on.click (fun _ -> dispatch (ToggleParam(chart, i, j)))
        sprintf "S%d%d" i j
    }

/// Downloads the chart's underlying (non-downsampled) data as a CSV, via the
/// figure JSON interop.js already cached for the modebar's own CSV button —
/// this is a second, always-visible entry point to the same download since
/// the modebar only reveals itself on hover and easily goes unnoticed among
/// its other icons.
let private csvDownloadButton (divId: string) =
    button {
        attr.``class`` "button is-small is-light ml-auto"
        attr.id (sprintf "csv-%s" divId)
        attr.title "Rohdaten (nicht downgesampelt) als CSV herunterladen"
        "⬇ CSV"
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

            csvDownloadButton divId
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

/// On/off switch for a chart section's min/max reference lines. Each of
/// Magnitude and Group Delay gets its own instance with independent state,
/// embedded in that section (via `chartSection`'s `extraControls`) rather
/// than a single shared control elsewhere on the page. A plain toggle
/// switch (`.switch` in index.html) rather than an An/Aus button pair.
let private extremaToggle (dispatch: Dispatch<Message>) (chart: ChartKind) (show: bool) =
    div {
        attr.``class`` "field is-grouped is-align-items-center mb-3"

        p {
            attr.``class`` "mr-2 has-text-grey"
            "Min/Max-Markierungen:"
        }

        label {
            attr.``class`` "switch"

            input {
                attr.``type`` "checkbox"
                attr.``checked`` show
                on.click (fun _ -> dispatch (SetShowExtrema(chart, not show)))
            }

            span {
                attr.``class`` "switch-track"
                span { attr.``class`` "switch-thumb" }
            }
        }
    }

/// Checked, dragging any one file's frequency-range slider moves every
/// file's slider along with it (to the same GHz bounds); unchecked, each
/// slider moves independently. Only shown once there's more than one file
/// to actually link. Default checked (State.fs's initModel).
let private linkFreqRangesToggle (dispatch: Dispatch<Message>) (linked: bool) =
    label {
        attr.``class`` "checkbox is-size-7 has-text-grey mb-2 is-flex is-align-items-center"

        input {
            attr.``type`` "checkbox"
            attr.``class`` "mr-2"
            attr.``checked`` linked
            on.click (fun _ -> dispatch (SetLinkFreqRanges(not linked)))
        }

        "Frequenzbereich-Slider aller Dateien verknüpfen"
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
        div {
            attr.``class`` "mb-2"
            attr.style (if model.Status.IsSome then "" else "display: none")

            p {
                attr.``class`` "has-text-warning mb-1"
                sprintf "⏳ %s" (defaultArg model.Status "")
            }

            // No `value` attribute: native <progress> renders indeterminate,
            // and Bulma animates that state with a moving stripe.
            progress {
                attr.``class`` "progress is-warning"
                attr.style "height: 4px;"
                attr.max "100"
            }
        }

        // Bulma's own file-upload component (file/file-label/file-cta) rather
        // than a hand-rolled box+label; the large dashed dropzone look is
        // layered on top via inline style since is-boxed alone is sized more
        // like a button.
        div {
            attr.id "drop-zone"
            attr.``class`` "file is-boxed"

            attr.style
                "border: 2px dashed #999; padding: 2rem; width: 100%; display: flex; justify-content: center; cursor: pointer; transition: border-color 0.15s ease, background-color 0.15s ease;"

            label {
                attr.``class`` "file-label"
                attr.style "cursor: pointer; align-items: center;"

                input {
                    attr.id "file-input"
                    attr.``class`` "file-input"
                    attr.``type`` "file"
                    attr.multiple true
                }

                span {
                    attr.``class`` "file-cta"
                    attr.style "border: none; background: none;"

                    span {
                        attr.``class`` "file-icon is-size-1"
                        "📤"
                    }

                    span {
                        attr.``class`` "file-label is-size-5"
                        "Drag & drop Touchstone files here, or click to browse"
                    }
                }
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

                let okCount = model.Files |> List.filter (fun f -> match f.Data with Ok _ -> true | Error _ -> false) |> List.length

                if okCount >= 2 then
                    linkFreqRangesToggle dispatch model.LinkFreqRanges

                let unionRange = unionFreqRangeGHz model.Files

                for f in model.Files do
                    fileTag dispatch model.LinkFreqRanges unionRange f

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
                                    (extremaToggle dispatch MagnitudeChart model.ShowMagnitudeExtrema)
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
                                (concat {
                                    groupDelayModeToggle dispatch model.GroupDelayMode comparableCount
                                    extremaToggle dispatch GroupDelayChart model.ShowGroupDelayExtrema
                                })
                                ""
                                "chart-group-delay"
                    }
            }
    }
