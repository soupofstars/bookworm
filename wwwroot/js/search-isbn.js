(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const panels = document.querySelectorAll('.search-panel[data-mode="isbn"]');
    if (!panels.length) return;

    const states = {};

    function classifyResults(state) {
        const { resultsEl, results, related, query } = state;
        resultsEl.innerHTML = '';
        if (!results.length && (!related || !related.length)) return;

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
        if (related && related.length) {
            renderSection('Related (title/author)', related);
        }
    }

    function attachPanel(panel) {
        const mode = panel.dataset.mode || 'isbn';
        const form = panel.querySelector('form');
        const input = panel.querySelector('input');
        const statusEl = panel.querySelector('.search-status');
        const resultsEl = panel.querySelector('.search-results');
        const historyEl = panel.querySelector('.search-history-list');
        const historyContainer = panel.querySelector('.search-history');
        if (!form || !input || !statusEl || !resultsEl) return;

        const state = states[mode] = {
            mode,
            form,
            input,
            statusEl,
            resultsEl,
            historyEl,
            historyContainer,
            query: '',
            results: [],
            related: [],
            status: 'Ready.'
        };

        const applyStatus = () => {
            statusEl.textContent = state.status;
        };

        const setStatus = text => {
            state.status = text;
            applyStatus();
        };

        const fetchRelated = async (title) => {
            if (!title) return;
            try {
                const res = await fetch(`/search?query=${encodeURIComponent(title)}&mode=title&skipHistory=true`);
                if (!res.ok) return;
                const data = await res.json();
                const books = Array.isArray(data?.books) ? data.books : [];
                if (!books.length) return;
                const primaryKey = books[0]?.slug || books[0]?.id || books[0]?.title;
                const related = books
                    .filter(b => {
                        const key = b.slug || b.id || b.title;
                        return key && key !== primaryKey;
                    })
                    .slice(0, 10);
                if (!related.length) return;
                state.related = related;
                classifyResults(state);
            } catch (err) {
                console.warn('Related fetch failed', err);
            }
        };

        const runSearch = async (query) => {
            state.query = query;
            state.results = [];
            state.related = [];
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
                const primaryTitle = books[0]?.title;
                if (primaryTitle) {
                    fetchRelated(primaryTitle);
                }
            } catch (err) {
                console.error(err);
                setStatus('Error talking to Bookworm API.');
            }
        };

        state.runSearch = runSearch;

        function positionHistory() {
            if (!historyContainer || historyContainer.classList.contains('hidden')) return;
            if (!input) return;
            const rect = input.getBoundingClientRect();
            const margin = 16;
            const viewportWidth = window.innerWidth;
            const desiredWidth = Math.min(viewportWidth - margin * 2, Math.max(rect.width + 80, 420));
            const maxLeft = window.scrollX + viewportWidth - desiredWidth - margin;
            const left = Math.min(Math.max(window.scrollX + rect.left - 20, margin), maxLeft);
            const top = rect.bottom + 6 + window.scrollY;
            historyContainer.style.position = 'fixed';
            historyContainer.style.left = `${left}px`;
            historyContainer.style.width = `${desiredWidth}px`;
            historyContainer.style.right = 'auto';
            historyContainer.style.top = `${top}px`;
        }

        async function deleteHistoryEntry(query) {
            try {
                const res = await fetch(`/search/history/isbn?query=${encodeURIComponent(query)}`, { method: 'DELETE' });
                if (!res.ok && res.status !== 404) {
                    console.warn('Failed to delete ISBN history', res.status);
                }
            } catch (err) {
                console.warn('Delete ISBN history failed', err);
            } finally {
                await renderHistory();
            }
        }

        async function renderHistory() {
            if (!historyEl || !historyContainer) return;
            historyEl.innerHTML = '';
            historyContainer.classList.remove('hidden');
            const showEmpty = (message) => {
                const row = document.createElement('div');
                row.className = 'search-history-entry search-history-empty';
                row.textContent = message;
                historyEl.appendChild(row);
            };
            try {
                const res = await fetch('/search/history/isbn?take=8');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                const entries = Array.isArray(data?.items) ? data.items : [];
                if (!entries.length) {
                    showEmpty('No recent ISBN searches.');
                    return;
                }
                entries.forEach(entry => {
                    const row = document.createElement('div');
                    row.className = 'search-history-entry';

                    const btn = document.createElement('button');
                    btn.type = 'button';
                    btn.textContent = entry.query;
                    btn.addEventListener('click', () => {
                        input.value = entry.query;
                        runSearch(entry.query);
                        historyContainer.classList.add('hidden');
                    });

                    const count = document.createElement('div');
                    count.className = 'search-history-count';
                    count.textContent = `${entry.count} result(s)`;

                    const remove = document.createElement('button');
                    remove.type = 'button';
                    remove.className = 'search-history-remove';
                    remove.textContent = '×';
                    remove.title = 'Remove from history';
                    remove.addEventListener('click', (e) => {
                        e.stopPropagation();
                        deleteHistoryEntry(entry.query);
                    });

                    const left = document.createElement('div');
                    left.className = 'search-history-left';
                    left.appendChild(btn);
                    left.appendChild(count);

                    row.appendChild(left);
                    row.appendChild(remove);
                    historyEl.appendChild(row);
                });
                positionHistory();
            } catch (err) {
                console.warn('Failed to load ISBN search history', err);
                showEmpty('Unable to load search history.');
            }
        }

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            const query = input.value.trim();
            if (!query) return;
            await runSearch(query);
            if (historyContainer) historyContainer.classList.add('hidden');
        });

        input.addEventListener('focus', () => {
            renderHistory();
        });
        input.addEventListener('input', () => {
            renderHistory();
        });
        input.addEventListener('blur', () => {
            setTimeout(() => {
                if (historyContainer) historyContainer.classList.add('hidden');
            }, 120);
        });

        window.addEventListener('resize', positionHistory);
        window.addEventListener('scroll', positionHistory, { passive: true });

        applyStatus();
    }

    panels.forEach(attachPanel);

    window.bookwormIsbnSearch = {
        restore(mode = 'isbn') {
            const state = states[mode];
            if (!state) return;
            state.input.value = state.query;
            state.statusEl.textContent = state.status;
            classifyResults(state);
        },
        search(query, mode = 'isbn') {
            const state = states[mode];
            if (!state || !query) return;
            state.input.value = query;
            state.runSearch(query);
        }
    };
})();
