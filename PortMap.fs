/// Which ports of a file face which, and what that makes each parameter mean:
/// a through path, near-end crosstalk or far-end crosstalk. A Touchstone file
/// carries none of this — the numbering is whatever the measurement setup
/// happened to use — so it is a per-file setting with deliberately no default.
/// Guessing it would silently relabel someone's crosstalk as insertion loss,
/// which is worse than showing nothing until it's been stated.
///
/// Pure: no UI, no Plotly, exercised directly from `dotnet fsi`.
module TouchstoneReader.PortMap

/// How a file's ports map onto the two ends of the device under test.
type PortLayout =
    /// Ports 1..N/2 on one end, N/2+1..N on the other; line k runs from port k
    /// to port k + N/2. A 4-port pair is then 1,2 | 3,4, and the through paths
    /// are S31 and S42.
    | EndsSplit
    /// Consecutive ports are the two ends of one line: 1<->2, 3<->4, and so
    /// on. The through paths of a 4-port file are then S21 and S43.
    | AdjacentPairs

/// What a parameter is, once the layout is known.
type ParamGroup =
    | Reflection
    | Through
    | NearEndCrosstalk
    | FarEndCrosstalk

let layoutLabel =
    function
    | EndsSplit -> "1..N/2 | N/2+1..N"
    | AdjacentPairs -> "1-2, 3-4, ..."

/// Short label plus the 4-port example, for the per-file switch in the UI:
/// the S-parameter is what makes the choice unambiguous.
let layoutDescription =
    function
    | EndsSplit -> "ends split: through S31, S42"
    | AdjacentPairs -> "adjacent pairs: through S21, S43"

let groupLabel =
    function
    | Reflection -> "Reflection"
    | Through -> "Through"
    | NearEndCrosstalk -> "NEXT"
    | FarEndCrosstalk -> "FEXT"

let groupDescription =
    function
    | Reflection -> "Sii — return loss at each port"
    | Through -> "the signal path of each line"
    | NearEndCrosstalk -> "coupling between lines, measured at the same end"
    | FarEndCrosstalk -> "coupling between lines, measured at opposite ends"

let allGroups = [ Reflection; Through; NearEndCrosstalk; FarEndCrosstalk ]

/// Whether the ports can be split into two ends at all. An odd count (or a
/// single port) has no second end, so only Reflection is defined for it — a
/// 1-port reflection measurement and a 3-port file both land here.
let isTwoSided (ports: int) = ports >= 2 && ports % 2 = 0

/// Whether a file's layout has to be stated before its parameters can be
/// named: more than one line, so that there is something to lay out, and two
/// ends to lay them across. A 1- or 2-port file needs no statement (nothing
/// to lay out / both layouts agree), and an odd port count has no second end
/// at all, so nothing beyond its reflections is classifiable under either
/// layout and offering the switch would only suggest otherwise.
let needsLayout (ports: int) = ports > 2 && isTwoSided ports

/// Line index (0-based) and end (0 or 1) of one port under a layout.
let private lineAndEnd layout (ports: int) (port: int) =
    match layout with
    | EndsSplit ->
        let half = ports / 2
        if port <= half then port - 1, 0 else port - half - 1, 1
    | AdjacentPairs -> (port - 1) / 2, (port - 1) % 2

/// The number of signal lines a layout sees in `ports` ports.
let lineCount (ports: int) = if isTwoSided ports then ports / 2 else 0

/// Which group Sij belongs to, or None when the layout cannot say: a port
/// number outside the file, or an off-diagonal parameter of a file that has no
/// second end to speak of.
let groupOf layout (ports: int) (i: int, j: int) =
    if i < 1 || j < 1 || i > ports || j > ports then
        None
    elif i = j then
        Some Reflection
    elif not (isTwoSided ports) then
        None
    else
        let li, ei = lineAndEnd layout ports i
        let lj, ej = lineAndEnd layout ports j

        if li = lj then
            // Same line and different ports means the two ends of that line:
            // both layouts put a line's two ports on opposite ends, so this is
            // always the signal path.
            Some Through
        elif ei = ej then
            Some NearEndCrosstalk
        else
            Some FarEndCrosstalk

/// Every parameter of a file, row-major — what the full matrix picker offers.
let allParams (ports: int) =
    [ for i in 1 .. ports do
        for j in 1 .. ports -> i, j ]

/// The parameters of one group. `includeReverse` decides whether both
/// directions are offered: a passive assembly is reciprocal, so S21 and S12
/// are nominally the same measurement and plotting both doubles the subplots
/// for no new information — but on real data the difference between them is a
/// measurement-quality check worth seeing, so the caller chooses.
///
/// With `includeReverse = false` the lower triangle (i >= j) is kept, not the
/// upper: Sij is the response at port i to a stimulus at port j, so the
/// forward measurement of a line is S21 (or S31 under EndsSplit), which has
/// i > j. Keeping i <= j instead would label the reverse direction as the
/// primary one.
let groupMembers layout (ports: int) (includeReverse: bool) (group: ParamGroup) =
    allParams ports
    |> List.filter (fun (i, j) -> (includeReverse || i >= j) && groupOf layout ports (i, j) = Some group)

/// Group memberships of every parameter, for labelling a matrix picker.
let groupsOfAll layout (ports: int) =
    allParams ports |> List.map (fun p -> p, groupOf layout ports p)

/// Port indices as they appear in a parameter's name: plain digits up to
/// port 9, comma-separated once either index reaches two — the Touchstone
/// convention, and the only unambiguous option ("S111" could be S1,11 or
/// S11,1). Files that big are rare but legal, and a 12-port backplane
/// fixture is exactly the kind of thing that turns up eventually.
let indexPair (i: int, j: int) =
    if i >= 10 || j >= 10 then sprintf "%d,%d" i j else sprintf "%d%d" i j

/// An S-parameter's name, e.g. "S21" or "S1,11". Use `indexPair` directly for
/// the Y/Z/H/G equivalents, whose letter comes from the file's option line.
let paramName (p: int * int) = "S" + indexPair p

/// The diagonal: every reflection parameter, in port order. Independent of
/// the layout — Sii is a reflection whatever faces what.
let reflectionParams (ports: int) = [ for i in 1 .. ports -> i, i ]

/// The forward through path of each line (S21, or S31 under EndsSplit), one
/// per line. Empty for a file with no second end.
let throughParams layout (ports: int) = groupMembers layout ports false Through

/// Conventional VNA quad order: S11 top-left, S21 top-right, S12
/// bottom-left, S22 bottom-right. Kept as a special case for 2-port files
/// because it is the order every VNA shows, and the order this app has always
/// shown; the group-based order below would give S11, S22, S21, S12 instead.
let private quadOrder2Port = [ (1, 1); (2, 1); (1, 2); (2, 2) ]

/// One group's parameters, forward direction first (S31 before S13) rather
/// than the row-major order `groupMembers` returns, so a selection that
/// includes both reads in measurement order. This is what a group button in
/// the UI selects.
let orderedGroup layout (ports: int) group =
    groupMembers layout ports false group
    @ (groupMembers layout ports true group |> List.filter (fun (i, j) -> i < j))

/// Canonical display order of every parameter of a file with `ports` ports:
/// reflections, then through paths, then near-end and far-end crosstalk,
/// forward before reverse within each group. Charts render a selection in
/// this order and the matrix picker is ordered by it, so a 4- or 8-port file
/// no longer has to squeeze into a hardcoded 2-port quad. Parameters the
/// layout cannot classify (the off-diagonals of an odd port count) come last
/// rather than being dropped, so nothing is silently unreachable.
let displayOrder layout (ports: int) =
    if ports = 2 then
        quadOrder2Port
    else
        let grouped = allGroups |> List.collect (orderedGroup layout ports)
        grouped @ (allParams ports |> List.filter (fun p -> not (List.contains p grouped)))

/// What the magnitude and phase grids start out showing for a file of this
/// size: the first port's return loss plus the first line's insertion loss —
/// the pair any RF check starts from. A 2-port file gets S11 + S21, exactly
/// the previous hardcoded default. A file with no through path (1 or 3 ports)
/// falls back to S11 alone.
let defaultMagnitudeSelection layout (ports: int) =
    (reflectionParams ports |> List.truncate 1) @ (throughParams layout ports |> List.truncate 1)

/// Smith chart, VSWR and TDR all plot a reflection parameter, and all three
/// started on S11 before; they keep doing so at any port count.
let defaultReflectionSelection (ports: int) = reflectionParams ports |> List.truncate 1

/// Group delay is a transmission measurement, so it starts on the first
/// line's forward through path (S21 for a 2-port file, as before).
let defaultThroughSelection layout (ports: int) =
    throughParams layout ports |> List.truncate 1

/// How many subplots a magnitude or phase grid will draw before it stops
/// being readable. An 8-port file has 64 parameters; even its 12 near-end
/// crosstalk terms in one grid would be twelve unreadable thumbnails, so the
/// UI caps the grid here and says how many were left out instead of silently
/// producing a wall of them. Nine fills a 3x3 grid exactly.
let subplotCap = 9
