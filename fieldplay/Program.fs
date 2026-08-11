module FieldPlay.Program

// M0: the smallest possible Mibo program — a window whose tick counter and
// elapsed time visibly advance. Its only job is to prove the toolchain and
// the Mibo 4 API in practice before anything real is built on top.

open System.Numerics
open Raylib_cs
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D

type Model = {
  Ticks: int64
  ElapsedSeconds: float
}

[<Struct>]
type Msg = Tick of tick: GameTime

let init (_ctx: GameContext) : struct (Model * Cmd<Msg>) =
  { Ticks = 0L; ElapsedSeconds = 0.0 }, Cmd.none

let update (msg: Msg) (model: Model) : struct (Model * Cmd<Msg>) =
  match msg with
  | Tick gt ->
    {
      Ticks = model.Ticks + 1L
      ElapsedSeconds = model.ElapsedSeconds + gt.ElapsedGameTime.TotalSeconds
    },
    Cmd.none

let view (_ctx: GameContext) (model: Model) (buffer: RenderBuffer2D) =
  let label =
    sprintf "FieldPlay M0 - tick %d, t = %.1f s" model.Ticks model.ElapsedSeconds

  buffer
    .text(Raylib.GetFontDefault(), label, Vector2(20f, 20f), 24f)
    .drop()

[<EntryPoint>]
let main _ =
  let program =
    Program.mkProgram init update
    |> Program.withConfig(fun cfg -> {
      cfg with
          Width = 1280
          Height = 800
          Title = "FieldPlay"
    })
    |> Program.withTick Tick
    |> Program.withRenderer(fun () -> Renderer2D.create view)

  let game = new RaylibGame<Model, Msg>(program)
  game.Run()
  0
