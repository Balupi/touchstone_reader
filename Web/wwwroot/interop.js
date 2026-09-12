window.touchstoneInterop = {
    setupDropZone: function (elementId, dotNetRef) {
        // Kept for downloadCsv, which needs to call back into .NET
        // (GetCsv) on demand — it's otherwise only ever passed in here.
        window.touchstoneInterop._dotNetRef = dotNetRef;

        const el = document.getElementById(elementId);
        if (!el) return;

        const readFile = (file, displayName) => {
            if (!file) return;
            const reader = new FileReader();
            reader.onload = () => dotNetRef.invokeMethodAsync('OnFileDropped', displayName || file.name, reader.result);
            reader.readAsText(file);
        };

        const looksLikeTouchstone = (name) => /\.s\d+p$/i.test(name);

        // Recursively walks a dropped folder's FileSystemDirectoryEntry,
        // reading every Touchstone-looking file it finds — including in
        // subfolders — with its path relative to the dropped folder as the
        // display name (e.g. "sub/device1.s2p"), so same-named files from
        // different subfolders don't collide and it's clear where each one
        // came from. Files that don't look like Touchstone data are skipped
        // here, unlike a direct file drop (which still attempts to parse
        // anything dropped and shows the real parse error): a folder can
        // easily contain a lot of unrelated files, and running all of them
        // through the parser would just spam error notifications for files
        // nobody meant to load.
        const walkDirectory = (dirEntry, relativePath) => {
            const reader = dirEntry.createReader();
            // readEntries only returns one batch per call (browsers commonly
            // cap it around 100) — keep calling until it returns empty.
            const readNextBatch = () => {
                reader.readEntries((entries) => {
                    if (entries.length === 0) return;
                    entries.forEach((child) => {
                        const childPath = relativePath + '/' + child.name;
                        if (child.isDirectory) {
                            walkDirectory(child, childPath);
                        } else if (looksLikeTouchstone(child.name)) {
                            child.file((file) => readFile(file, childPath));
                        }
                    });
                    readNextBatch();
                });
            };
            readNextBatch();
        };

        el.addEventListener('dragover', (e) => {
            e.preventDefault();
            el.classList.add('is-dragover');
        });
        el.addEventListener('dragleave', () => el.classList.remove('is-dragover'));
        el.addEventListener('drop', (e) => {
            e.preventDefault();
            el.classList.remove('is-dragover');

            const items = e.dataTransfer.items;
            // webkitGetAsEntry (despite the name, supported by every major
            // browser) is what lets a dropped *folder* be told apart from an
            // empty file and actually walked — dataTransfer.files alone
            // flattens a folder into a single, content-less pseudo-file that
            // just fails to parse. Must be read synchronously here, before
            // any await: dataTransfer stops being valid once this handler
            // returns.
            if (items && items.length > 0 && typeof items[0].webkitGetAsEntry === 'function') {
                Array.from(items).forEach((item) => {
                    const entry = item.webkitGetAsEntry();
                    if (!entry) return;
                    if (entry.isDirectory) {
                        walkDirectory(entry, entry.name);
                    } else {
                        entry.file((file) => readFile(file));
                    }
                });
            } else {
                // Fallback for browsers without webkitGetAsEntry: unchanged
                // prior behavior — a dropped folder comes through as an
                // empty pseudo-file and fails to parse with a clear error.
                Array.from(e.dataTransfer.files).forEach((file) => readFile(file));
            }
        });

        const input = el.querySelector('input[type=file]');
        if (input) {
            input.addEventListener('change', (e) => Array.from(e.target.files).forEach((file) => readFile(file)));
        }
    },

    // Mirrors filePalette in TouchstonePlot.fs (the DIN 47100 core colors,
    // doubled into a lighter tier), so a trace that ever arrives without an
    // explicit color still lands inside the same set the file list's
    // swatches draw from. Every multi-file trace carries its own color
    // today, so this is only a fallback — but a stale one quietly puts back
    // colors the reference lines were never checked against, which is how
    // it drifted the first time.
    _colorway: [
        '#c9b37c', '#8b5a2b', '#3a9950', '#e0b400', '#8a8a8a', '#e0729e', '#3f7fd1', '#d94141',
        '#ded0a6', '#c08a52', '#7fcf8f', '#f2d34d', '#c4c4c4', '#f0a8c4', '#8fb8ea', '#f08a8a',
    ],

    // Plotly.NET always bakes in an explicit layout.width, which pins the
    // chart to that pixel size regardless of the `responsive: true` config —
    // wasted space in a wide container, overflow in a narrow one. Drop it and
    // let Plotly measure the container's actual width instead; layout.height
    // stays fixed (already sized per chart, e.g. taller for a 2-row grid).
    //
    // Also turns on unified hover (one tooltip listing every trace at the
    // hovered x, instead of one per curve) for frequency-x-axis charts —
    // Magnitude/Phase/Group Delay — but not the Smith chart, whose x-axis is
    // Re(Γ) rather than frequency and reads better with normal per-point hover.
    _makeResponsive: function (layout) {
        layout = Object.assign({}, layout, { autosize: true })
        delete layout.width
        const isSmith = layout.xaxis && layout.xaxis.title && layout.xaxis.title.text === 'Re(Γ)'
        if (!isSmith) layout.hovermode = 'x unified'
        // Unified hover's per-trace name (e.g. "deviceA.s2p S11") is
        // truncated to 15 chars by default, which cuts off exactly the part
        // that distinguishes files — disable that.
        layout.hoverlabel = Object.assign({}, layout.hoverlabel, { namelength: -1 })
        // Plotly's default legend sits to the right of the plot, eating
        // into width that's already tight in the magnitude/phase quad grid
        // (and doubly so once the container gets narrow). A horizontal
        // legend below the plot area uses the width freed up by autosize
        // above instead of competing with the chart for it.
        //
        // groupclick: 'togglegroup' pairs with renderChart's
        // _dedupeLegendByFile — each file's several per-parameter traces
        // share one legendgroup, so clicking that file's single legend
        // entry hides/shows all of them together instead of just the one
        // representative trace the entry happens to be attached to.
        layout.legend = Object.assign({}, layout.legend, {
            orientation: 'h',
            yanchor: 'top',
            y: -0.12,
            xanchor: 'center',
            x: 0.5,
            groupclick: 'togglegroup',
        })
        return layout
    },

    // Plotly always draws a white/opaque chart regardless of page theme.
    // Match the OS/browser dark-mode preference Bulma itself already follows,
    // so a chart doesn't sit as a glaring white box on a dark page.
    _themeLayout: function (layout) {
        layout = window.touchstoneInterop._makeResponsive(layout)
        layout = Object.assign({}, layout, { colorway: window.touchstoneInterop._colorway });
        const isDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        if (!isDark) return layout;

        layout.paper_bgcolor = 'rgba(0,0,0,0)';
        layout.plot_bgcolor = 'rgba(0,0,0,0)';
        layout.font = Object.assign({}, layout.font, { color: '#e6e6e6' });

        const axisIds = Object.keys(layout).filter((k) => /^(xaxis|yaxis)\d*$/.test(k));
        axisIds.forEach((k) => {
            layout[k] = Object.assign({}, layout[k], {
                gridcolor: 'rgba(255,255,255,0.15)',
                zerolinecolor: 'rgba(255,255,255,0.3)',
                linecolor: 'rgba(255,255,255,0.3)',
            });
        });

        // The unified-hover box (and the axis-hover legend it's built from)
        // always paints its own background regardless of paper_bgcolor, and
        // defaults to white — combined with the light-gray font color set
        // above, that made hover text on hover nearly invisible. Give it an
        // explicit dark background to match.
        layout.hoverlabel = Object.assign({}, layout.hoverlabel, {
            bgcolor: 'rgba(35,35,40,0.95)',
            bordercolor: 'rgba(255,255,255,0.3)',
            font: Object.assign({}, layout.hoverlabel && layout.hoverlabel.font, { color: '#e6e6e6' }),
        });
        return layout;
    },

    // Raw (untthemed) figures by divId, kept so a live OS theme change can
    // redraw already-rendered charts without needing new data from .NET.
    _lastFigures: {},

    // Print-style export: white paper, black grid/axes/text — JPEG has no
    // alpha channel, so a transparent/dark-mode background would otherwise
    // export as black regardless of on-screen theme. Trace colors are left
    // as-is (see _printData): files stay distinguishable in the export the
    // same way they are on screen, by color, rather than being flattened
    // to black.
    _printLayout: function (layout) {
        layout = window.touchstoneInterop._makeResponsive(layout)
        layout = Object.assign({}, layout, {
            paper_bgcolor: '#ffffff',
            plot_bgcolor: '#ffffff',
            font: Object.assign({}, layout.font, { color: '#000000' }),
        });
        const axisIds = Object.keys(layout).filter((k) => /^(xaxis|yaxis)\d*$/.test(k));
        axisIds.forEach((k) => {
            layout[k] = Object.assign({}, layout[k], {
                gridcolor: '#000000',
                zerolinecolor: '#000000',
                linecolor: '#000000',
            });
        });
        return layout;
    },

    // Trace names are built in TouchstonePlot.fs as "<filename> <Param><i><j>"
    // (e.g. "deviceA.s2p S11") — strips that known "<Letter><digits>" suffix
    // to recover the filename, which is otherwise not attached to the trace
    // in any dedicated field. Falls back to the whole name unchanged if it
    // doesn't match (e.g. a name _dedupeLegendByFile already shortened to
    // just the filename) — same resulting label either way.
    _fileLabelFor: function (traceName) {
        if (!traceName) return '';
        const match = traceName.match(/^(.*)\s[A-Za-z]\d+$/);
        return match ? match[1] : traceName;
    },

    // Collapses each file's several per-parameter traces (e.g. "deviceA.s2p
    // S11", "deviceA.s2p S21", ...) down to one legend entry showing just
    // the filename — the individual parameters already have their own
    // axis/subplot label (S11, S21, ...), so repeating the filename once
    // per parameter in the legend is just clutter. Traces with
    // showlegend === false (Smith chart's background grid lines) are left
    // alone. Paired with layout.legend.groupclick: 'togglegroup' above.
    _dedupeLegendByFile: function (data) {
        const seenFiles = new Set();
        return data.map((trace) => {
            if (trace.showlegend === false || !trace.name) return trace;
            const label = window.touchstoneInterop._fileLabelFor(trace.name);
            const isFirst = !seenFiles.has(label);
            seenFiles.add(label);
            return Object.assign({}, trace, {
                legendgroup: label,
                showlegend: isFirst,
                name: isFirst ? label : trace.name,
            });
        });
    },

    // Filenames currently hidden via their file-list color swatch (see
    // colorSwatch in View.fs / the swatch-toggle binding below) — re-applied
    // on every renderChart so the hidden state survives a chart re-render
    // (e.g. toggling a parameter), which would otherwise reset every
    // trace's visibility back to shown.
    _hiddenFiles: new Set(),

    _applyHiddenFiles: function (data) {
        return data.map((trace) => {
            const label = window.touchstoneInterop._fileLabelFor(trace.name);
            if (label && window.touchstoneInterop._hiddenFiles.has(label)) {
                return Object.assign({}, trace, { visible: 'legendonly' });
            }
            return trace;
        });
    },

    // Hides/shows every trace for `fileName` in every currently-rendered
    // chart at once (a file's traces are spread across every chart section —
    // magnitude/phase/smith/group-delay/tdr/tdr-gated). Uses the same Plotly
    // `visible: 'legendonly'` state a click on the file's legend entry
    // already toggles, just reachable from the file list's color swatch too.
    _setFileVisible: function (fileName, visible) {
        Object.keys(window.touchstoneInterop._lastFigures).forEach((divId) => {
            const el = document.getElementById(divId);
            if (!el || !el.data) return;

            el.data.forEach((trace, idx) => {
                if (window.touchstoneInterop._fileLabelFor(trace.name) === fileName) {
                    Plotly.restyle(el, { visible: visible ? true : 'legendonly' }, [idx]);
                }
            });
        });
    },

    // Flips `fileName`'s *global* hidden state (file-list swatch only),
    // fades its swatch to match, and propagates to every currently-rendered
    // chart via _setFileVisible/_hiddenFiles — the single source of truth
    // for "hidden everywhere," so hiding it here keeps it hidden on every
    // chart (including ones not rendered yet) until the swatch un-hides it
    // again. Deliberately separate from the TDR-pair-local hiding below:
    // the file list is meant to be the one *global* on/off switch, with
    // each chart's own legend staying local to just that chart.
    _toggleFileHidden: function (fileName) {
        const nowHidden = !window.touchstoneInterop._hiddenFiles.has(fileName);
        if (nowHidden) {
            window.touchstoneInterop._hiddenFiles.add(fileName);
        } else {
            window.touchstoneInterop._hiddenFiles.delete(fileName);
        }
        const swatch = document.getElementById('swatch-' + encodeURIComponent(fileName));
        if (swatch) swatch.style.opacity = nowHidden ? '0.25' : '1';
        window.touchstoneInterop._setFileVisible(fileName, !nowHidden);
    },

    // Filenames hidden via *either* chart-tdr's or chart-tdr-gated's own
    // legend — a second, narrower layer than _hiddenFiles, scoped to just
    // that pair (see _bindLegendClick). Re-applied alongside _hiddenFiles
    // in renderChart so it survives that pair's own re-renders (e.g.
    // dragging the TDR gate) the same way _hiddenFiles does for every
    // chart, without leaking to Magnitude/Phase/Smith/Group Delay or
    // touching the file-list swatch.
    _tdrHiddenFiles: new Set(),

    _applyTdrHiddenFiles: function (data) {
        return data.map((trace) => {
            const label = window.touchstoneInterop._fileLabelFor(trace.name);
            if (label && window.touchstoneInterop._tdrHiddenFiles.has(label)) {
                return Object.assign({}, trace, { visible: 'legendonly' });
            }
            return trace;
        });
    },

    // Toggles `fileName` in _tdrHiddenFiles and restyles it on both
    // chart-tdr and chart-tdr-gated (whichever are currently rendered) —
    // never the file-list swatch or any other chart, unlike
    // _toggleFileHidden. A global hide (_hiddenFiles) still applies on top
    // of this regardless, via _applyHiddenFiles; this only ever adds
    // additional, TDR-pair-scoped hiding, never removes a global one.
    _toggleTdrPairFileHidden: function (fileName) {
        const nowHidden = !window.touchstoneInterop._tdrHiddenFiles.has(fileName);
        if (nowHidden) {
            window.touchstoneInterop._tdrHiddenFiles.add(fileName);
        } else {
            window.touchstoneInterop._tdrHiddenFiles.delete(fileName);
        }
        ['chart-tdr', 'chart-tdr-gated'].forEach((divId) => {
            const el = document.getElementById(divId);
            if (!el || !el.data) return;

            el.data.forEach((trace, idx) => {
                if (window.touchstoneInterop._fileLabelFor(trace.name) === fileName) {
                    Plotly.restyle(el, { visible: nowHidden ? 'legendonly' : true }, [idx]);
                }
            });
        });
    },

    // Only chart-tdr and chart-tdr-gated get a legend-click override — a
    // deliberate pair, not "every chart" like _hiddenFiles/the file-list
    // swatch: they're one workflow (set the gate on the impedance view,
    // read the result on the gated one) sharing S11/S22 selection and gate
    // state already, so keeping a file hidden across *just* that pair when
    // toggled from either one's legend matches how tightly coupled they
    // already are. Every other chart's legend is left to Plotly's own
    // default (chart-local, not synced anywhere) behavior — the file-list
    // swatch is the one "hide everywhere" control, on purpose. Rebound on
    // every render rather than once: Plotly replaces a graph div's internal
    // event emitter on some react() calls (see _bindTdrGateDrag), which
    // would otherwise orphan this listener the same way it did there.
    _bindLegendClick: function (divId) {
        if (divId !== 'chart-tdr' && divId !== 'chart-tdr-gated') return;

        const el = document.getElementById(divId);
        el.removeAllListeners('plotly_legendclick');
        el.on('plotly_legendclick', function (eventData) {
            const trace = eventData.data[eventData.curveNumber];
            const fileName = window.touchstoneInterop._fileLabelFor(trace.name);
            if (fileName) window.touchstoneInterop._toggleTdrPairFileHidden(fileName);
            return false;
        });
    },

    // The file whose curves are currently emphasized, or null. Set by
    // hovering that file's legend entry (see _bindLegendHover) — purely
    // presentational and transient, unlike _hiddenFiles, but re-applied on
    // every renderChart for the same reason: Plotly.react rebuilds the
    // figure from the JSON F# just produced, which knows nothing about it.
    _highlightedFile: null,

    // What "highlighted" looks like: the other files drop to _dimOpacity,
    // the highlighted one grows to _highlightWidth. Dimming rather than
    // recoloring keeps each file's own palette color — and with it the
    // match to its file-list swatch — readable while it sits in the
    // background.
    _dimOpacity: 0.18,
    _highlightWidth: 3,

    _applyHighlight: function (data) {
        const self = window.touchstoneInterop;
        const target = self._highlightedFile;
        if (!target) return data;

        // A highlight naming a file this chart doesn't have would dim every
        // trace and emphasize nothing — reachable if the file went away
        // while its legend entry was hovered. Leave the figure alone.
        if (!data.some((trace) => self._fileLabelFor(trace.name) === target)) return data;

        return data.map((trace) => {
            const label = self._fileLabelFor(trace.name);
            if (!label) return trace; // Smith grid lines — same test _printData uses
            if (label === target) {
                return Object.assign({}, trace, {
                    line: Object.assign({}, trace.line, { width: self._highlightWidth }),
                });
            }
            return Object.assign({}, trace, { opacity: self._dimOpacity });
        });
    },

    // Emphasizes `fileName`'s curves in every currently-rendered chart, or
    // clears the emphasis when passed null. One Plotly.restyle per chart
    // with per-trace value arrays, not one call per trace the way
    // _setFileVisible does it: a legend hover touches every trace of every
    // chart at once, and this runs during a pointer gesture. Baseline width
    // and opacity are restored from _lastFigures — the figure as handed to
    // Plotly.react, so its indices line up with el.data — rather than from
    // a hardcoded default; `null` asks Plotly for its own default back.
    _setHighlight: function (fileName) {
        const self = window.touchstoneInterop;

        // Hovering a hidden file's legend entry would dim every other file
        // while emphasizing nothing that's actually on screen — so treat it
        // as no highlight at all. Only the global _hiddenFiles counts here;
        // a file hidden on just the TDR pair is still visible elsewhere.
        if (fileName && self._hiddenFiles.has(fileName)) fileName = null;

        if (self._highlightedFile === fileName) return;
        self._highlightedFile = fileName;

        Object.keys(self._lastFigures).forEach((divId) => {
            const el = document.getElementById(divId);
            if (!el || !el.data) return;

            const pristine = (self._lastFigures[divId] || {}).data || [];
            const indices = [];
            const opacity = [];
            const width = [];
            const restore = (v) => (v === undefined ? null : v);

            el.data.forEach((trace, idx) => {
                const label = self._fileLabelFor(trace.name);
                if (!label) return;

                const base = pristine[idx] || {};
                indices.push(idx);
                opacity.push(fileName && label !== fileName ? self._dimOpacity : restore(base.opacity));
                width.push(fileName && label === fileName ? self._highlightWidth : restore((base.line || {}).width));
            });

            if (indices.length) {
                Plotly.restyle(el, { opacity: opacity, 'line.width': width }, indices);
            }
        });
    },

    // Plotly has no legend-hover event (only plotly_legendclick), so this
    // rides on the legend's DOM instead. Two things shape how: the legend is
    // redrawn from scratch on every draw — including the Plotly.restyle that
    // _setHighlight itself issues — so per-entry listeners would need
    // constant rebinding and, worse, the entry under the cursor gets
    // replaced mid-hover, so its own mouseleave never arrives and the
    // highlight sticks on. Delegating from the chart div, which Plotly never
    // replaces, using mouseover/mouseout (those bubble; mouseenter and
    // mouseleave don't) avoids both: bound once per div, and the pointer's
    // position is re-resolved from scratch on every move. An entry's text is
    // the filename itself, since _dedupeLegendByFile renames each file's
    // representative trace to the bare filename — no need to read Plotly's
    // internal per-node data.
    _bindLegendHover: function (divId) {
        const el = document.getElementById(divId);
        if (!el || el.dataset.legendHoverBound) return;
        el.dataset.legendHoverBound = 'true';

        const labelAt = function (node) {
            const entry = node && node.closest ? node.closest('g.traces') : null;
            if (!entry) return null;
            const text = entry.querySelector('.legendtext');
            return text ? text.textContent.trim() : null;
        };

        el.addEventListener('mouseover', function (e) {
            const label = labelAt(e.target);
            if (label) window.touchstoneInterop._setHighlight(label);
        });

        el.addEventListener('mouseout', function (e) {
            // Moving from one entry straight onto the next fires this before
            // that entry's mouseover, so resolve where the pointer actually
            // went: another entry means switch, anything else means clear.
            window.touchstoneInterop._setHighlight(labelAt(e.relatedTarget));
        });
    },

    // Widens each trace for legibility once printed/exported (thin on-screen
    // lines can look faint on paper); color is left untouched, unlike the
    // black-flattened export this replaced. Grid lines (the Smith chart's
    // background resistance/reactance circles, drawn as plain traces with
    // no name) are kept thin instead of data-thick, so they don't become
    // indistinguishable from the actual S11/S22 curve — identified by
    // having no file label, *not* by showlegend === false: since
    // _dedupeLegendByFile, every file's non-first parameter trace (e.g.
    // S21 once S11 already represents that file in the legend) also has
    // showlegend === false despite being real data, not a grid line.
    _printData: function (data) {
        return data.map((trace) => {
            const isGridLine = !window.touchstoneInterop._fileLabelFor(trace.name);
            return Object.assign({}, trace, {
                line: Object.assign({}, trace.line, { width: isGridLine ? 1 : 2.5 }),
            });
        });
    },

    // JPEG has no alpha channel, so a transparent/dark-mode background would
    // export as black. Swap the whole chart to a white-background print-style
    // figure just for the download, then swap back — replaces the default
    // camera button since its own download path always captures the current
    // on-screen colors as-is.
    // Triggers a browser download of `text` as `filename` via a throwaway
    // Blob URL + synthetic anchor click.
    _downloadText: function (filename, text) {
        const blob = new Blob([text], { type: 'text/csv' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);
    },

    // Shared by the modebar's CSV button and the always-visible "CSV" button
    // in each chart section's toggle row (added because the modebar only
    // reveals itself on hover and its icons are easy to miss/mix up). Calls
    // back into .NET (GetCsv) to build the CSV on demand rather than reading
    // an already-built one — it's not sent over with the chart's own figure
    // JSON, since building it eagerly on every render was wasted work most
    // of the time (see ChartResult in TouchstonePlot.fs).
    downloadCsv: function (divId) {
        window.touchstoneInterop._dotNetRef.invokeMethodAsync('GetCsv', divId).then((csv) => {
            if (csv) window.touchstoneInterop._downloadText(divId + '.csv', csv);
        });
    },

    _config: function (divId) {
        return {
            responsive: true,
            // A shape's own `editable: true` (TouchstonePlot.fs's gate
            // lines) only lets Plotly recognize a grab on it and suppress
            // the plot's normal box-zoom drag underneath — actually
            // committing the resulting position change needs this broader
            // per-chart permission too, or the drag is intercepted but
            // never applied. Harmless for every other chart here, which
            // has no editable shapes to begin with.
            edits: { shapePosition: true },
            modeBarButtonsToRemove: ['toImage'],
            modeBarButtonsToAdd: [
                {
                    name: 'downloadJpegWhite',
                    title: 'Download plot as jpeg',
                    icon: Plotly.Icons.camera,
                    click: function (gd) {
                        const fig = window.touchstoneInterop._lastFigures[divId];
                        const printLayout = window.touchstoneInterop._printLayout(fig.layout);
                        const printData = window.touchstoneInterop._printData(fig.data);
                        Plotly.react(divId, printData, printLayout, window.touchstoneInterop._config(divId))
                            .then(() => Plotly.downloadImage(gd, { format: 'jpeg', filename: divId }))
                            .then(() =>
                                Plotly.react(
                                    divId,
                                    fig.data,
                                    window.touchstoneInterop._themeLayout(fig.layout),
                                    window.touchstoneInterop._config(divId)
                                )
                            );
                    },
                },
                {
                    name: 'downloadCsv',
                    title: 'Download data as csv',
                    icon: Plotly.Icons.disk,
                    click: function () {
                        window.touchstoneInterop.downloadCsv(divId);
                    },
                },
            ],
        };
    },

    renderChart: function (divId, figureJson) {
        const el = document.getElementById(divId);
        if (!el) return;
        const fig = JSON.parse(figureJson);
        fig.data = window.touchstoneInterop._dedupeLegendByFile(fig.data);
        fig.data = window.touchstoneInterop._applyHiddenFiles(fig.data);
        fig.data = window.touchstoneInterop._applyHighlight(fig.data);
        if (divId === 'chart-tdr' || divId === 'chart-tdr-gated') {
            fig.data = window.touchstoneInterop._applyTdrHiddenFiles(fig.data);
        }
        // Explicitly carries the currently-displayed zoom/pan (if any) over
        // onto the fresh layout, for every axis (xaxis, xaxis2, yaxis2, ...
        // — a subplot grid has more than one). Deliberately not done via
        // layout.uirevision (Plotly's own built-in "preserve what the user
        // interactively changed" mechanism) — tried that first, but it
        // preserves *every* interactive change under one revision, not just
        // axis ranges: it also froze the TDR gate's guide-line shapes at
        // whatever Y position a live drag gesture last left them at (see
        // gateBoundaryShapes in TouchstonePlot.fs — Y0/Y1 are always meant
        // to snap back to 0/1, full plot height, every render), since
        // editable shape positions ride on the same revision as axis
        // ranges. Copying just the axis range by hand avoids that. Reads
        // from el._fullLayout (Plotly's own resolved current state), not
        // el.layout (the raw, possibly-still-autorange figure as last given
        // to it), since that's what reflects an actual interactive zoom/pan.
        if (el._fullLayout) {
            Object.keys(el._fullLayout)
                .filter((k) => /^(xaxis|yaxis)\d*$/.test(k))
                .forEach((k) => {
                    const current = el._fullLayout[k];
                    if (current && current.autorange === false && current.range) {
                        fig.layout[k] = Object.assign({}, fig.layout[k], {
                            range: current.range.slice(),
                            autorange: false,
                        });
                    }
                });
        }
        window.touchstoneInterop._lastFigures[divId] = fig;
        // Plotly.react diffs against the existing plot and patches it in place
        // instead of tearing down and rebuilding the whole chart like newPlot.
        // _bindTdrGateDrag must wait for this to actually finish — binding
        // synchronously right after calling (not awaiting) react() attached
        // to chart-tdr's event emitter before Plotly had finished setting it
        // up on that div's very first render, silently missing the swap
        // renderChart itself waits for elsewhere too.
        Plotly.react(divId, fig.data, window.touchstoneInterop._themeLayout(fig.layout), window.touchstoneInterop._config(divId)).then(() => {
            window.touchstoneInterop._bindTdrGateDrag(divId);
            window.touchstoneInterop._bindLegendClick(divId);
            window.touchstoneInterop._bindLegendHover(divId);
        });
    },

    // Lets the two vertical dashed lines TouchstonePlot.fs draws for an
    // active TDR time gate (gateBoundaryShapes, Editable = true) be dragged
    // directly on the chart instead of only via the number inputs/slider
    // below it. Plotly's own shape-drag handling only kicks in when the
    // pointer grabs a shape itself, so dragging anywhere else in the plot
    // area still does Plotly's normal box-zoom — the two gestures don't
    // compete for the same drag. The gate lines are always the *last* two
    // entries in layout.shapes (extrema min/max lines, if shown, come
    // first — see gateBoundaryShapes/tdrChartMulti in TouchstonePlot.fs),
    // so they're identified by position, not by name/id.
    //
    // Rebinds on *every* render rather than once: Plotly replaces the graph
    // div's internal event emitter object on some react() calls (observed
    // here specifically when the shapes count changes, e.g. a gate first
    // appearing), silently orphaning a listener attached to the old one —
    // a "bind once" guard leaves the chart's drag handles permanently dead
    // the moment that first happens. removeAllListeners first, so a render
    // that *didn't* get a fresh emitter doesn't accumulate duplicates.
    _bindTdrGateDrag: function (divId) {
        if (divId !== 'chart-tdr') return;

        const el = document.getElementById(divId);
        el.removeAllListeners('plotly_relayout');
        el.on('plotly_relayout', function (eventData) {
            const shapes = (el.layout && el.layout.shapes) || [];
            if (shapes.length < 2) return;

            const loIdx = shapes.length - 2;
            const hiIdx = shapes.length - 1;
            const loKey = 'shapes[' + loIdx + '].x0';
            const hiKey = 'shapes[' + hiIdx + '].x0';
            if (!(loKey in eventData) && !(hiKey in eventData)) return;

            // Midpoint of x0/x1, not x0 alone: gateBoundaryShapes
            // (TouchstonePlot.fs) gives each line a tiny x0/x1 slant to
            // sidestep a Plotly.js bug where a perfectly vertical line's
            // editable dragging never engages at all — reading just x0
            // would report a position off by that (invisible but nonzero)
            // epsilon.
            const midX = (s) => (s.x0 + s.x1) / 2;
            let lo = Math.max(0, midX(shapes[loIdx]));
            let hi = Math.max(0, midX(shapes[hiIdx]));

            // Clamp whichever handle just moved instead of letting it cross
            // the other one — State.fs's own min/max reordering already
            // guarantees a valid (lo <= hi) gate either way, but without
            // this the crossed handle would visually swap to the other
            // side once you drag past it, which reads as the line jumping
            // rather than stopping. minGapNs is deliberately tiny (not
            // meant to be a visible "closest allowed gate width", just
            // enough to keep the two lines distinguishable).
            const minGapNs = 0.001;
            if (loKey in eventData && lo > hi - minGapNs) lo = hi - minGapNs;
            if (hiKey in eventData && hi < lo + minGapNs) hi = lo + minGapNs;

            window.touchstoneInterop._dotNetRef.invokeMethodAsync('OnTdrGateDragged', lo, hi);
        });
    },

    // Without this, a chart rendered under one OS theme keeps that theme's
    // colors (e.g. light-gray axis text) even after the user switches to the
    // other theme, which can leave it unreadable (light text on a now-light
    // page). Bound once; re-themes every chart drawn so far on each change.
    _themeListenerBound: false,
    bindThemeListener: function () {
        if (window.touchstoneInterop._themeListenerBound) return;
        window.touchstoneInterop._themeListenerBound = true;
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        mq.addEventListener('change', () => {
            Object.keys(window.touchstoneInterop._lastFigures).forEach((divId) => {
                if (!document.getElementById(divId)) return;
                const fig = window.touchstoneInterop._lastFigures[divId];
                Plotly.react(
                    divId,
                    fig.data,
                    window.touchstoneInterop._themeLayout(fig.layout),
                    window.touchstoneInterop._config(divId)
                );
            });
        });
    },

    // A chart drawn while its <details> is collapsed measures its container
    // as 0x0. Resize it once the section is actually expanded. Safe to call
    // repeatedly: already-bound elements are skipped.
    //
    // Also rebinds _bindTdrGateDrag/_bindLegendClick here, not just from
    // renderChart: a chart whose very first render happens while its
    // section is still collapsed (0x0) can end up with those listeners
    // silently not attached to begin with — same underlying cause as the
    // "Plotly replaces the internal event emitter" issue renderChart's own
    // rebind-every-render already works around, just triggered by this
    // resize instead of a react() call, so renderChart's rebind never runs
    // for it. Found by testing the nested TDR Gated Magnitude sub-section
    // specifically, since it's the one chart that both starts collapsed
    // *and* has interactive listeners riding on it.
    setupCollapsibleCharts: function () {
        document.querySelectorAll('details.chart-section').forEach((details) => {
            if (details.dataset.resizeBound) return;
            details.dataset.resizeBound = 'true';
            details.addEventListener('toggle', () => {
                if (!details.open) return;
                details.querySelectorAll('.js-plotly-plot').forEach((el) => {
                    Plotly.Plots.resize(el);
                    window.touchstoneInterop._bindTdrGateDrag(el.id);
                    window.touchstoneInterop._bindLegendClick(el.id);
                    window.touchstoneInterop._bindLegendHover(el.id);
                });
            });
        });

        // Wires each chart section's always-visible "CSV" button; the
        // button's id ("csv-chart-magnitude" etc.) encodes its chart's divId.
        document.querySelectorAll('button[id^="csv-"]').forEach((btn) => {
            if (btn.dataset.csvBound) return;
            btn.dataset.csvBound = 'true';
            const divId = btn.id.slice('csv-'.length);
            btn.addEventListener('click', () => window.touchstoneInterop.downloadCsv(divId));
        });

        // Wires each file list entry's color swatch; the id
        // ("swatch-<url-encoded filename>") is set in View.fs's colorSwatch.
        // Toggles that file's curves across every chart and fades the
        // swatch to reflect the hidden state.
        document.querySelectorAll('span[id^="swatch-"]').forEach((el) => {
            if (el.dataset.toggleBound) return;
            el.dataset.toggleBound = 'true';
            const fileName = decodeURIComponent(el.id.slice('swatch-'.length));
            el.addEventListener('click', () => window.touchstoneInterop._toggleFileHidden(fileName));
        });
    }
};
