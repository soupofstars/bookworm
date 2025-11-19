(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const panel = document.querySelector('.search-panel[data-mode="author"]');
    if (!panel) return;

    const form = panel.querySelector('form');
    const input = panel.querySelector('input');
    const statusEl = panel.querySelector('.search-status');
    const resultsEl = panel.querySelector('.search-results');
    if (!form || !input || !statusEl || !resultsEl) return;

    const state = {
        query: '',
        status: 'Ready.',
        results: []
    };

    const applyStatus = () => statusEl.textContent = state.status;
    const setStatus = text => {
        state.status = text;
        applyStatus();
    };

    const classifyResults = () => {
        resultsEl.innerHTML = '';
        if (!state.results.length) return;

        const grid = document.createElement('div');
        grid.className = 'results-grid';
        state.results.forEach(book => grid.appendChild(app.createBookCard(book)));
        resultsEl.appendChild(grid);
    };

    const runSearch = async (query) => {
        state.query = query;
        state.results = [];
        setStatus('Searching…');
        resultsEl.innerHTML = '';

        try {
            const res = await fetch(`/search?query=${encodeURIComponent(query)}&mode=author`);
            if (!res.ok) {
                setStatus('Error: ' + res.status);
                return;
            }

            const data = await res.json();
            const books = Array.isArray(data?.books) ? data.books : [];
            if (!books.length) {
                state.results = [];
                setStatus('No results found.');
                return;
            }

            state.results = books;
            setStatus(`Found ${books.length} result(s).`);
            classifyResults();
        } catch (err) {
            console.error(err);
            setStatus('Error talking to Bookworm API.');
        }
    };

    state.runSearch = runSearch;

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const query = input.value.trim();
        if (!query) return;
        await runSearch(query);
    });

    applyStatus();

    window.bookwormAuthorSearch = {
        restore() {
            input.value = state.query;
            applyStatus();
            classifyResults();
        },
        search(query) {
            if (!query) return;
            input.value = query;
            runSearch(query);
        }
    };
})();
