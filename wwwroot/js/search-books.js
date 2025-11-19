(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const panels = document.querySelectorAll('.search-panel[data-mode="title"], .search-panel[data-mode="isbn"]');
    if (!panels.length) return;

    const states = {};

    function classifyResults(state) {
        const { resultsEl, results, query } = state;
        resultsEl.innerHTML = '';
        if (!results.length) return;

        const normalized = (query || '').trim().toLowerCase();
        const matches = [];
        const weakMatches = [];
        const suggestions = [];

        results.forEach(book => {
            const title = (book.title || '').toLowerCase();
            const editionCount = book.edition_count ?? book.users_count ?? 0;
            const ratingRaw = book.rating ?? book.ratings_average ?? book.average_rating;
            const hasRating = typeof ratingRaw === 'number' && !Number.isNaN(ratingRaw);
            const hasImage = Boolean(
                (book.image && book.image.url) ||
                book.coverUrl ||
                book.cover_i ||
                (Array.isArray(book.cached_contributors) && book.cached_contributors.some(c => c.image && c.image.url))
            );
            const lowSignal = !hasImage && editionCount <= 1 && !hasRating;
            const titleMatches = normalized && title.includes(normalized);

            if (lowSignal) {
                weakMatches.push(book);
                return;
            }

            if (titleMatches) {
                matches.push(book);
                return;
            }

            suggestions.push(book);
        });

        const renderSection = (title, books) => {
            const heading = document.createElement('div');
            heading.className = 'search-section-title';
            heading.textContent = title;
            resultsEl.appendChild(heading);

            const grid = document.createElement('div');
            grid.className = 'results-grid';
            books.forEach(book => grid.appendChild(app.createBookCard(book)));
            resultsEl.appendChild(grid);
        };

        if (matches.length) renderSection('Matches', matches);
        if (weakMatches.length) renderSection('Weak matches', weakMatches);
        if (suggestions.length) {
            renderSection(matches.length || weakMatches.length ? 'Suggested' : 'Related titles', suggestions);
        }
    }

    function attachPanel(panel) {
        const mode = panel.dataset.mode || 'title';
        const form = panel.querySelector('form');
        const input = panel.querySelector('input');
        const statusEl = panel.querySelector('.search-status');
        const resultsEl = panel.querySelector('.search-results');
        if (!form || !input || !statusEl || !resultsEl) return;

        const state = states[mode] = {
            mode,
            form,
            input,
            statusEl,
            resultsEl,
            query: '',
            results: [],
            status: 'Ready.'
        };

        const applyStatus = () => {
            statusEl.textContent = state.status;
        };

        const setStatus = text => {
            state.status = text;
            applyStatus();
        };

        const runSearch = async (query) => {
            state.query = query;
            state.results = [];
            setStatus('Searching…');
            resultsEl.innerHTML = '';

            try {
                const res = await fetch(`/search?query=${encodeURIComponent(query)}&mode=${encodeURIComponent(mode)}`);
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
                classifyResults(state);
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
    }

    panels.forEach(attachPanel);

    window.bookwormBookSearch = {
        restore(mode = 'title') {
            const state = states[mode];
            if (!state) return;
            state.input.value = state.query;
            state.statusEl.textContent = state.status;
            classifyResults(state);
        },
        search(mode = 'title', query) {
            const state = states[mode];
            if (!state || !query) return;
            state.input.value = query;
            state.runSearch(query);
        }
    };
})();
