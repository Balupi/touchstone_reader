/// Bolero view for the Touchstone web app.
module TouchstoneReader.Web.View

open System
open System.Globalization
open Bolero
open Bolero.Html
open Elmish
open TouchstoneReader.Touchstone
open TouchstoneReader.LimitTest
open TouchstoneReader.PortMap
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

/// Same rounding as formatGHz, for the TDR gate UI's ns values.
let private formatNs (v: float) = formatInvariant (Math.Round(v, 3))

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
/// Clickable: interop.js's setupCollapsibleCharts binds a plain click
/// handler (matched by this element's id) that hides/shows every trace for
/// this file across every chart, fading the swatch to indicate the hidden
/// state — the same underlying Plotly per-trace visibility toggle a click
/// on the file's legend entry triggers, just reachable from the file list
/// too. Pure client-side (Plotly `visible` state), not round-tripped
/// through Elmish — same reason the CSV download button works the same way.
let private colorSwatch (fileName: string) =
    span {
        attr.id (sprintf "swatch-%s" (Uri.EscapeDataString fileName))
        attr.title "Click to show/hide this file's curves"

        attr.style (
            sprintf
                "display:inline-block; width:0.8em; height:0.8em; border-radius:50%%; background-color:%s; margin-right:0.5rem; flex-shrink:0; cursor:pointer;"
                (fileColor fileName)
        )
    }

/// The union of every *linked* file's own native frequency range — the
/// shared slider bounds for a file that's part of the linked group, so a
/// linked slider can reach every other linked file's full extent rather
/// than being capped at whichever one happens to be narrowest. Files that
/// have opted out of linking don't contribute to (or use) this union.
let private unionFreqRangeGHz (files: LoadedFile list) =
    let bounds =
        files
        |> List.choose (fun f ->
            match f.Data with
            | Ok data when f.FreqRangeLinked && data.Frequencies.Length > 0 ->
                Some(data.Frequencies.[0] / 1e9, data.Frequencies.[data.Frequencies.Length - 1] / 1e9)
            | _ -> None)

    match bounds with
    | [] -> None
    | _ -> Some(bounds |> List.map fst |> List.min, bounds |> List.map snd |> List.max)

/// Per-file frequency-range crop, as one slider with two handles: two native
/// range inputs stacked exactly on top of each other (`.range-dual` in
/// index.html hides each input's own track and touch-target, leaving only
/// its thumb interactive, so the overlap doesn't block either handle from
/// being dragged). Bounded by the file's actual sweep (or, while this file
/// is part of the linked group, the union of every other linked file's
/// sweep — see unionFreqRangeGHz), defaulting to the full range. Changes
/// commit on release (`on.change`, not `on.input`) since every change
/// re-downsamples and re-renders every chart this file appears in — doing
/// that on every drag tick would be janky. `update` (State.fs) clamps
/// lo/hi so the handles can't cross, and fans a linked change out to every
/// other linked file.
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
                        "Frequency range:"
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
                        "Reset"
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

/// Checked, this file's frequency-range slider is part of the linked group
/// (see unionFreqRangeGHz / SetFreqRange in State.fs); unchecked, it moves
/// independently. Lives right next to the slider it controls, in the same
/// Details section, rather than a single global switch elsewhere on the
/// page with no visible slider to explain what it's linking. Only shown
/// once there's more than one file to actually link with.
let private freqRangeLinkToggle (dispatch: Dispatch<Message>) (fileName: string) (linked: bool) =
    label {
        attr.``class`` "checkbox is-size-7 has-text-grey is-flex is-align-items-center mt-2"

        input {
            attr.``type`` "checkbox"
            attr.``class`` "mr-2"
            attr.``checked`` linked
            on.click (fun _ -> dispatch (SetFreqRangeLinked(fileName, not linked)))
        }

        "Link range with other files"
    }

/// PASS/FAIL for one file across every parameter that has an active limit
/// table, or None when nothing is tested — an empty spec shows no badge
/// rather than a green one, so "not checked" can't be read as "passed".
/// Evaluated on the same windowed series the chart draws (it's handed the
/// data from okFiles), so badge and red markers can't disagree.
let private verdictOf (model: Model) (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)

    let outcomes =
        model.LimitTables
        |> Map.toList
        |> List.filter (fun ((i, j), _) -> i <= data.Ports && j <= data.Ports)
        |> List.map (fun ((i, j), segments) ->
            let ys = entry data i j |> Array.map (fun c -> 20.0 * log10 c.Magnitude)
            evaluate segments freqGHz ys)
        |> List.filter (fun outcome -> outcome.Tested)

    if outcomes.IsEmpty then
        None
    else
        Some(outcomes |> List.forall passed)

/// Every file's verdict, memoized. The badges are read on every Blazor
/// render, but they only change when the files, their frequency windows or
/// the tables do — and computing one means a dB pass plus a limit pass over
/// every point of every file: measured at roughly 0.6 ms per file and table
/// for an 11,000-point sweep natively, so ~12 ms for ten files against two
/// tables, and the WASM interpreter multiplies that. Renders happen far more
/// often than those inputs change — every status dispatch causes one — so
/// the result is kept against a signature covering exactly those inputs.
/// The windowed data carries its own range in its first/last point, so no
/// separate generation counter is needed here.
let mutable private verdictSignature = ""
let mutable private verdictCache: Map<string, bool> = Map.empty

let private limitVerdicts (model: Model) (ok: (string * TouchstoneFile) list) =
    let sweepOf (name: string, data: TouchstoneFile) =
        let n = data.Frequencies.Length
        let lo = if n > 0 then data.Frequencies.[0] else 0.0
        let hi = if n > 0 then data.Frequencies.[n - 1] else 0.0
        sprintf "%s@%d:%g-%g" name n lo hi

    let signature =
        String.Join(
            "##",
            [| ok |> List.map sweepOf |> String.concat "|"
               serialize (Map.toList model.LimitTables)
               model.HiddenFiles |> Set.toList |> String.concat "," |]
        )

    if signature <> verdictSignature then
        verdictSignature <- signature

        verdictCache <-
            ok
            // Hidden files are skipped on purpose: a curve that isn't on
            // screen isn't being judged, so leaving a stale FAIL beside it
            // would claim more than the display is showing.
            |> List.filter (fun (name, _) -> not (model.HiddenFiles.Contains name))
            |> List.choose (fun (name, data) -> verdictOf model data |> Option.map (fun v -> name, v))
            |> Map.ofList

    verdictCache

/// One row of a limit table, in the instrument's column order: TYPE, begin
/// and end stimulus (GHz), begin and end response (dB). TYPE is a button
/// group rather than the instrument's dropdown — one click instead of two,
/// and it keeps the row to widgets this view already uses elsewhere.
let private limitRow (dispatch: Dispatch<Message>) (i: int) (j: int) (index: int) (segment: LimitSegment) =
    let numberCell (value: float) (withValue: float -> LimitSegment) =
        td {
            input {
                attr.``class`` "input is-small"
                attr.style "width: 6rem;"
                attr.``type`` "number"
                attr.step "any"
                attr.value (formatInvariant value)

                on.change (fun e ->
                    match tryParseInvariant e.Value with
                    | Some typed -> dispatch (SetLimitSegment(i, j, index, withValue typed))
                    | None -> ())
            }
        }

    tr {
        td {
            div {
                attr.``class`` "buttons has-addons mb-0"

                for kind in [ Max; Min; Off ] do
                    button {
                        attr.``class`` (
                            if segment.Kind = kind then
                                "button is-small is-primary is-selected"
                            else
                                "button is-small"
                        )

                        on.click (fun _ -> dispatch (SetLimitSegment(i, j, index, { segment with Kind = kind })))
                        kindText kind
                    }
            }
        }

        numberCell segment.BeginStimulus (fun v -> { segment with BeginStimulus = v })
        numberCell segment.EndStimulus (fun v -> { segment with EndStimulus = v })
        numberCell segment.BeginResponse (fun v -> { segment with BeginResponse = v })
        numberCell segment.EndResponse (fun v -> { segment with EndResponse = v })

        td {
            button {
                attr.``class`` "delete"
                attr.title "Remove this row"
                on.click (fun _ -> dispatch (RemoveLimitSegment(i, j, index)))
            }
        }
    }

/// One parameter's limit table.
let private limitTable (dispatch: Dispatch<Message>) (model: Model) ((i, j): int * int) =
    let rows = model.LimitTables |> Map.tryFind (i, j) |> Option.defaultValue []

    div {
        attr.``class`` "mt-4"

        div {
            attr.``class`` "is-flex is-align-items-center mb-2"

            span {
                attr.``class`` "has-text-weight-semibold mr-3"
                paramName (i, j)
            }

            button {
                attr.``class`` "button is-small mr-2"
                on.click (fun _ -> dispatch (AddLimitSegment(i, j)))
                "Add segment"
            }

            if rows.IsEmpty then
                empty ()
            else
                button {
                    attr.``class`` "button is-small is-light"
                    on.click (fun _ -> dispatch (ClearLimitTable(i, j)))
                    "Clear"
                }
        }

        if rows.IsEmpty then
            p {
                attr.``class`` "has-text-grey is-size-7"
                "No limits — this parameter isn't tested."
            }
        else
            table {
                attr.``class`` "table is-narrow is-fullwidth is-size-7 mb-0"

                thead {
                    tr {
                        th { "Type" }
                        th { "Begin stimulus (GHz)" }
                        th { "End stimulus (GHz)" }
                        th { "Begin response (dB)" }
                        th { "End response (dB)" }
                        th { "" }
                    }
                }

                tbody {
                    for (index, segment) in List.indexed rows do
                        limitRow dispatch i j index segment
                }
            }
    }

/// The limit tables, as a collapsed sub-section of the magnitude chart: one
/// table per *selected* parameter, since those are the subplots on screen,
/// and the instrument keeps limits per trace too. Collapsed by default —
/// entering a spec is a setup step, not something to look at while reading
/// curves.
let private limitSubSection (dispatch: Dispatch<Message>) (model: Model) =
    let active =
        model.LimitTables
        |> Map.toList
        |> List.filter (fun (_, rows) -> rows |> List.exists (fun s -> s.Kind <> Off))

    let activeLabel =
        active |> List.map (fun (p, _) -> paramName p) |> String.concat ", "

    // Shown in the summary, which stays visible while the section is
    // collapsed — a spec is only safe to leave running if you can see that
    // it is. Restored tables say so explicitly: they were written for
    // whatever files were loaded last time, not necessarily these.
    let statusTag =
        if active.IsEmpty then
            empty ()
        elif model.LimitsRestored then
            span {
                attr.``class`` "tag is-warning is-light ml-3"
                sprintf "restored from storage: %s" activeLabel
            }
        else
            span {
                attr.``class`` "tag is-info is-light ml-3"
                sprintf "active: %s" activeLabel
            }

    let restoredNotice =
        if model.LimitsRestored && not active.IsEmpty then
            div {
                attr.``class`` "notification is-warning is-light py-2 px-3 mt-2 is-flex is-align-items-center"

                span {
                    attr.``class`` "is-size-7 mr-auto"
                    "Restored from your last session and applied to every loaded file. Editing any row clears this note."
                }

                button {
                    attr.``class`` "button is-small"
                    on.click (fun _ -> dispatch ClearAllLimitTables)
                    "Discard"
                }
            }
        else
            empty ()

    details {
        attr.``class`` "chart-section mt-4"

        summary {
            attr.``class`` "title is-6"
            attr.style "cursor: pointer;"
            "Limit Lines"
            statusTag
        }

        restoredNotice

        p {
            attr.``class`` "has-text-grey is-size-7 mt-2"
            "MAX fails points above the line, MIN below it. A segment only tests the frequencies it spans, and a table that is empty or all OFF tests nothing."
        }

        for pij in paramOrderFor model MagnitudeChart |> List.filter model.MagnitudeSelected.Contains do
            limitTable dispatch model pij
    }

/// Per-file port-layout switch, shown only for files that need one (see
/// PortMap.needsLayout): a 1- or 2-port file has nothing to lay out or no
/// ambiguity about it, and an odd port count has no second end to lay
/// anything across. Starts with neither option selected on purpose —
/// see Model.PortLayouts. The through paths in each label are what make the
/// choice checkable against the actual measurement setup.
let private portLayoutPicker
    (dispatch: Dispatch<Message>)
    (fileName: string)
    (ports: int)
    (layout: PortLayout option)
    =
    let optionButton (l: PortLayout) =
        let isOn = layout = Some l

        button {
            attr.``class`` (if isOn then "button is-small is-info mr-2" else "button is-small mr-2")
            attr.title (layoutDescription l)
            on.click (fun _ -> dispatch (SetPortLayout(fileName, l)))
            layoutLabel l
        }

    let hint =
        match layout with
        | Some l ->
            let through = throughParams l ports |> List.map paramName |> String.concat ", "

            span {
                attr.``class`` "has-text-grey is-size-7"
                sprintf "through: %s" through
            }
        | None ->
            span {
                attr.``class`` "tag is-warning is-light"
                "not set — Through/NEXT/FEXT and Group Delay stay unavailable"
            }

    div {
        attr.``class`` "field mt-3 mb-0"

        label {
            attr.``class`` "label is-small mb-1"
            sprintf "Port layout (%d ports)" ports
        }

        div {
            attr.``class`` "is-flex is-align-items-center is-flex-wrap-wrap"
            optionButton EndsSplit
            optionButton AdjacentPairs
            hint
        }
    }

let private fileTag
    (dispatch: Dispatch<Message>)
    (okCount: int)
    (unionRange: (float * float) option)
    (verdict: bool option)
    (layout: PortLayout option)
    (f: LoadedFile)
    =
    let deleteButton =
        button {
            attr.``class`` "delete"
            on.click (fun _ -> dispatch (RemoveFile f.FileName))
        }

    let verdictBadge =
        match verdict with
        | Some true ->
            span {
                attr.``class`` "tag is-success is-light mr-2"
                "PASS"
            }
        | Some false ->
            span {
                attr.``class`` "tag is-danger mr-2"
                "FAIL"
            }
        | None -> empty ()

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

                // Visible with Details collapsed: an unstated layout silently
                // withholds half the charts, so it can't hide behind a click.
                if needsLayout data.Ports && layout.IsNone then
                    span {
                        attr.``class`` "tag is-warning is-light mr-2"
                        "port layout?"
                    }
                else
                    empty ()

                verdictBadge
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

                if needsLayout data.Ports then
                    portLayoutPicker dispatch f.FileName data.Ports layout
                else
                    empty ()

                freqRangeSlider dispatch f.FileName data f.FreqRangeGHz f.FreqRangeGen f.FreqRangeLinked unionRange

                if okCount >= 2 then
                    freqRangeLinkToggle dispatch f.FileName f.FreqRangeLinked

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
        paramName (i, j)
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

/// One cell of the full matrix: same toggle, but labelled with just the
/// indices and sized to keep an 8x8 grid compact. The tooltip carries what
/// the parameter means under the stated port layout, which is the part that
/// isn't obvious from "62".
let private matrixCell
    (dispatch: Dispatch<Message>)
    (chart: ChartKind)
    (selected: Set<int * int>)
    (title: string)
    (i, j)
    =
    let isOn = selected.Contains(i, j)

    button {
        attr.``class`` (if isOn then "button is-small is-info" else "button is-small is-light")
        attr.style "padding: 0 0.4em; height: 1.75em;"
        attr.title title
        on.click (fun _ -> dispatch (ToggleParam(chart, i, j)))
        indexPair (i, j)
    }

/// Switches every parameter of one group that this section offers. Hidden
/// when the group is empty here: a 2-port file has no crosstalk, a
/// diagonal-only section has nothing but reflections, and before a >2-port
/// file's layout has been stated only Reflection is defined at all (see
/// State.groupParamsFor).
let private groupButton
    (dispatch: Dispatch<Message>)
    (model: Model)
    (chart: ChartKind)
    (selected: Set<int * int>)
    (group: ParamGroup)
    =
    let members = groupParamsFor model chart group

    if members.IsEmpty then
        empty ()
    else
        let allOn = members |> List.forall selected.Contains
        let anyOn = members |> List.exists selected.Contains

        button {
            attr.``class`` (
                if allOn then "button is-small is-info mr-2"
                elif anyOn then "button is-small is-info is-light mr-2"
                else "button is-small mr-2"
            )
            attr.title (
                sprintf "%s — %s" (groupDescription group) (members |> List.map paramName |> String.concat ", ")
            )
            on.click (fun _ -> dispatch (ToggleParamGroup(chart, group)))
            groupLabel group
        }

/// The full N x N parameter matrix, rows = response port i, columns =
/// stimulus port j — the same orientation as the instrument's own matrix.
/// Cells this section doesn't offer (everything off the diagonal, in a
/// reflection-only section) are dots rather than dead buttons.
let private paramMatrix
    (dispatch: Dispatch<Message>)
    (model: Model)
    (chart: ChartKind)
    (order: (int * int) list)
    (selected: Set<int * int>)
    =
    let ports = pickerPorts model
    let offered = Set.ofList order
    let layout = pickerLayout model

    // Deliberately not a match nested inside a match: an inner `match` with
    // outer cases after it silently absorbs them as its own (they typecheck,
    // being options either way), leaving the outer match incomplete and this
    // throwing exactly when the layout is unstated - the case that matters.
    let cellTitle (i, j) =
        let group =
            match layout with
            | Some l -> groupOf l ports (i, j)
            // The diagonal is a reflection under either layout, so it can be
            // named before anything has been stated about the ports.
            | None -> if i = j then Some Reflection else None

        match group with
        | Some g -> sprintf "%s — %s" (paramName (i, j)) (groupLabel g)
        | None when layout.IsNone -> sprintf "%s — port layout not stated" (paramName (i, j))
        | None -> sprintf "%s — not classifiable at this port count" (paramName (i, j))

    div {
        attr.``class`` "mb-3"
        attr.style "overflow-x: auto;"

        table {
            attr.``class`` "table is-narrow is-bordered is-size-7 mb-0"

            thead {
                tr {
                    th {
                        attr.``class`` "has-text-grey has-text-weight-normal"
                        "i \\ j"
                    }

                    for j in 1..ports do
                        th {
                            attr.``class`` "has-text-centered has-text-grey has-text-weight-normal"
                            string j
                        }
                }
            }

            tbody {
                for i in 1..ports do
                    tr {
                        th {
                            attr.``class`` "has-text-grey has-text-weight-normal"
                            string i
                        }

                        for j in 1..ports do
                            td {
                                attr.``class`` "p-1 has-text-centered"

                                if offered.Contains(i, j) then
                                    matrixCell dispatch chart selected (cellTitle (i, j)) (i, j)
                                else
                                    span {
                                        attr.``class`` "has-text-grey-light"
                                        "·"
                                    }
                            }
                    }
            }
        }
    }

/// A section's parameter picker. Up to 8 parameters (any 1- or 2-port file,
/// and every reflection-only section up to 8 ports) it stays the flat row of
/// S-parameter buttons it always was. Past that — a 4-port file's 16
/// parameters, an 8-port file's 64 — a flat row is not a picker, so it
/// becomes group buttons plus the current selection as removable chips, with
/// the full matrix behind a toggle.
let private paramPicker
    (dispatch: Dispatch<Message>)
    (model: Model)
    (chart: ChartKind)
    (order: (int * int) list)
    (selected: Set<int * int>)
    (active: (int * int) list)
    (divId: string)
    =
    if order.Length <= 8 then
        div {
            attr.``class`` "field is-grouped is-flex-wrap-wrap mb-3"

            for pij in order do
                paramToggle dispatch chart selected pij

            csvDownloadButton divId
        }
    else
        let matrixOpen = model.MatrixOpen.Contains chart
        let matrixLabel = if matrixOpen then "Matrix ▴" else "Matrix ▾"

        let selectionChips =
            if active.IsEmpty then
                span {
                    attr.``class`` "has-text-grey is-size-7 mr-3"
                    "nothing selected"
                }
            else
                concat {
                    for pij in active do
                        paramToggle dispatch chart selected pij
                }

        concat {
            div {
                attr.``class`` "is-flex is-align-items-center is-flex-wrap-wrap mb-2"

                for g in allGroups do
                    groupButton dispatch model chart selected g

                button {
                    attr.``class`` (
                        if matrixOpen then
                            "button is-small is-link is-light mr-2"
                        else
                            "button is-small is-light mr-2"
                    )
                    attr.title (sprintf "All %d parameters of the matrix" order.Length)
                    on.click (fun _ -> dispatch (ToggleMatrix chart))
                    matrixLabel
                }

                csvDownloadButton divId
            }

            div {
                attr.``class`` "is-flex is-align-items-center is-flex-wrap-wrap mb-3"
                selectionChips
            }

            if matrixOpen then
                paramMatrix dispatch model chart order selected
            else
                empty ()
        }

/// A collapsible <details> section with its own parameter picker and chart
/// container. Which parameters it offers and which are selected both come
/// from the model via the ChartKind (see State.paramOrderFor /
/// State.selectionFor), so a section never carries its own hardcoded port
/// assumptions. `open`'s toggle event doesn't rebuild the Plotly chart, so
/// interop.js resizes it on expand — otherwise a chart drawn while hidden
/// renders at 0 size and never fixes itself. `extraControls` renders between
/// the picker and the chart (e.g. Group Delay's absolute/deviation switch);
/// `nested` renders after the chart (e.g. TDR's Gated Magnitude sub-section,
/// sharing this section's own parameter selection rather than duplicating a
/// picker for the same S11/S22); pass `empty ()` for either when not needed.
/// `nested` and the chart div are skipped when nothing plottable is selected,
/// so a nested sub-section never renders with no parameters chosen for it.
let private chartSection
    (dispatch: Dispatch<Message>)
    (model: Model)
    (title: string)
    (isOpenByDefault: bool)
    (chart: ChartKind)
    (extraControls: Node)
    (divStyle: string)
    (divId: string)
    (nested: Node)
    =
    let order = paramOrderFor model chart
    let selected = selectionFor model chart
    // Only what this section both offers and has selected. The selection is
    // shared across loaded files and survives them being removed, so it can
    // hold a parameter no loaded file has — that must read as "nothing
    // selected" here, not as an empty chart div waiting for a figure that
    // never comes.
    let active = order |> List.filter selected.Contains

    // Past the cap the grid's cells stop being readable, so quadMulti draws
    // the first `subplotCap` and this says so rather than letting the rest
    // vanish silently. Only the two grid sections can hit it; the others
    // overlay everything into a single chart.
    let capNotice =
        if (chart = MagnitudeChart || chart = PhaseChart) && active.Length > subplotCap then
            span {
                attr.``class`` "tag is-warning is-light mb-3"
                sprintf "%d selected — only the first %d are drawn" active.Length subplotCap
            }
        else
            empty ()

    let emptyNotice =
        if not order.IsEmpty then
            "Select at least one parameter above."
        elif chart = GroupDelayChart then
            "Group delay is measured on the through paths, and those can't be named until each file's port layout is set — see Details on the file above."
        else
            "None of this chart's parameters exist in the loaded files."

    details {
        attr.``class`` "chart-section box mt-4"

        if isOpenByDefault then attr.``open`` true else attr.empty ()

        summary {
            attr.``class`` "title is-5"
            attr.style "cursor: pointer;"
            title
        }

        paramPicker dispatch model chart order selected active divId
        capNotice
        extraControls

        if active.IsEmpty then
            p {
                attr.``class`` "has-text-grey"
                emptyNotice
            }
        else
            concat {
                div {
                    attr.id divId
                    attr.style divStyle
                }

                nested
            }
    }

/// Collapsible sub-section nested inside the TDR Impedance section, holding
/// just the Gated Magnitude chart — shares TDR's own S11/S22 selection and
/// gate state instead of duplicating a second toggle row for the same
/// parameters; only its CSV button (a different chart, different data) is
/// section-specific.
let private tdrGatedSubSection (isOpenByDefault: bool) (divId: string) =
    details {
        attr.``class`` "chart-section mt-4"

        if isOpenByDefault then attr.``open`` true else attr.empty ()

        summary {
            attr.``class`` "title is-6"
            attr.style "cursor: pointer;"
            "Gated Magnitude (dB)"
        }

        div {
            attr.``class`` "field is-grouped mb-3"
            csvDownloadButton divId
        }

        div { attr.id divId }
    }

/// Collapsible sub-section nested inside the Smith Chart section, holding
/// just the VSWR chart — shares Smith's own S11/S22 selection instead of
/// duplicating a second toggle row for the same parameters (they're the
/// same reflection-coefficient data, just a different view of it: a complex
/// trajectory vs. a frequency-domain scalar). `extraControls` renders
/// between the CSV button and the chart, e.g. VSWR's own min/max toggle —
/// Smith itself has none, a 2D trajectory has no single meaningful extremum.
let private vswrSubSection (isOpenByDefault: bool) (extraControls: Node) (divId: string) =
    details {
        attr.``class`` "chart-section mt-4"

        if isOpenByDefault then attr.``open`` true else attr.empty ()

        summary {
            attr.``class`` "title is-6"
            attr.style "cursor: pointer;"
            "VSWR"
        }

        div {
            attr.``class`` "field is-grouped mb-3"
            csvDownloadButton divId
        }

        extraControls

        div { attr.id divId }
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
            "Min/max markers:"
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

/// On/off switch for Savitzky-Golay smoothing of the Group Delay curves
/// (both Absolute and Δ from mean — group delay is a numerical derivative
/// of phase, which amplifies whatever measurement noise is already in the
/// raw data). Off by default: shows the real measured data unless asked
/// otherwise.
let private smoothGroupDelayToggle (dispatch: Dispatch<Message>) (smooth: bool) =
    div {
        attr.``class`` "field is-grouped is-align-items-center mb-3"

        p {
            attr.``class`` "mr-2 has-text-grey"
            "Smoothing:"
        }

        label {
            attr.``class`` "switch"

            input {
                attr.``type`` "checkbox"
                attr.``checked`` smooth
                on.click (fun _ -> dispatch (SetSmoothGroupDelay(not smooth)))
            }

            span {
                attr.``class`` "switch-track"
                span { attr.``class`` "switch-thumb" }
            }
        }
    }

/// Global time-gate control for the TDR section: a two-handle ns slider
/// (same visual pattern as freqRangeSlider, minus per-file linking — there's
/// only one gate, shared across every loaded file) plus number inputs and a
/// Reset button once a gate is set. Bounded by `maxNs` (the largest native
/// TDR time span across the loaded S-parameter files — see tdrFullSpanNs);
/// the default [0, maxNs] range is what tdrGatedChartMulti treats as "no
/// gating". Drawn as guide lines on the TDR Impedance chart (see
/// gateBoundaryShapes in TouchstonePlot.fs) so the gate can be calibrated
/// against the actual step response.
let private tdrGateSlider (dispatch: Dispatch<Message>) (gateNs: (float * float) option) (gen: int) (maxNs: float) =
    if maxNs <= 0.0 then
        empty ()
    else
        let lo, hi = defaultArg gateNs (0.0, maxNs)
        let step = formatInvariant (maxNs / 500.0)
        let pct v = v / maxNs * 100.0

        div {
            attr.``class`` "mb-3"

            div {
                attr.``class`` "is-flex is-align-items-center is-justify-content-space-between"

                div {
                    attr.``class`` "is-flex is-align-items-center"

                    p {
                        attr.``class`` "mr-2 has-text-grey"
                        "Time gate:"
                    }

                    input {
                        attr.key (sprintf "tdr-gate-lo-%d" gen)
                        attr.``class`` "input is-small"
                        attr.style "width: 5rem;"
                        attr.``type`` "number"
                        attr.step "any"
                        attr.value (formatNs lo)

                        on.change (fun e ->
                            match tryParseInvariant e.Value with
                            | Some typed -> dispatch (SetTdrGate(max 0.0 typed, hi))
                            | None -> ())
                    }

                    p {
                        attr.``class`` "mx-2 is-size-7 has-text-grey"
                        "–"
                    }

                    input {
                        attr.key (sprintf "tdr-gate-hi-%d" gen)
                        attr.``class`` "input is-small"
                        attr.style "width: 5rem;"
                        attr.``type`` "number"
                        attr.step "any"
                        attr.value (formatNs hi)

                        on.change (fun e ->
                            match tryParseInvariant e.Value with
                            | Some typed -> dispatch (SetTdrGate(lo, min maxNs typed))
                            | None -> ())
                    }

                    p {
                        attr.``class`` "ml-2 is-size-7 has-text-grey"
                        "ns"
                    }
                }

                if gateNs.IsSome then
                    button {
                        attr.``class`` "button is-white is-size-7 p-0"
                        attr.style "height: auto; border: none;"
                        on.click (fun _ -> dispatch ResetTdrGate)
                        "Reset"
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
                    attr.min "0"
                    attr.max (formatInvariant maxNs)
                    attr.step step
                    attr.value (formatInvariant lo)
                    on.change (fun e -> dispatch (SetTdrGate(parseInvariant e.Value, hi)))
                }

                input {
                    attr.``class`` "range-dual"
                    attr.``type`` "range"
                    attr.min "0"
                    attr.max (formatInvariant maxNs)
                    attr.step step
                    attr.value (formatInvariant hi)
                    on.change (fun e -> dispatch (SetTdrGate(lo, parseInvariant e.Value)))
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

                let ok = okFiles model
                let okCount = ok.Length
                let unionRange = unionFreqRangeGHz model.Files

                // Only the files that are actually tested appear here; a
                // missing entry means either no active table covers that file
                // or it's hidden, and fileTag renders both as no badge at all.
                let verdicts = limitVerdicts model ok

                for f in model.Files do
                    fileTag dispatch okCount unionRange (verdicts.TryFind f.FileName) (model.PortLayouts.TryFind f.FileName) f

                if not ok.IsEmpty then
                    concat {
                        // No port-count gate on these two: magnitude and phase
                        // are defined for any Sij, so a 1-port return-loss
                        // measurement gets its chart too (it used to be
                        // silently chartless), and an N-port file gets the
                        // whole matrix instead of a 2-port quad.
                        chartSection
                            dispatch
                            model
                            "Magnitude (dB)"
                            true
                            MagnitudeChart
                            (extremaToggle dispatch MagnitudeChart model.ShowMagnitudeExtrema)
                            ""
                            "chart-magnitude"
                            (limitSubSection dispatch model)

                        chartSection
                            dispatch
                            model
                            "Phase (deg)"
                            false
                            PhaseChart
                            (empty ())
                            ""
                            "chart-phase"
                            (empty ())

                        if ok |> List.exists (fun (_, data) -> data.Option.Parameter = S) then
                            chartSection
                                dispatch
                                model
                                "Smith Chart"
                                false
                                SmithChart
                                (empty ())
                                "max-width: 700px; margin: 0 auto;"
                                "chart-smith"
                                (vswrSubSection false (extremaToggle dispatch VswrChart model.ShowVswrExtrema) "chart-vswr")

                        // Shown for any file that has two ends at all, even
                        // before its port layout is stated — the section then
                        // explains what's missing instead of disappearing.
                        if isTwoSided (pickerPorts model) then
                            // How many files could be compared against each
                            // other: those that actually have one of the
                            // selected through paths. A 2-port file next to a
                            // 4-port one shares none of them, so it doesn't
                            // count towards the two curves a deviation needs.
                            let gdSelected =
                                paramOrderFor model GroupDelayChart |> List.filter model.GroupDelaySelected.Contains

                            let comparableCount =
                                ok
                                |> List.filter (fun (_, data) ->
                                    gdSelected |> List.exists (fun (i, j) -> max i j <= data.Ports))
                                |> List.length

                            chartSection
                                dispatch
                                model
                                "Group Delay (ns)"
                                false
                                GroupDelayChart
                                (concat {
                                    groupDelayModeToggle dispatch model.GroupDelayMode comparableCount
                                    extremaToggle dispatch GroupDelayChart model.ShowGroupDelayExtrema
                                    smoothGroupDelayToggle dispatch model.SmoothGroupDelay
                                })
                                ""
                                "chart-group-delay"
                                (empty ())

                        let sFileData = ok |> List.map snd |> List.filter (fun data -> data.Option.Parameter = S)

                        if not sFileData.IsEmpty then
                            let maxGateNs = sFileData |> List.map tdrFullSpanNs |> List.max

                            chartSection
                                dispatch
                                model
                                "TDR Impedance (Ω)"
                                false
                                TdrChart
                                (concat {
                                    extremaToggle dispatch TdrChart model.ShowTdrExtrema
                                    tdrGateSlider dispatch model.TdrGateNs model.TdrGateGen maxGateNs
                                })
                                ""
                                "chart-tdr"
                                (tdrGatedSubSection false "chart-tdr-gated")
                    }
            }
    }
