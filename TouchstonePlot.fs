/// Plotly.NET charts for Touchstone network-parameter data.
module TouchstoneReader.TouchstonePlot

open System
open System.Numerics
open Plotly.NET
open TouchstoneReader.Touchstone

let private toDb (c: Complex) = 20.0 * log10 c.Magnitude
let private toDeg (c: Complex) = c.Phase * 180.0 / Math.PI

/// One overlaid chart: magnitude (dB) of every parameter vs frequency (GHz).
let magnitudeChart (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    [ for i in 1 .. data.Ports do
        for j in 1 .. data.Ports ->
            let ys = data.Matrices |> Array.map (fun m -> toDb m.[i, j])
            Chart.Line(x = freqGHz, y = ys, Name = sprintf "%A%d%d" data.Option.Parameter i j) ]
    |> Chart.combine
    |> Chart.withTitle (sprintf "%A magnitude (dB)" data.Option.Parameter)
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Magnitude (dB)"

/// One overlaid chart: phase (deg) of every parameter vs frequency (GHz).
let phaseChart (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    [ for i in 1 .. data.Ports do
        for j in 1 .. data.Ports ->
            let ys = data.Matrices |> Array.map (fun m -> toDeg m.[i, j])
            Chart.Line(x = freqGHz, y = ys, Name = sprintf "%A%d%d" data.Option.Parameter i j) ]
    |> Chart.combine
    |> Chart.withTitle (sprintf "%A phase (deg)" data.Option.Parameter)
    |> Chart.withXAxisStyle "Frequency (GHz)"
    |> Chart.withYAxisStyle "Phase (deg)"

/// Grid of small-multiple magnitude (dB) charts, one per Sij (or Yij/Zij/...).
let magnitudeGrid (data: TouchstoneFile) =
    let freqGHz = data.Frequencies |> Array.map (fun f -> f / 1e9)
    let n = data.Ports
    [ for i in 1 .. n do
        for j in 1 .. n ->
            let ys = data.Matrices |> Array.map (fun m -> toDb m.[i, j])
            Chart.Line(x = freqGHz, y = ys, Name = sprintf "%A%d%d" data.Option.Parameter i j)
            |> Chart.withTitle (sprintf "%A%d%d" data.Option.Parameter i j) ]
    |> Chart.Grid(n, n)
    |> Chart.withSize (350 * n, 300 * n)

/// Opens magnitude + phase overlay charts in the default browser.
let show (data: TouchstoneFile) =
    magnitudeChart data |> Chart.show
    phaseChart data |> Chart.show
