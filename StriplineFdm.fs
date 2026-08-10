module TouchstoneReader.StriplineFdm

// 2D electrostatic finite-difference solver for the asymmetric-stripline
// cross-section — a numerical cross-check for the closed-form approximations
// in Stripline.fs, free of their validity limits (strong asymmetry, large
// er contrast, thick traces).
//
// Method: the TEM line's Z0 and effective permittivity follow from two
// capacitances, with the real dielectrics and with air. Each one comes from
// solving Laplace's equation for the potential (trace at 1 V, planes and
// far side walls at 0 V): box-integration 5-point stencil whose face
// coefficients average the adjacent cells' permittivities, SOR iteration
// with the optimal over-relaxation factor, capacitance via the field-energy
// integral. The grid is a tensor product of per-axis zones whose boundaries
// land *exactly* on the conductor faces and dielectric interface — naive
// uniform-grid rasterization of a thin trace put several percent of
// geometry error into the result, and worse, an error that jumped between
// resolutions instead of extrapolating away. Two resolutions are solved
// (the coarse solution seeding the fine one) and Richardson-extrapolated;
// their spread is reported as a grid-uncertainty estimate. Side walls sit
// 1.5 plane-spacings beyond the trace edges — fringing fields decay like
// exp(-pi*x/b), so the truncated energy is below the grid error.

open TouchstoneReader.Stripline

type FdmResult =
    { Z0: float
      EffectiveEr: float
      /// Relative spread between the two grid resolutions, percent — a
      /// rough indicator of the remaining numerical uncertainty.
      GridUncertaintyPct: float }

type private Grid =
    { C: float // capacitance per unit length, in units of eps0
      Phi: float[]
      Xs: float[]
      Ys: float[] }

let private eps0 = 8.8541878128e-12
let private c0 = 299792458.0

let private zoneCells (len: float) (target: float) =
    if len <= 0.0 then 0 else max 1 (int (System.Math.Round(len / target)))

/// Node coordinates along one axis: consecutive zones of the given lengths,
/// each subdivided uniformly into ~len/target cells, so every zone boundary
/// (conductor face, dielectric interface, domain edge) is exactly a grid
/// line.
let private axisNodes (lengths: float list) (target: float) =
    let nodes = ResizeArray<float>()
    nodes.Add 0.0
    let mutable x0 = 0.0

    for len in lengths do
        let n = zoneCells len target

        for k in 1 .. n do
            nodes.Add(x0 + len * float k / float n)

        x0 <- x0 + len

    nodes.ToArray()

/// Bilinear sample of a coarser solution at physical coordinates, used to
/// seed the finer grid's iteration (doesn't affect what SOR converges to,
/// only how fast it gets there).
let private sample (g: Grid) (x: float) (y: float) =
    let locate (coords: float[]) v =
        let n = coords.Length - 1
        let mutable lo = 0
        let mutable hi = n - 1

        while lo < hi do
            let mid = (lo + hi + 1) / 2
            if coords.[mid] <= v then lo <- mid else hi <- mid - 1

        let t = (v - coords.[lo]) / (coords.[lo + 1] - coords.[lo])
        lo, min 1.0 (max 0.0 t)

    let i, tx = locate g.Xs x
    let j, ty = locate g.Ys y
    let s = g.Xs.Length
    let p00 = g.Phi.[j * s + i]
    let p10 = g.Phi.[j * s + i + 1]
    let p01 = g.Phi.[(j + 1) * s + i]
    let p11 = g.Phi.[(j + 1) * s + i + 1]
    (p00 * (1.0 - tx) + p10 * tx) * (1.0 - ty) + (p01 * (1.0 - tx) + p11 * tx) * ty

/// One finite-difference solve with target cell size `b / nominal`.
let private solveGrid (g: Geometry) (withDielectric: bool) (nominal: int) (guess: Grid option) : Grid =
    let b = g.Height1 + g.Height2 + g.Thickness
    let target = b / float nominal
    let margin = 1.5 * b

    // Zone boundaries: side walls / trace edges in x; bottom plane,
    // conductor faces, top plane in y (trace band only present for t > 0).
    let xs = axisNodes [ margin; g.Width; margin ] target
    let ys = axisNodes [ g.Height2; g.Thickness; g.Height1 ] target
    let i1 = zoneCells margin target
    let i2 = i1 + zoneCells g.Width target
    let j1 = zoneCells g.Height2 target
    let j2 = j1 + zoneCells g.Thickness target
    let nx = xs.Length - 1
    let ny = ys.Length - 1
    let stride = nx + 1
    let nNodes = (nx + 1) * (ny + 1)
    let dxs = Array.init nx (fun i -> xs.[i + 1] - xs.[i])
    let dys = Array.init ny (fun j -> ys.[j + 1] - ys.[j])
    let phi = Array.zeroCreate nNodes
    let isFixed = Array.zeroCreate<bool> nNodes

    for j in 0 .. ny do
        for i in 0 .. nx do
            let k = j * stride + i

            if i = 0 || i = nx || j = 0 || j = ny then
                isFixed.[k] <- true
            elif i >= i1 && i <= i2 && j >= j1 && j <= j2 then
                isFixed.[k] <- true
                phi.[k] <- 1.0
            else
                match guess with
                | Some coarse -> phi.[k] <- sample coarse xs.[i] ys.[j]
                | None -> ()

    // Cell permittivities; the er1/er2 interface sits at the trace's own
    // mid-plane (inside the conductor band it has no effect on the field).
    let epsC = Array.zeroCreate (nx * ny)
    let yInterface = g.Height2 + g.Thickness / 2.0

    for j in 0 .. ny - 1 do
        for i in 0 .. nx - 1 do
            epsC.[j * nx + i] <-
                if not withDielectric then 1.0
                elif (ys.[j] + ys.[j + 1]) * 0.5 < yInterface then g.Er2
                else g.Er1

    // Precomputed per-node stencil coefficients (box integration on the
    // tensor grid).
    let aE = Array.zeroCreate nNodes
    let aW = Array.zeroCreate nNodes
    let aN = Array.zeroCreate nNodes
    let aS = Array.zeroCreate nNodes
    let invDiag = Array.zeroCreate nNodes

    for j in 1 .. ny - 1 do
        for i in 1 .. nx - 1 do
            let k = j * stride + i
            let eSW = epsC.[(j - 1) * nx + (i - 1)]
            let eSE = epsC.[(j - 1) * nx + i]
            let eNW = epsC.[j * nx + (i - 1)]
            let eNE = epsC.[j * nx + i]
            aE.[k] <- (eSE * dys.[j - 1] + eNE * dys.[j]) * 0.5 / dxs.[i]
            aW.[k] <- (eSW * dys.[j - 1] + eNW * dys.[j]) * 0.5 / dxs.[i - 1]
            aN.[k] <- (eNW * dxs.[i - 1] + eNE * dxs.[i]) * 0.5 / dys.[j]
            aS.[k] <- (eSW * dxs.[i - 1] + eSE * dxs.[i]) * 0.5 / dys.[j - 1]
            invDiag.[k] <- 1.0 / (aE.[k] + aW.[k] + aN.[k] + aS.[k])

    let omega = 2.0 / (1.0 + sin (System.Math.PI / float (max nx ny)))
    let mutable maxDelta = infinity
    let mutable sweep = 0

    while maxDelta > 1e-6 && sweep < 20000 do
        maxDelta <- 0.0

        for j in 1 .. ny - 1 do
            let row = j * stride

            for i in 1 .. nx - 1 do
                let k = row + i

                if not isFixed.[k] then
                    let v =
                        (aE.[k] * phi.[k + 1] + aW.[k] * phi.[k - 1]
                         + aN.[k] * phi.[k + stride]
                         + aS.[k] * phi.[k - stride])
                        * invDiag.[k]

                    let d = v - phi.[k]
                    phi.[k] <- phi.[k] + omega * d
                    let ad = abs d
                    if ad > maxDelta then maxDelta <- ad

        sweep <- sweep + 1

    // C = 2·W with V = 1: cell-wise field energy from bilinear gradients.
    let mutable energy2 = 0.0

    for j in 0 .. ny - 1 do
        for i in 0 .. nx - 1 do
            let p00 = phi.[j * stride + i]
            let p10 = phi.[j * stride + i + 1]
            let p01 = phi.[(j + 1) * stride + i]
            let p11 = phi.[(j + 1) * stride + i + 1]
            let gx = ((p10 + p11) - (p00 + p01)) * 0.5 / dxs.[i]
            let gy = ((p01 + p11) - (p00 + p10)) * 0.5 / dys.[j]
            energy2 <- energy2 + epsC.[j * nx + i] * (gx * gx + gy * gy) * dxs.[i] * dys.[j]

    { C = energy2; Phi = phi; Xs = xs; Ys = ys }

/// Numerical Z0 / effective permittivity for the same geometry the
/// closed-form `Stripline.impedance` takes. Noticeably slower than the
/// closed form (four Laplace solves) — seconds under interpreted WASM.
let solve (g: Geometry) : Result<FdmResult, string> =
    match impedance g with
    | Error e -> Error e
    | Ok _ ->
        let b = g.Height1 + g.Height2 + g.Thickness

        if (g.Width + 3.0 * b) / b * 96.0 > 1500.0 then
            Error "trace is too wide relative to the plane spacing for the built-in FDM grid"
        else
            let dielCoarse = solveGrid g true 48 None
            let dielFine = solveGrid g true 96 (Some dielCoarse)
            // The dielectric solutions seed the air solves too — a good
            // starting point only speeds SOR up, it can't bias the result.
            let airCoarse = solveGrid g false 48 (Some dielCoarse)
            let airFine = solveGrid g false 96 (Some dielFine)
            let z0Of (cd: Grid) (ca: Grid) = 1.0 / (c0 * eps0 * sqrt (cd.C * ca.C))
            let zC = z0Of dielCoarse airCoarse
            let zF = z0Of dielFine airFine
            let erC = dielCoarse.C / airCoarse.C
            let erF = dielFine.C / airFine.C

            Ok
                { Z0 = zF + (zF - zC) // first-order Richardson extrapolation
                  EffectiveEr = erF + (erF - erC)
                  GridUncertaintyPct = abs (zF - zC) / zF * 100.0 }
