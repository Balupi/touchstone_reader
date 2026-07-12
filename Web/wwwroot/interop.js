window.touchstoneInterop = {
    setupDropZone: function (elementId, dotNetRef) {
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

    _monochromeData: function (data) {
        return data.map((trace) => {
            const isGridLine = trace.showlegend === false;
            return Object.assign({}, trace, {
                line: Object.assign({}, trace.line, { color: '#000000', width: isGridLine ? 1 : 2.5 }),
            });
        });
    },

    // JPEG has no alpha channel, so a transparent/dark-mode background would
    // export as black. Swap the whole chart to a monochrome print-style
    // figure just for the download, then swap back — replaces the default
    // camera button since its own download path always captures the current
    // on-screen colors as-is.
    // Triggers a browser download of `text` as `filename` via a throwaway
    // Blob URL + synthetic anchor click — no server round-trip needed since
    // the CSV is already fully built on the .NET side.
    _downloadText: function (filename, text) {
        const blob = new Blob([text], { type: 'text/csv' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);
    },

    _config: function (divId) {
        return {
            responsive: true,
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
                        const fig = window.touchstoneInterop._lastFigures[divId];
                        if (fig && fig.csv) window.touchstoneInterop._downloadText(divId + '.csv', fig.csv);
                    },
                },
            ],
        };
    },

    renderChart: function (divId, figureJson, csv) {
        const el = document.getElementById(divId);
        if (!el) return;
        const fig = JSON.parse(figureJson);
        fig.csv = csv;
        window.touchstoneInterop._lastFigures[divId] = fig;
        // Plotly.react diffs against the existing plot and patches it in place
        // instead of tearing down and rebuilding the whole chart like newPlot.
        Plotly.react(divId, fig.data, window.touchstoneInterop._themeLayout(fig.layout), window.touchstoneInterop._config(divId));
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
    }
};
