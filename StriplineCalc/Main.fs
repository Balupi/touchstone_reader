module StriplineCalc.Main

open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Bolero
open Elmish
open StriplineCalc.State
open StriplineCalc.View

type App() =
    inherit ProgramComponent<Model, Message>()

    override this.Program = Program.mkSimple (fun _ -> initModel) update renderView

[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    builder.RootComponents.Add<App>("#app")
    builder.Build().RunAsync() |> ignore
    0
