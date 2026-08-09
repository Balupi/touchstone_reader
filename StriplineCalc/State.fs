/// Elmish model, messages, and update function for the standalone
/// stripline-impedance calculator app. The math itself lives in the shared
/// Stripline.fs at the repo root (TouchstoneReader.Stripline).
module StriplineCalc.State

open TouchstoneReader

/// Which input a SetField message applies to.
type Field =
    | FieldW
    | FieldT
    | FieldH1
    | FieldH2
    | FieldEr
    | FieldTargetZ0

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
      Er: string
      TargetZ0: string
      /// Bumped when the solver overwrites W — View.fs keys the w input on
      /// it, forcing element replacement even if the solved width happens to
      /// equal what the input already displays.
      Gen: int
      /// Feedback from the last SolveWidth, cleared on any edit.
      SolveError: string option }

let initModel =
    { W = "0.2"
      T = "0.035"
      H1 = "0.3"
      H2 = "0.5"
      Er = "4.3"
      TargetZ0 = "50"
      Gen = 0
      SolveError = None }

type Message =
    | SetField of field: Field * value: string
    | SolveWidth

let private tryFloat (s: string) =
    match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | false, _ -> None

/// The geometry, if every input currently parses as a number — View.fs
/// computes and shows the impedance from this on each render.
let geometry (m: Model) : Stripline.Geometry option =
    match tryFloat m.W, tryFloat m.T, tryFloat m.H1, tryFloat m.H2, tryFloat m.Er with
    | Some w, Some t, Some h1, Some h2, Some er ->
        Some { Width = w; Thickness = t; Height1 = h1; Height2 = h2; Er = er }
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
            | FieldEr -> { model with Er = value }
            | FieldTargetZ0 -> { model with TargetZ0 = value }

        { m with SolveError = None }
    | SolveWidth ->
        let solved =
            match tryFloat model.T, tryFloat model.H1, tryFloat model.H2, tryFloat model.Er, tryFloat model.TargetZ0 with
            | Some t, Some h1, Some h2, Some er, Some target ->
                // Width is what's being solved for, so a non-numeric w field
                // doesn't block the solve; 1.0 is just a placeholder.
                let g: Stripline.Geometry =
                    { Width = 1.0; Thickness = t; Height1 = h1; Height2 = h2; Er = er }

                Stripline.widthForImpedance g target
            | _ -> Error "t, h1, h2, εr and target Z₀ must all be numbers"

        match solved with
        | Ok w ->
            { model with
                W = w.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                Gen = model.Gen + 1
                SolveError = None }
        | Error e -> { model with SolveError = Some e }
