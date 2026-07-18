window.touchstoneInterop = {
    setupDropZone: function (elementId, dotNetRef) {
        // Kept for downloadCsv, which needs to call back into .NET
        // (GetCsv) on demand — it's otherwise only ever passed in here.
        window.touchstoneInterop._dotNetRef = dotNetRef;

        const el = document.getElementById(elementId);
        if (!el) return;

        const readFile = (file) => {
            if (!file) return;
            const reader = new FileReader();
            reader.onload = () => dotNetRef.invokeMethodAsync('OnFileDropped', file.name, reader.result);
            reader.readAsText(file);
        };

        el.addEventListener('dragover', (e) => {
            e.preventDefault();
            el.classList.add('is-dragover');
        });
        el.addEventListener('dragleave', () => el.classList.remove('is-dragover'));
        el.addEventListener('drop', (e) => {
            e.preventDefault();
            el.classList.remove('is-dragover');
            Array.from(e.dataTransfer.files).forEach(readFile);
        });

        const input = el.querySelector('input[type=file]');
        if (input) {
            input.addEventListener('change', (e) => Array.from(e.target.files).forEach(readFile));
        }
    },

    // A Bulma-flavored qualitative palette so trace colors feel like part of
    // the same UI instead of Plotly's unrelated default set.
    _colorway: ['#3298dc', '#f14668', '#48c78e', '#ffdd57', '#485fc7', '#00d1b2', '#ff6b81', '#9b59b6'],

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

    // Print-style export: white paper, black grid/axes/text, black curves
    // thicker than the gridlines. Traces with showlegend === false are the
    // Smith chart's background resistance/reactance circles (drawn as plain
    // traces, not real Plotly gridlines) — kept grid-thin instead of
    // data-thick so they don't become indistinguishable from the actual S11/
    // S22 curve.
    _monochromeLayout: function (layout) {
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

    // On screen, color alone (touchstoneInterop.fileColor in TouchstonePlot.fs)
    // is enough to tell files apart. The print/monochrome export flattens
    // every line to black, so files need a second cue there — a per-file
    // dash pattern, assigned only for this export and never shown on screen.
    _dashCycle: ['solid', 'dot', 'dash', 'dashdot', 'longdash', 'longdashdot'],

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

    _fileDashFor: function (traceName) {
        const label = window.touchstoneInterop._fileLabelFor(traceName);
        if (!label) return 'solid';
        let h = 0;
        for (let i = 0; i < label.length; i++) {
            h = (h * 31 + label.charCodeAt(i)) | 0;
        }
        const cycle = window.touchstoneInterop._dashCycle;
        return cycle[((h % cycle.length) + cycle.length) % cycle.length];
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
    // chart at once (a file's traces are spread across up to 4 separate
    // figures — magnitude/phase/smith/group-delay). Uses the same Plotly
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

    _monochromeData: function (data) {
        return data.map((trace) => {
            const isGridLine = trace.showlegend === false;
            const dash = isGridLine ? trace.line && trace.line.dash : window.touchstoneInterop._fileDashFor(trace.name);
            return Object.assign({}, trace, {
                line: Object.assign({}, trace.line, { color: '#000000', width: isGridLine ? 1 : 2.5, dash: dash }),
            });
        });
    },

    // JPEG has no alpha channel, so a transparent/dark-mode background would
    // export as black. Swap the whole chart to a monochrome print-style
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
                        const monoLayout = window.touchstoneInterop._monochromeLayout(fig.layout);
                        const monoData = window.touchstoneInterop._monochromeData(fig.data);
                        Plotly.react(divId, monoData, monoLayout, window.touchstoneInterop._config(divId))
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
        window.touchstoneInterop._lastFigures[divId] = fig;
        // Plotly.react diffs against the existing plot and patches it in place
        // instead of tearing down and rebuilding the whole chart like newPlot.
        // _bindTdrGateDrag must wait for this to actually finish — binding
        // synchronously right after calling (not awaiting) react() attached
        // to chart-tdr's event emitter before Plotly had finished setting it
        // up on that div's very first render, silently missing the swap
        // renderChart itself waits for elsewhere too.
        Plotly.react(divId, fig.data, window.touchstoneInterop._themeLayout(fig.layout), window.touchstoneInterop._config(divId)).then(
            () => window.touchstoneInterop._bindTdrGateDrag(divId)
        );
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
    setupCollapsibleCharts: function () {
        document.querySelectorAll('details.chart-section').forEach((details) => {
            if (details.dataset.resizeBound) return;
            details.dataset.resizeBound = 'true';
            details.addEventListener('toggle', () => {
                if (!details.open) return;
                details.querySelectorAll('.js-plotly-plot').forEach((el) => Plotly.Plots.resize(el));
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
            el.addEventListener('click', () => {
                const nowHidden = !window.touchstoneInterop._hiddenFiles.has(fileName);
                if (nowHidden) {
                    window.touchstoneInterop._hiddenFiles.add(fileName);
                } else {
                    window.touchstoneInterop._hiddenFiles.delete(fileName);
                }
                el.style.opacity = nowHidden ? '0.25' : '1';
                window.touchstoneInterop._setFileVisible(fileName, !nowHidden);
            });
        });
    }
};
