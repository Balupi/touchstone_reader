/// Elmish model, messages, and update function for the standalone
/// stripline-impedance calculator app. The math itself lives in the shared
/// Stripline.fs at the repo root (TouchstoneReader.Stripline).
module StriplineCalc.State

open Elmish
open TouchstoneReader

/// Which input a SetField message applies to.
type Field =
    | FieldW
    | FieldT
    | FieldH1
    | FieldH2
    | FieldEr1
    | FieldEr2
    | FieldTargetZ0

/// State of the on-demand numerical (FDM) verification. Not recomputed
/// live: four Laplace solves take seconds under interpreted WASM, so it
/// runs on a button press and is invalidated by any input edit.
type FdmState =
    | FdmIdle
    | FdmRunning
    | FdmDone of StriplineFdm.FdmResult
    | FdmFailed of string

/// Inputs kept as the raw typed strings rather than parsed floats: what's
/// rendered then always equals what's in the live DOM, sidestepping the
/// stale-input-after-transform class of Blazor diffing bugs (see the
/// TouchstoneReader web app's LoadedFile.FreqRangeGen for the long version).
/// Parsing happens on use, in geometry / the solver.
type Model =
    { W: string
      T: string
      H1: string
      H2: string
      Er1: string
      Er2: string
      TargetZ0: string
      /// Bumped when the solver overwrites W — View.fs keys the w input on
      /// it, forcing element replacement even if the solved width happens to
      /// equal what the input already displays.
      Gen: int
      /// Feedback from the last SolveWidth, cleared on any edit.
      SolveError: string option
      Fdm: FdmState }

let initModel =
    { W = "0.2"
      T = "0.035"
      H1 = "0.3"
      H2 = "0.5"
      Er1 = "4.3"
      Er2 = "4.3"
      TargetZ0 = "50"
      Gen = 0
      SolveError = None
      Fdm = FdmIdle }

type Message =
    | SetField of field: Field * value: string
    | SolveWidth
    | RunFdm
    | FdmFinished of Result<StriplineFdm.FdmResult, string>

let private tryFloat (s: string) =
    match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | false, _ -> None

/// The geometry, if every input currently parses as a number — View.fs
/// computes and shows the impedance from this on each render.
let geometry (m: Model) : Stripline.Geometry option =
    match tryFloat m.W, tryFloat m.T, tryFloat m.H1, tryFloat m.H2, tryFloat m.Er1, tryFloat m.Er2 with
    | Some w, Some t, Some h1, Some h2, Some er1, Some er2 ->
        Some { Width = w; Thickness = t; Height1 = h1; Height2 = h2; Er1 = er1; Er2 = er2 }
    | _ -> None

let update message model =
    match message with
    | SetField(field, value) ->
        let m =
            match field with
            | FieldW -> { model with W = value }
            | FieldT -> { model with T = value }
            | FieldH1 -> { model with H1 = value }
            | FieldH2 -> { model with H2 = value }
            | FieldEr1 -> { model with Er1 = value }
            | FieldEr2 -> { model with Er2 = value }
            | FieldTargetZ0 -> { model with TargetZ0 = value }

        // Any edit invalidates a previous numerical result along with any
        // stale solver feedback.
        { m with SolveError = None; Fdm = FdmIdle }, Cmd.none
    | SolveWidth ->
        let solved =
            match tryFloat model.T, tryFloat model.H1, tryFloat model.H2, tryFloat model.Er1, tryFloat model.Er2, tryFloat model.TargetZ0 with
            | Some t, Some h1, Some h2, Some er1, Some er2, Some target ->
                // Width is what's being solved for, so a non-numeric w field
                // doesn't block the solve; 1.0 is just a placeholder.
                let g: Stripline.Geometry =
                    { Width = 1.0; Thickness = t; Height1 = h1; Height2 = h2; Er1 = er1; Er2 = er2 }

                Stripline.widthForImpedance g target
            | _ -> Error "t, h1, h2, εr1, εr2 and target Z₀ must all be numbers"

        match solved with
        | Ok w ->
            { model with
                W = w.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                Gen = model.Gen + 1
                SolveError = None
                Fdm = FdmIdle },
            Cmd.none
        | Error e -> { model with SolveError = Some e }, Cmd.none
    | RunFdm ->
        match geometry model with
        | None -> { model with Fdm = FdmFailed "w, t, h1, h2, εr1 and εr2 must all be numbers" }, Cmd.none
        | Some g ->
            let run () =
                task {
                    // WASM is single-threaded: without yielding here, the
                    // browser never paints the "solving…" state before the
                    // solver blocks the thread.
                    do! System.Threading.Tasks.Task.Delay 30
                    return StriplineFdm.solve g
                }

            { model with Fdm = FdmRunning }, Cmd.OfTask.perform run () FdmFinished
    | FdmFinished(Ok r) -> { model with Fdm = FdmDone r }, Cmd.none
    | FdmFinished(Error e) -> { model with Fdm = FdmFailed e }, Cmd.none
