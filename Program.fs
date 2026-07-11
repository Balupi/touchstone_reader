module TouchstoneReader.Program

open TouchstoneReader.Touchstone
open TouchstoneReader.TouchstonePlot

[<EntryPoint>]
let main argv =
    match argv with
    | [| path |] ->
        let data = Touchstone.read path
        printfn "Loaded %s: %d-port, %d frequency points, unit=%A format=%A"
            path data.Ports data.Frequencies.Length data.Option.FreqUnit data.Option.Format
        TouchstonePlot.show data
        0
    | _ ->
        eprintfn "Usage: dotnet run -- <path-to-touchstone-file>"
        1
