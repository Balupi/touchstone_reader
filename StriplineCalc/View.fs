/// Bolero view for the standalone stripline-impedance calculator.
module StriplineCalc.View

open Bolero.Html
open Elmish
open TouchstoneReader
open StriplineCalc.State

/// One labeled input row. `keySuffix` feeds attr.key so the solver
/// overwriting w forces element replacement (see Model.Gen); the other
/// fields pass a constant. Commit on change (blur/enter) — with the raw
/// string stored as-is, what's rendered always round-trips what was typed.
let private inputRow
    (dispatch: Dispatch<Message>)
    (labelText: string)
    (field: Field)
    (value: string)
    (keySuffix: string)
    =
    div {
        attr.``class`` "is-flex is-align-items-center mb-2"

        p {
            attr.``class`` "is-size-7 has-text-grey mr-2"
            attr.style "width: 16rem;"
            labelText
        }

        input {
            attr.key (sprintf "stripline-%s" keySuffix)
            attr.``class`` "input is-small"
            attr.style "width: 6rem;"
            attr.``type`` "number"
            attr.step "any"
            attr.value value
            on.change (fun e -> dispatch (SetField(field, string e.Value)))
        }
    }

/// Impedance and per-unit-length line parameters for the current inputs,
/// recomputed on every render — the whole calculation is a handful of
/// closed-form evaluations.
let private results (m: Model) =
    match geometry m with
    | None ->
        p {
            attr.``class`` "has-text-grey mt-3"
            "Enter numeric values for w, t, h1, h2, εr1 and εr2."
        }
    | Some g ->
        match Stripline.impedance g with
        | Error e ->
            p {
                attr.``class`` "has-text-danger mt-3"
                e
            }
        | Ok r ->
            concat {
                nav {
                    attr.``class`` "level mt-4 mb-2"

                    let cells =
                        [ "Z₀", sprintf "%.2f Ω" r.Z0
                          "εeff", sprintf "%.2f" r.EffectiveEr
                          "Delay", sprintf "%.3f ns/m" r.DelayNsPerM
                          "C′", sprintf "%.1f pF/m" r.CapacitancePfPerM
                          "L′", sprintf "%.1f nH/m" r.InductanceNhPerM ]

                    for heading, value in cells do
                        div {
                            attr.``class`` "level-item has-text-centered"

                            div {
                                p {
                                    attr.``class`` "heading"
                                    heading
                                }

                                p {
                                    attr.``class`` "title is-4"
                                    value
                                }
                            }
                        }
                }

                for w in r.Warnings do
                    p {
                        attr.``class`` "is-size-7 has-text-warning-dark"
                        sprintf "⚠ %s" w
                    }
            }

let private solverRow (m: Model) (dispatch: Dispatch<Message>) =
    concat {
        div {
            attr.``class`` "is-flex is-align-items-center mt-4"

            p {
                attr.``class`` "is-size-7 has-text-grey mr-2"
                "Target Z₀:"
            }

            input {
                attr.``class`` "input is-small"
                attr.style "width: 6rem;"
                attr.``type`` "number"
                attr.step "any"
                attr.value m.TargetZ0
                on.change (fun e -> dispatch (SetField(FieldTargetZ0, string e.Value)))
            }

            p {
                attr.``class`` "mx-2 is-size-7 has-text-grey"
                "Ω"
            }

            button {
                attr.``class`` "button is-small is-info"
                on.click (fun _ -> dispatch SolveWidth)
                "Solve width"
            }
        }

        match m.SolveError with
        | Some e ->
            p {
                attr.``class`` "is-size-7 has-text-danger mt-2"
                e
            }
        | None -> empty ()
    }

/// On-demand numerical verification: a button that runs the FDM solver
/// (a few seconds under interpreted WASM, hence not live) and the outcome,
/// compared against the closed-form result shown above.
let private fdmRow (m: Model) (dispatch: Dispatch<Message>) =
    concat {
        div {
            attr.``class`` "is-flex is-align-items-center mt-4"

            button {
                attr.``class`` "button is-small"
                attr.disabled (m.Fdm = FdmRunning)
                on.click (fun _ -> dispatch RunFdm)
                "Verify numerically (FDM)"
            }
        }

        match m.Fdm with
        | FdmIdle -> empty ()
        | FdmRunning ->
            p {
                attr.``class`` "is-size-7 has-text-grey mt-2"
                "Solving Laplace on the cross-section… this can take a few seconds in the browser."
            }
        | FdmFailed e ->
            p {
                attr.``class`` "is-size-7 has-text-danger mt-2"
                e
            }
        | FdmDone r ->
            let deviation =
                match geometry m |> Option.map Stripline.impedance with
                | Some(Ok a) -> sprintf " — closed form deviates by %+.1f %%" ((a.Z0 - r.Z0) / r.Z0 * 100.0)
                | _ -> ""

            p {
                attr.``class`` "is-size-7 mt-2"

                sprintf "FDM: Z₀ = %.2f Ω, εeff = %.2f (grid ±%.1f %%)%s" r.Z0 r.EffectiveEr r.GridUncertaintyPct deviation
            }
    }

let renderView (model: Model) (dispatch: Dispatch<Message>) =
    div {
        attr.``class`` "container mt-5 px-4"
        attr.style "max-width: 720px;"

        h1 {
            attr.``class`` "title"
            "Stripline Impedance Calculator"
        }

        p {
            attr.``class`` "subtitle is-6"

            "Asymmetric (offset) stripline: a trace of width w and thickness t between two ground planes, "
            + "separated from them by dielectric heights h1 and h2 with their own permittivities εr1 and εr2 "
            + "(e.g. core vs. prepreg). Any consistent length unit — only the ratios matter."
        }

        div {
            attr.``class`` "box"

            inputRow dispatch "w — trace width" FieldW model.W (sprintf "w-%d" model.Gen)
            inputRow dispatch "t — trace thickness" FieldT model.T "t"
            inputRow dispatch "h1 — dielectric to one ground plane" FieldH1 model.H1 "h1"
            inputRow dispatch "εr1 — permittivity on the h1 side" FieldEr1 model.Er1 "er1"
            inputRow dispatch "h2 — dielectric to the other plane" FieldH2 model.H2 "h2"
            inputRow dispatch "εr2 — permittivity on the h2 side" FieldEr2 model.Er2 "er2"

            results model
            solverRow model dispatch
            fdmRow model dispatch
        }

        p {
            attr.``class`` "is-size-7 has-text-grey"

            "Method: Cohn's symmetric-stripline solution (exact elliptic-integral form at t = 0, narrow/wide-strip "
            + "thickness corrections otherwise), with the offset handled as the parallel combination of the two "
            + "symmetric half-structures (spacings 2·h1+t and 2·h2+t), per Wadell's Transmission Line Design Handbook. "
            + "The FDM verification solves Laplace's equation on the cross-section directly (geometry-snapped grid, "
            + "two resolutions, Richardson-extrapolated) and has none of the closed form's validity limits."
        }
    }
