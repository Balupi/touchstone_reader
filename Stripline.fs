module TouchstoneReader.Stripline

// Characteristic impedance of an asymmetric (offset) stripline: a trace of
// width w and thickness t embedded in a homogeneous dielectric (er) between
// two ground planes, at distance h1 from one plane and h2 from the other.
//
// All lengths are in the same arbitrary unit — the impedance depends only on
// ratios, so mm, mil, µm all work as long as they're consistent.
//
// Method (Cohn 1954, as collected in Wadell, "Transmission Line Design
// Handbook", §4.4): the offset line is treated as the parallel combination of
// two symmetric striplines with ground spacings b1 = 2·h1 + t and
// b2 = 2·h2 + t (each contributing half its capacitance), so
// Z0 = 2·Z1·Z2 / (Z1 + Z2), which reduces exactly to the symmetric case for
// h1 = h2. Each symmetric impedance uses Cohn's exact elliptic-integral
// solution for a zero-thickness strip, or his narrow/wide-strip
// thickness-corrected formulas for t > 0.

type Geometry =
    { Width: float          // w: trace width
      Thickness: float      // t: trace (metal) thickness
      Height1: float        // h1: dielectric between trace and one ground plane
      Height2: float        // h2: dielectric between trace and the other plane
      Er: float }           // relative permittivity of the dielectric

type ImpedanceResult =
    { Z0: float                     // characteristic impedance, ohm
      DelayNsPerM: float            // propagation delay, ns/m
      CapacitancePfPerM: float      // C', pF/m
      InductanceNhPerM: float       // L', nH/m
      Warnings: string list }

let private eta0 = 376.730313668    // free-space wave impedance, ohm
let private c0 = 299792458.0        // speed of light, m/s

// Complete elliptic integral of the first kind K(k), via the
// arithmetic-geometric mean (converges quadratically).
let private ellipK k =
    let rec go a b =
        if abs (a - b) <= 1e-15 * a then a
        else go ((a + b) / 2.0) (sqrt (a * b))
    System.Math.PI / (2.0 * go 1.0 (sqrt (1.0 - k * k)))

// Symmetric stripline, zero-thickness strip — Cohn's exact solution.
let private zSymThin er w b =
    let u = System.Math.PI * w / (2.0 * b)
    let k = 1.0 / cosh u
    let k' = tanh u
    eta0 / (4.0 * sqrt er) * ellipK k / ellipK k'

// Symmetric stripline with finite strip thickness — Cohn's approximations:
// narrow strips via an equivalent round conductor, wide strips via the
// parallel-plate term plus fringing capacitance.
let private zSymThick er w b t =
    if w / (b - t) < 0.35 then
        let d0 =
            w / 2.0
            * (1.0
               + t / (System.Math.PI * w)
                 * (1.0 + log (4.0 * System.Math.PI * w / t))
               + 0.51 * (t / w) ** 2.0)
        60.0 / sqrt er * log (4.0 * b / (System.Math.PI * d0))
    else
        let x = 1.0 / (1.0 - t / b)
        let fringe =
            (2.0 * x * log (x + 1.0) - (x - 1.0) * log (x * x - 1.0))
            / System.Math.PI
        eta0 / (4.0 * sqrt er) / (w / (b - t) + fringe)

let private zSym er w b t =
    if t <= 0.0 then zSymThin er w b else zSymThick er w b t

let private validityWarnings (g: Geometry) =
    let b = g.Height1 + g.Height2 + g.Thickness
    [ if g.Thickness / b > 0.25 then
          sprintf "t/b = %.2f exceeds 0.25; thickness correction loses accuracy" (g.Thickness / b)
      if g.Width / (b - g.Thickness) > 2.0 then
          sprintf "w/(b-t) = %.2f is very wide; result approaches the parallel-plate limit" (g.Width / (b - g.Thickness))
      if g.Thickness > g.Width then
          sprintf "t/w = %.2f exceeds 1; the narrow-strip thickness correction is outside its validity range" (g.Thickness / g.Width)
      let ratio = max (g.Height1 / g.Height2) (g.Height2 / g.Height1)
      if ratio > 5.0 then
          sprintf "h1/h2 asymmetry of %.1f:1 is strong; the parallel-combination model degrades" ratio ]

/// Characteristic impedance and per-unit-length line parameters of an
/// asymmetric stripline. Returns Error for a non-physical geometry.
let impedance (g: Geometry) : Result<ImpedanceResult, string> =
    if g.Width <= 0.0 || g.Height1 <= 0.0 || g.Height2 <= 0.0 then
        Error "width, h1 and h2 must all be positive"
    elif g.Thickness < 0.0 then
        Error "thickness must be zero or positive"
    elif g.Er < 1.0 then
        Error "relative permittivity must be >= 1"
    else
        let z1 = zSym g.Er g.Width (2.0 * g.Height1 + g.Thickness) g.Thickness
        let z2 = zSym g.Er g.Width (2.0 * g.Height2 + g.Thickness) g.Thickness
        let z0 = 2.0 * z1 * z2 / (z1 + z2)
        let sqrtEr = sqrt g.Er
        Ok
            { Z0 = z0
              DelayNsPerM = sqrtEr / c0 * 1e9
              CapacitancePfPerM = sqrtEr / (c0 * z0) * 1e12
              InductanceNhPerM = z0 * sqrtEr / c0 * 1e9
              Warnings = validityWarnings g }

/// Solve for the trace width that hits a target impedance (bisection; Z0 is
/// strictly decreasing in width). The other geometry values are fixed.
let widthForImpedance (g: Geometry) (targetZ0: float) : Result<float, string> =
    if targetZ0 <= 0.0 then Error "target impedance must be positive" else
    let b = g.Height1 + g.Height2 + g.Thickness
    // Probe the rest of the geometry (heights, thickness, er) with a known-
    // good width first, so an invalid input surfaces as an Error here rather
    // than an exception out of the bisection closure below.
    match impedance { g with Width = max b 1.0 } with
    | Error e -> Error e
    | Ok _ ->
        let z w =
            match impedance { g with Width = w } with
            | Ok r -> r.Z0
            | Error e -> failwith e
        // Below w ~ t the narrow-strip thickness correction is invalid (and
        // can even go negative), so don't search there.
        let wLo, wHi = max (1e-3 * b) g.Thickness, 100.0 * b

        if z wLo < targetZ0 then
            Error (sprintf "target %.1f ohm is too high for this geometry (max ~%.1f ohm)" targetZ0 (z wLo))
        elif z wHi > targetZ0 then
            Error (sprintf "target %.1f ohm is too low for this geometry (min ~%.1f ohm)" targetZ0 (z wHi))
        else
            let rec bisect lo hi n =
                let mid = (lo + hi) / 2.0
                if n = 0 then mid
                elif z mid > targetZ0 then bisect mid hi (n - 1)
                else bisect lo mid (n - 1)

            Ok (bisect wLo wHi 60)
