module TouchstoneReader.StriplineApp

open TouchstoneReader.Stripline

let private usage =
    "Asymmetric stripline impedance calculator\n\
     \n\
     Usage:\n\
     \  dotnet run -- --w <width> --h1 <height1> --h2 <height2> --er <epsilon> [--t <thickness>]\n\
     \  dotnet run -- --target <ohm> --h1 <height1> --h2 <height2> --er <epsilon> [--t <thickness>]\n\
     \n\
     Geometry (any consistent length unit — only ratios matter):\n\
     \  --w    trace width\n\
     \  --t    trace thickness (default 0)\n\
     \  --h1   dielectric height between trace and one ground plane\n\
     \  --h2   dielectric height between trace and the other ground plane\n\
     \  --er   relative permittivity of the dielectric\n\
     \  --target   instead of --w: solve for the width that gives this impedance\n\
     \n\
     Example: dotnet run -- --w 0.2 --t 0.035 --h1 0.3 --h2 0.5 --er 4.3"

let rec private parseArgs acc args =
    match args with
    | [] -> Ok acc
    | key :: value :: rest when (key: string).StartsWith "--" ->
        match System.Double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> parseArgs (Map.add (key.Substring 2) v acc) rest
        | false, _ -> Error (sprintf "cannot parse '%s' as a number for %s" value key)
    | key :: _ -> Error (sprintf "unexpected argument '%s'" key)

let private require (opts: Map<string, float>) name =
    match Map.tryFind name opts with
    | Some v -> Ok v
    | None -> Error (sprintf "missing required option --%s" name)

let private printResult (g: Geometry) (r: ImpedanceResult) =
    printfn "Asymmetric stripline: w=%g t=%g h1=%g h2=%g er=%g" g.Width g.Thickness g.Height1 g.Height2 g.Er
    printfn "  Z0    = %8.2f ohm" r.Z0
    printfn "  delay = %8.3f ns/m" r.DelayNsPerM
    printfn "  C'    = %8.1f pF/m" r.CapacitancePfPerM
    printfn "  L'    = %8.1f nH/m" r.InductanceNhPerM
    for w in r.Warnings do
        eprintfn "  warning: %s" w

let private run (opts: Map<string, float>) =
    let result =
        match require opts "h1", require opts "h2", require opts "er" with
        | Ok h1, Ok h2, Ok er ->
            let geometry w =
                { Width = w
                  Thickness = defaultArg (Map.tryFind "t" opts) 0.0
                  Height1 = h1
                  Height2 = h2
                  Er = er }
            match Map.tryFind "target" opts, Map.tryFind "w" opts with
            | Some target, _ ->
                match widthForImpedance (geometry 1.0) target with
                | Ok w ->
                    printfn "Width for %g ohm: w = %.4f" target w
                    impedance (geometry w) |> Result.map (printResult (geometry w))
                | Error e -> Error e
            | None, Some w -> impedance (geometry w) |> Result.map (printResult (geometry w))
            | None, None -> Error "specify either --w or --target"
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
    match result with
    | Ok () -> 0
    | Error e ->
        eprintfn "error: %s" e
        eprintfn "%s" usage
        1

[<EntryPoint>]
let main argv =
    match argv with
    | [||] | [| "--help" |] | [| "-h" |] ->
        printfn "%s" usage
        0
    | _ ->
        match parseArgs Map.empty (List.ofArray argv) with
        | Ok opts -> run opts
        | Error e ->
            eprintfn "error: %s" e
            eprintfn "%s" usage
            1
