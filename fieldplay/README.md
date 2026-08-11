# FieldPlay

An interactive 2D electrostatic field sandbox: place, drag and resize conductors and dielectrics
with the mouse and watch the field solve live. Built in F# on [Mibo](https://github.com/AngelMunoz/Mibo)
(Elmish MVU for games, raylib backend). A planned second stage adds time-domain wave propagation
(FDTD).

The full plan — motivation, solver design, MVU sketch, milestones, verification strategy — lives in
[Projektvorschlag.md](Projektvorschlag.md) (German). The numerical core generalizes the verified
finite-difference solver from [touchstone_reader](https://github.com/Balupi/touchstone_reader)'s
stripline calculator.

## Status

**M0 — scaffold.** A window with a live tick counter, proving the toolchain and the Mibo 4 API.
Next up (M1): the generalized FDM solver with its verification cases, before any real graphics.

## Build & run

```
dotnet build
dotnet run
```

Requires the .NET 10 SDK. On a headless machine, `xvfb-run -a dotnet run` works (software GL via
Mesa/llvmpipe).
