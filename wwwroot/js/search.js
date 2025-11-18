(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const form = document.getElementById('search-form');
    if (!form) return;

    const queryInput = document.getElementById('query');
    const statusEl = document.getElementById('status');
    const resultsEl = document.getElementById('results');

    const searchState = {
        lastQuery: '',
        lastResults: [],
        lastStatus: 'Ready.'
    };

    function applyStatus() {
        statusEl.textContent = searchState.lastStatus;
    }

    function updateStatus(text) {
        searchState.lastStatus = text;
        applyStatus();
    }

    function renderSections(title, books) {
        const heading = document.createElement('div');
        heading.className = 'search-section-title';
        heading.textContent = title;
        resultsEl.appendChild(heading);

        const grid = document.createElement('div');
        grid.className = 'results-grid';
        books.forEach(book => grid.appendChild(app.createBookCard(book)));
        resultsEl.appendChild(grid);
    }

    function renderResults() {
        resultsEl.innerHTML = '';
        if (!searchState.lastResults.length) return;

        const normalized = (searchState.lastQuery || '').trim().toLowerCase();
        const matches = [];
        const weakMatches = [];
        const suggestions = [];

        searchState.lastResults.forEach(book => {
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

        if (matches.length) {
            renderSections('Matches', matches);
        }
        if (weakMatches.length) {
            renderSections('Weak matches', weakMatches);
        }
        if (suggestions.length) {
            const label = (matches.length || weakMatches.length) ? 'Suggested' : 'Related titles';
            renderSections(label, suggestions);
        }
    }

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        const query = queryInput.value.trim();
        if (!query) return;

        searchState.lastQuery = query;
        searchState.lastResults = [];
        updateStatus('Searching…');
        resultsEl.innerHTML = '';

        try {
            const res = await fetch('/search?query=' + encodeURIComponent(query));
            if (!res.ok) {
                updateStatus('Error: ' + res.status);
                return;
            }

            const data = await res.json();
            const books = Array.isArray(data?.books) ? data.books : [];
            if (!books.length) {
                searchState.lastResults = [];
                updateStatus('No results found.');
                return;
            }

            searchState.lastResults = books;
            updateStatus(`Found ${books.length} result(s).`);
            renderResults();
        } catch (err) {
            console.error(err);
            updateStatus('Error talking to Bookworm API.');
        }
    });

    applyStatus();

    window.bookwormSearch = {
        restore() {
            queryInput.value = searchState.lastQuery;
            applyStatus();
            renderResults();
        }
    };
})();
