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

        el.addEventListener('dragover', (e) => e.preventDefault());
        el.addEventListener('drop', (e) => {
            e.preventDefault();
            Array.from(e.dataTransfer.files).forEach(readFile);
        });

        const input = el.querySelector('input[type=file]');
        if (input) {
            input.addEventListener('change', (e) => Array.from(e.target.files).forEach(readFile));
        }
    },

    renderChart: function (divId, figureJson) {
        const el = document.getElementById(divId);
        if (!el) return;
        const fig = JSON.parse(figureJson);
        // Plotly.react diffs against the existing plot and patches it in place
        // instead of tearing down and rebuilding the whole chart like newPlot.
        Plotly.react(divId, fig.data, fig.layout, { responsive: true });
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
