# FieldPlay — Projektvorschlag (Mibo 4.0)

Eine interaktive 2D-Feldsimulations-Sandbox als natives Desktop-Programm: Leiter und Dielektrika
mit der Maus platzieren, verschieben und skalieren — und live das elektrostatische Feld sehen.
Ausbaustufe: Wellenausbreitung im Zeitbereich (FDTD), die anschauliche Version der Physik, die das
TDR-Feature des Touchstone-Readers aus Messdaten rekonstruiert.

**Framework**: [Mibo](https://github.com/AngelMunoz/Mibo) 4.0 (Elmish-MVU-Loop für Spiele,
raylib-cs-Backend), .NET 8+. **Sprache**: F#, mit denselben Konventionen wie dieses Repo.

---

## 1. Motivation

Drei Dinge kommen hier zusammen:

1. **MVU überträgt sich eins zu eins.** Mibo ist derselbe Elmish-Loop wie in den beiden
   Bolero-Apps dieses Repos — Model, Message-Union, pures `update`, View als Funktion des Models.
   Man lernt das Framework, ohne gleichzeitig eine neue Architektur lernen zu müssen.
2. **Der Solver existiert schon und ist verifiziert.** `StriplineFdm.fs` löst die
   Laplace-Gleichung per Box-Integration + SOR auf einem Tensor-Grid, validiert auf <0,1 %
   gegen Cohns exakte Lösung. Was fehlt, ist nur die Verallgemeinerung von "eine fest verdrahtete
   Stripline-Geometrie" auf "beliebige Rechtecke" — der numerische Kern bleibt derselbe.
3. **Nativ statt interpretiertem WASM.** In CLAUDE.md ist der Schmerzpunkt dokumentiert: derselbe
   F#-Code lief im Browser ~16 s, nativ quasi sofort. Deshalb darf die FDM-Verifikation im
   Stripline-Rechner nur auf Knopfdruck laufen. Nativ unter Mibo entfällt dieser Kompromiss —
   kontinuierliche Relaxation *während* man einen Leiter zieht wird realistisch.

Nebeneffekt: die halbe Bolero-Gotcha-Liste (DOM-Diffing, interop.js, Browser-Cache,
`on.change`-vs-`on.input`) entfällt schlicht, weil es keinen Browser gibt.

## 2. Projektaufbau

**Eigenes Repository** (`fieldplay` o. ä.), nicht ein weiterer Ordner hier:

- Anderes Deployment: natives Desktop-Binary statt GitHub Pages — der Pages-Workflow dieses Repos
  passt nicht und soll nicht verkompliziert werden.
- Ausgangspunkt ist das Mibo-Template (`dotnet new install Mibo.Templates`,
  `dotnet new mibo-2d -o FieldPlay`), nicht ein bestehendes Projekt.
- CLAUDE.md dieses Repos ist ausdrücklich als "carry forward"-Dokument geschrieben — die
  relevanten Abschnitte (F#-Stil, allgemeine Konventionen, Verifikationsdisziplin) werden in das
  neue Repo übernommen.

**Kein Code wird gelinkt oder kopiert**: `Stripline.fs` (geschlossene Formeln) braucht FieldPlay
nicht, und `StriplineFdm.fs` bleibt hier unangetastet. FieldPlay bekommt ein neues, allgemeineres
`FieldFdm.fs`, dessen Kern (Tensor-Grid, Box-Integration-Stencil, SOR, bilineares `sample`) aus
`solveGrid` übernommen und verallgemeinert wird — siehe §4. Die Stripline-Geometrie wird dann zum
*Testfall* des neuen Solvers, nicht zu seiner Abhängigkeit (§7).

Zielframework: net10.0 — Mibo braucht nur .NET 8+, und ohne Bolero gibt es keinen Grund für die
net8.0-Kappung der Web-Apps.

## 3. Domänenmodell

Pure F#-Datei ohne Framework-Abhängigkeit (dieselbe Trennung wie `Touchstone.fs` /
`Stripline.fs` hier: Domäne kennt kein UI):

```fsharp
type Material =
    | Conductor of potential: float   // Dirichlet: Knoten im Leiter fest auf V
    | Dielectric of er: float         // Zellen bekommen dieses epsilon_r

type Body =
    { Id: int
      X: float                        // untere linke Ecke, physikalische Einheiten
      Y: float
      W: float
      H: float
      Material: Material }

type Scene =
    { DomainW: float                  // Rechengebiet; Rand fest auf 0 V
      DomainH: float
      Bodies: Body list }             // spaeter platzierte Koerper ueberdecken fruehere
```

Nur achsenparallele Rechtecke — das ist keine Verlegenheitslösung, sondern die Bedingung, unter
der das Tensor-Grid-Verfahren exakt bleibt (jede Körperkante *ist* eine Gitterlinie). Schräge oder
runde Körper wären ein Verfahrenswechsel (Treppenstufen-Approximation oder unstrukturiertes
Gitter) und sind bewusst außerhalb des Scopes.

## 4. Solver-Verallgemeinerung: `FieldFdm.fs`

Was in `solveGrid` heute fest verdrahtet ist und wie es allgemein wird:

| heute (StriplineFdm) | allgemein (FieldFdm) |
|---|---|
| x-Zonen: `[ margin; Width; margin ]`, y-Zonen: `[ Height2; Thickness; Height1 ]` | Zonengrenzen = sortierte, deduplizierte Kanten-Koordinaten **aller** Körper plus Gebietsrand, pro Achse |
| ein Leiter (Indexfenster `i1..i2`, `j1..j2`, fest 1 V) | `isFixed`/`phi`-Initialisierung per Punkt-in-Rechteck-Test gegen alle `Conductor`-Körper, jeder mit eigenem Potential |
| eine horizontale Dielektrikum-Grenzfläche (`yInterface`) | `epsC` pro Zelle: Zellmittelpunkt gegen alle `Dielectric`-Körper testen, der zuletzt platzierte gewinnt, sonst 1.0 |
| löst immer bis Konvergenz (`while maxDelta > 1e-6`) | **resumierbarer** Zustand: `step n` macht n SOR-Sweeps und gibt den Zustand zurück — der Elmish-Tick treibt die Iteration |

Der resumierbare Zustand ist die eine echte Strukturänderung, und sie ist klein — die
`while`-Schleife wird zu einer Funktion über einem Record:

```fsharp
type SolverState =
    private
        { Xs: float[]                 // Knotenkoordinaten (Zonengrenzen exakt enthalten)
          Ys: float[]
          Phi: float[]
          IsFixed: bool[]
          AE: float[]                 // vorberechnete Stencil-Koeffizienten wie bisher
          AW: float[]
          AN: float[]
          AS: float[]
          InvDiag: float[]
          EpsC: float[]
          MaxDelta: float             // letzter Sweep-Fehler -> Konvergenzanzeige im UI
          Sweeps: int }

val create : Scene -> targetCellSize: float -> seed: SolverState option -> SolverState
val step   : sweeps: int -> SolverState -> SolverState
val sampleAt : SolverState -> x: float -> y: float -> float   // fuer Rendering/Sonden
```

`create` mit `seed` übernimmt die vorhandene bilineare `sample`-Funktion wörtlich: nach jeder
Geometrieänderung wird das Gitter neu gebaut, aber mit dem alten Potentialfeld vorbesetzt — SOR
konvergiert dann in wenigen Sweeps nach, statt bei null anzufangen. Genau dieser Mechanismus
existiert in `StriplineFdm.fs` schon (Grob-Lösung seedet Fein-Lösung); er wird nur umgewidmet
von "einmalig, zwei Auflösungen" zu "fortlaufend, nach jeder Mausbewegung".

Zwei Lehren aus CLAUDE.md, die hier direkt wieder gelten:

- **Gitterlinien exakt auf Materialgrenzen** — der ganze Grund für das Zonen-Verfahren. Beim
  Verallgemeinern neu dazu: Kanten, die fast zusammenfallen (Körper aneinandergeschoben), müssen
  innerhalb einer Toleranz zu *einer* Zonengrenze verschmolzen werden, sonst entstehen
  degenerierte Mini-Zellen, die den SOR-Faktor und die Zellzahl ruinieren.
- **Teuer und live verträgt sich nur mit Budget.** Der Tick macht ein festes Sweep-Budget pro
  Frame (Startwert: so viele Sweeps, wie in ~5 ms passen, gemessen, nicht geraten), nie "bis
  konvergiert". Konvergenz ist ein *Zustand* (`MaxDelta` unterschreitet die Schwelle), kein
  blockierender Aufruf — bei Stillstand der Geometrie läuft der Solver einfach weiter, bis die
  Anzeige "konvergiert" zeigt, und stoppt dann.

## 5. MVU-Entwurf

```fsharp
type Tool =
    | Select
    | PlaceConductor
    | PlaceDielectric

type Drag =
    | NoDrag
    | Moving of bodyId: int * grabOffset: Vector2
    | Resizing of bodyId: int * corner: Corner

type Model =
    { Scene: Scene
      Solver: SolverState
      Tool: Tool
      Drag: Drag
      Display: DisplayMode            // Potential | FieldMagnitude
      ShowEquipotentials: bool
      NextId: int }

type Message =
    | PointerDown of Vector2
    | PointerMoved of Vector2
    | PointerUp
    | ToolChanged of Tool
    | BodyDeleted of int
    | DisplayChanged of DisplayMode
    | SolverTicked                    // fester Zeitschritt: Sweep-Budget abarbeiten
```

`update` bleibt pur: Maus-Messages ändern nur `Scene`/`Drag`; jede Szenenänderung ersetzt
`Solver` durch `create scene target (Some model.Solver)`; `SolverTicked` ersetzt ihn durch
`step budget model.Solver`. Der Gitter-Neubau bei `PointerMoved` während eines Drags ist der
teuerste Schritt — falls das Profiling ihn als zu teuer für jede Mausbewegung ausweist
(wahrscheinlich bei feinen Gittern), ist die Ausweichposition schon definiert: während des Drags
nur die Rechtecke bewegen und den Solver einfrieren, Neubau erst bei `PointerUp`. Das ist eine
Ein-Zeilen-Entscheidung im `update`, keine Architekturfrage — genau dafür trennt das Modell
`Scene` (Wahrheit) von `SolverState` (abgeleitet).

Der Tick kommt aus Mibos Fixed-Timestep-Konfiguration, nicht aus einem eigenen Timer — Simulation
und Rendering entkoppelt zu halten ist bei Mibo der vorgesehene Weg, nicht ein Sonderfall.

## 6. Rendering

Schichten, von hinten nach vorn:

1. **Feld-Farbkarte**: `Phi` (oder |E| aus den Zellgradienten — dieselbe Rechnung wie im
   Energie-Integral von `solveGrid`) in ein Pixel-Array auf einem festen, *uniformen* Anzeigeraster
   (z. B. 512×384) via `sampleAt` abtasten, als Textur hochladen, skaliert zeichnen. Das entkoppelt
   die Anzeige vom nicht-uniformen Rechengitter und macht die Kosten pro Frame konstant.
   Farbverlauf: einfarbig-sequentiell für Potential, dazu Diverging erst, wenn negative
   Potentiale (zweiter Leiter mit −V) tatsächlich vorkommen.
2. **Äquipotentiallinien**: Marching Squares über demselben Anzeigeraster, ~10 Niveaus. Zweite
   Ausbaustufe, nicht Teil des ersten Wurfs.
3. **Körper**: Rechteck-Umrisse; Leiter gefüllt (Metall-Look), Dielektrika halbtransparent
   schraffiert. Auswahl-/Drag-Griffe an den Ecken.
4. **HUD**: Werkzeugleiste, Konvergenzanzeige (`MaxDelta` + Sweep-Zähler), Sonden-Auslese
   (Potential unter dem Mauszeiger via `sampleAt` — praktisch gratis).

Alles davon ist mit Mibos gebündeltem Layered-2D-Rendering und Input-Mapping abgedeckt; es wird
bewusst nichts an Assets gebraucht (keine Texturen von Platte, keine Sounds) — das Projekt bleibt
"nur F# und der Elmish-Loop", ganz im Sinn von Mibos eigener Philosophie.

## 7. Verifikation

Dieselbe Disziplin wie bei Savitzky-Golay und TDR in diesem Repo: **synthetische Referenzfälle
vor der ersten Grafik.** Konkret, als Konsolen-Checks im neuen Repo (Expecto erst, wenn es sich
lohnt — dann per `dotnet run`, nicht `dotnet test`):

1. **Plattenkondensator**: zwei breite Leiterplatten, Abstand d — C' muss gegen eps0·er·w/d
   konvergieren (mit bekanntem Streufeld-Fehler an den Rändern, also breite Platten). Prüft
   Stencil, Randbedingungen und Energie-Integral.
2. **Stripline-Regression**: die Geometrie aus `StriplineFdm.solve` als Szene nachbauen (Leiter
   bei 1 V, zwei Dielektrikum-Rechtecke, geerdete Wände) — das Ergebnis muss den Werten des
   bestehenden, verifizierten Solvers auf Gitterfehler-Niveau entsprechen. Damit ist der
   verallgemeinerte Solver gegen den spezialisierten rückverankert, und transitiv gegen Cohn.
3. **Seed-Invarianz**: `create` mit und ohne `seed` muss zum selben Feld konvergieren
   (der Seed darf nur die Geschwindigkeit ändern, nie das Ergebnis — dieselbe Invariante, die
   in `StriplineFdm.fs` heute als Kommentar dokumentiert ist, hier als Check).
4. **Degenerierte Kanten**: zwei Körper mit fast identischen Kantenkoordinaten dürfen weder
   Mini-Zellen noch NaNs erzeugen (Kantenverschmelzung aus §4 greift).

## 8. Meilensteine

Jeder Meilenstein endet lauffähig; Grafik kommt erst *nach* dem verifizierten Solver.

- **M0 — Gerüst**: Repo, Mibo-Template (`mibo-2d`), leeres Fenster mit Tick-Zähler läuft.
  Klärt nebenbei die eine echte Unbekannte: Mibo-4.0-API in der Praxis (§9).
- **M1 — `FieldFdm.fs` + Checks 1–4 aus §7 grün.** Das Herzstück, komplett ohne Grafik
  entwickelbar (`dotnet fsi` / Konsole). Hier entsteht der resumierbare Solver-Zustand.
- **M2 — Statisches Rendering**: eine hartkodierte Szene, Farbkarte + Körper-Umrisse gezeichnet,
  Solver läuft im Tick bis zur Konvergenzanzeige.
- **M3 — Interaktion**: Werkzeuge, Platzieren, Ziehen, Größe ändern, Löschen; Gitter-Neubau mit
  Seeding; Entscheidung "Neubau pro Mausbewegung oder pro Drop" anhand gemessener Zeiten.
- **M4 — Poliert**: Äquipotentiallinien, |E|-Modus, Sonden-Auslese, mehrere Potentiale
  (z. B. ±V-Leiterpaar), Konvergenz-/Performance-HUD.
- **M5 — FDTD-Ausbaustufe** (eigenständig, optional): 2D-FDTD (TMz-Yee-Gitter) auf *uniformem*
  Raster über derselben `Scene` — Puls injizieren, Reflexion/Brechung an den platzierten Körpern
  in Echtzeit ansehen. Eigene Referenzfälle: Reflexionskoeffizient an einer
  Dielektrikum-Stufe gegen (1−√er)/(1+√er), Courant-Bedingung als Assertion. Erst hier entsteht
  die direkte Brücke zum TDR-Feature des Touchstone-Readers.

M1 ist bewusst vor allem Sichtbaren: wenn die Verallgemeinerung des Solvers wider Erwarten hakt,
ist das Projekt danach umsteuerbar, ohne dass schon UI-Code daran hängt.

## 9. Risiken und offene Punkte

- **Mibo 4.0 im Detail.** Die Release-Notes zu 4.0 konnten aus dieser Umgebung nicht abgerufen
  werden (sergeytihon.com und forums.fsharp.org vom Egress-Proxy blockiert); die Beschreibung
  oben stützt sich auf das GitHub-README auf 4.0-Stand. Breaking Changes zwischen den schnellen
  Releases (2.0 im Juli, 4.0 im August) sind möglich — deshalb klärt M0 die API praktisch, bevor
  irgendetwas darauf aufbaut, und deshalb ist die Domäne (§3) frameworkfrei geschnitten.
- **Gitter-Neubau-Kosten beim Ziehen** — bekanntes Risiko mit definierter Ausweichposition (§5),
  kein Blocker.
- **Raylib-Nativbibliothek** muss auf der Zielplattform mitkommen (NuGet bringt sie für die
  gängigen RIDs mit; in M0 einmal auf dem echten Zielsystem verifizieren).
- **Scope-Disziplin**: keine schrägen/runden Körper (§3), kein Speichern/Laden vor M4, FDTD
  strikt als eigene Stufe hinter einem fertigen M4.
