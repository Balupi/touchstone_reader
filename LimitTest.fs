/// Keysight-style limit lines and pass/fail testing, modeled on the limit
/// table a network analyzer presents: TYPE / BEGIN STIMULUS / END STIMULUS /
/// BEGIN RESPONSE / END RESPONSE per row, one table per measured parameter.
/// Pure — no UI, no Plotly — so it can be exercised from `dotnet fsi`.
module TouchstoneReader.LimitTest

open System
open System.Globalization

/// A row's role, matching the instrument's TYPE column: Max fails points
/// above the line, Min fails points below it, Off keeps the row in the table
/// without testing against it.
type LimitKind =
    | Off
    | Min
    | Max

/// One row of a limit table. Stimulus is in the chart's x unit (GHz for the
/// magnitude chart), response in its y unit (dB there).
type LimitSegment =
    { Kind: LimitKind
      BeginStimulus: float
      EndStimulus: float
      BeginResponse: float
      EndResponse: float }

/// A fresh row for the editor: a flat Max line, which is the shape most
/// insertion-loss specs start from.
let newSegment (loStimulus: float) (hiStimulus: float) =
    { Kind = Max
      BeginStimulus = loStimulus
      EndStimulus = hiStimulus
      BeginResponse = 0.0
      EndResponse = 0.0 }

/// The same segment with its stimulus bounds ascending, responses carried
/// along. Entering a row "backwards" is easy to do in a table and describes
/// the same line, so it's normalized rather than rejected.
let private oriented (s: LimitSegment) =
    if s.BeginStimulus <= s.EndStimulus then
        s
    else
        { s with
            BeginStimulus = s.EndStimulus
            EndStimulus = s.BeginStimulus
            BeginResponse = s.EndResponse
            EndResponse = s.BeginResponse }

/// The segment's limit value at `x`, or None when `x` falls outside it: the
/// instrument tests a point only against segments that actually span it, so
/// a spec covering 0-20 GHz says nothing about a point at 25 GHz.
let limitAt (segment: LimitSegment) (x: float) =
    let s = oriented segment

    if s.Kind = Off || Double.IsNaN x || x < s.BeginStimulus || x > s.EndStimulus then
        None
    elif s.EndStimulus = s.BeginStimulus then
        // Zero-width row: a single stimulus point. Its begin response is the
        // limit there; an end response typed alongside it has nowhere to go.
        Some s.BeginResponse
    else
        let t = (x - s.BeginStimulus) / (s.EndStimulus - s.BeginStimulus)
        Some(s.BeginResponse + t * (s.EndResponse - s.BeginResponse))

/// How far `y` lies outside one segment at `x`, or None if it's inside (or
/// the segment doesn't cover `x`). Always positive, whichever the kind — a
/// point exactly on the limit passes, matching the instrument, which fails a
/// point only once it lies beyond.
let private violation (segment: LimitSegment) (x: float) (y: float) =
    match limitAt segment x with
    | None -> None
    | Some limit ->
        match segment.Kind with
        | Max -> if y > limit then Some(y - limit) else None
        | Min -> if y < limit then Some(limit - y) else None
        | Off -> None

/// The result of testing one series against one table.
type LimitOutcome =
    { /// Indices into the tested series that lie outside at least one segment.
      FailingIndices: int[]
      /// Largest violation found, as (stimulus, measured value, amount by
      /// which it's outside). None when nothing failed.
      WorstViolation: (float * float * float) option
      /// Whether the table had anything to test against at all. An empty or
      /// all-Off table tests nothing, and the instrument passes such a trace
      /// rather than reporting on it.
      Tested: bool }

let untested =
    { FailingIndices = [||]
      WorstViolation = None
      Tested = false }

/// Tests one series (stimulus, response) against a table. A point is tested
/// against every active segment spanning it — a MIN and a MAX row over the
/// same band is the normal shape of a spec — and fails if it lies outside
/// any of them.
let evaluate (segments: LimitSegment list) (xs: float[]) (ys: float[]) =
    let active = segments |> List.filter (fun s -> s.Kind <> Off)

    if active.IsEmpty then
        untested
    else
        let failing = ResizeArray<int>()
        let mutable worst = None

        for k in 0 .. (min xs.Length ys.Length) - 1 do
            let mutable amount = None

            for s in active do
                match violation s xs.[k] ys.[k] with
                | Some v -> amount <- Some(match amount with
                                           | Some previous -> max previous v
                                           | None -> v)
                | None -> ()

            match amount with
            | Some v ->
                failing.Add k

                match worst with
                | Some(_, _, previous) when previous >= v -> ()
                | _ -> worst <- Some(xs.[k], ys.[k], v)
            | None -> ()

        { FailingIndices = failing.ToArray()
          WorstViolation = worst
          Tested = true }

/// A table with nothing active in it passes, so "not tested" and "passed"
/// are deliberately the same verdict here; `Tested` is what tells them apart
/// for display purposes.
let passed (outcome: LimitOutcome) = outcome.FailingIndices.Length = 0

/// Each active row as the two endpoints of a straight line, for drawing the
/// mask. One polyline per row rather than one for the whole table: rows can
/// be disjoint, or deliberately overlap (again, MIN plus MAX over one band),
/// so joining them end to end would draw lines nobody entered.
let polylines (segments: LimitSegment list) =
    segments
    |> List.filter (fun s -> s.Kind <> Off)
    |> List.map (fun s ->
        let s = oriented s
        s.Kind, [| s.BeginStimulus; s.EndStimulus |], [| s.BeginResponse; s.EndResponse |])

// ---------------------------------------------------------------------------
// Storage format
//
// Hand-rolled rather than a JSON serializer: five numbers and an enum per row
// don't justify one, it keeps the payload small, and parsing stays defensive —
// a row that doesn't make sense is dropped instead of throwing, so a
// half-written or hand-edited localStorage entry can't take the app down.
// Culture-invariant throughout, like the number inputs in View.fs.
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private num (v: float) = v.ToString("R", inv)

/// The instrument's own spelling of the TYPE column, shared by the storage
/// format and the editor so the two can't drift apart.
let kindText =
    function
    | Off -> "OFF"
    | Min -> "MIN"
    | Max -> "MAX"

let kindOfText =
    function
    | "MIN" -> Some Min
    | "MAX" -> Some Max
    | "OFF" -> Some Off
    | _ -> None

let private parseNum (s: string) =
    match Double.TryParse(s, NumberStyles.Float, inv) with
    | true, v -> Some v
    | _ -> None

/// One parameter's table, as (port i, port j, rows).
type LimitTables = ((int * int) * LimitSegment list) list

let serialize (tables: LimitTables) =
    tables
    |> List.filter (fun (_, rows) -> not rows.IsEmpty)
    |> List.map (fun ((i, j), rows) ->
        let body =
            rows
            |> List.map (fun s ->
                String.Join(";", [| kindText s.Kind; num s.BeginStimulus; num s.EndStimulus; num s.BeginResponse; num s.EndResponse |]))
            |> String.concat "|"

        sprintf "%d%d:%s" i j body)
    |> String.concat "#"

let deserialize (text: string) : LimitTables =
    if String.IsNullOrWhiteSpace text then
        []
    else
        text.Split('#')
        |> Array.toList
        |> List.choose (fun table ->
            let parts = table.Split(':')

            if parts.Length <> 2 || parts.[0].Length <> 2 then
                None
            else
                match Int32.TryParse(string parts.[0].[0], NumberStyles.Integer, inv),
                      Int32.TryParse(string parts.[0].[1], NumberStyles.Integer, inv) with
                | (true, i), (true, j) ->
                    let rows =
                        parts.[1].Split('|')
                        |> Array.toList
                        |> List.choose (fun row ->
                            let f = row.Split(';')

                            if f.Length <> 5 then
                                None
                            else
                                match kindOfText f.[0], parseNum f.[1], parseNum f.[2], parseNum f.[3], parseNum f.[4] with
                                | Some kind, Some bs, Some es, Some br, Some er ->
                                    Some
                                        { Kind = kind
                                          BeginStimulus = bs
                                          EndStimulus = es
                                          BeginResponse = br
                                          EndResponse = er }
                                | _ -> None)

                    if rows.IsEmpty then None else Some((i, j), rows)
                | _ -> None)
