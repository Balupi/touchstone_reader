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
        Plotly.newPlot(divId, fig.data, fig.layout, { responsive: true });
    }
};
