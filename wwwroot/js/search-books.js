(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const panels = document.querySelectorAll('.search-panel[data-mode="title"]');
    if (!panels.length) return;

    const states = {};

    async function deleteHistoryEntry(query, state) {
        try {
            const res = await fetch(`/search/history?query=${encodeURIComponent(query)}`, { method: 'DELETE' });
            if (!res.ok && res.status !== 404) {
                console.warn('Failed to delete history entry', res.status);
            }
        } catch (err) {
            console.warn('Delete history failed', err);
        } finally {
            renderHistory(state);
        }
    }

    function positionHistory(state) {
        if (!state.historyContainer || state.historyContainer.classList.contains('hidden')) return;
        const inputEl = state.input;
        if (!inputEl) return;
        const rect = inputEl.getBoundingClientRect();
        const margin = 16;
        const viewportWidth = window.innerWidth;
        const desiredWidth = Math.min(viewportWidth - margin * 2, Math.max(rect.width + 80, 420));
        const maxLeft = window.scrollX + viewportWidth - desiredWidth - margin;
        const left = Math.min(Math.max(window.scrollX + rect.left - 20, margin), maxLeft);
        const top = rect.bottom + 6 + window.scrollY;
        state.historyContainer.style.position = 'fixed';
        state.historyContainer.style.left = `${left}px`;
        state.historyContainer.style.width = `${desiredWidth}px`;
        state.historyContainer.style.right = 'auto';
        state.historyContainer.style.top = `${top}px`;
    }

    async function renderHistory(state) {
        if (!state.historyEl || !state.historyContainer) return;
        state.historyEl.innerHTML = '';
        state.historyContainer.classList.remove('hidden');
        const showEmpty = (message) => {
            const row = document.createElement('div');
            row.className = 'search-history-entry search-history-empty';
            row.textContent = message;
            state.historyEl.appendChild(row);
        };
        try {
            const res = await fetch('/search/history?take=8');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            const entries = Array.isArray(data?.items) ? data.items : [];
            if (!entries.length) {
                showEmpty('No recent searches yet.');
                return;
            }
            entries.forEach(entry => {
                const row = document.createElement('div');
                row.className = 'search-history-entry';

                const btn = document.createElement('button');
                btn.type = 'button';
                btn.textContent = entry.query;
                btn.addEventListener('click', () => {
                    state.input.value = entry.query;
                    state.runSearch(entry.query);
                    state.historyContainer.classList.add('hidden');
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
                    deleteHistoryEntry(entry.query, state);
                });

                const left = document.createElement('div');
                left.className = 'search-history-left';
                left.appendChild(btn);
                left.appendChild(count);

                row.appendChild(left);
                row.appendChild(remove);
                state.historyEl.appendChild(row);
            });
            positionHistory(state);
        } catch (err) {
            console.warn('Failed to load search history', err);
            showEmpty('Unable to load search history.');
        }
    }

    function updatePagination(state) {
        if (!state.pagination || !state.pagination.el) return;
        const { el, info, prev, next } = state.pagination;
        const total = state.total || 0;
        const pageSize = state.pageSize || 20;
        const page = state.page || 1;
        const totalPages = Math.max(1, Math.ceil(total / pageSize));
        if (totalPages <= 1) {
            el.classList.add('hidden');
            return;
        }
        el.classList.remove('hidden');
        if (info) {
            const showing = total
                ? Math.min(page * pageSize, total)
                : (state.results ? state.results.length : 0);
            info.textContent = `Page ${page} of ${totalPages} · Showing ${showing} of ${total || showing} results`;
        }
        if (prev) prev.disabled = page <= 1;
        if (next) next.disabled = page >= totalPages;
    }

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
            books.forEach(book => grid.appendChild(app.createBookCard(book, {
                showWanted: true,
                sourceInRightPill: true,
                showIsbnInline: true
            })));
            resultsEl.appendChild(grid);
        };

        if (matches.length) renderSection('Matches', matches);
        if (weakMatches.length) renderSection('Weak matches', weakMatches);
        if (suggestions.length) {
            renderSection(matches.length || weakMatches.length ? 'Suggested' : 'Related titles', suggestions);
        }

        updatePagination(state);
    }

    function attachPanel(panel) {
        const mode = panel.dataset.mode || 'title';
        const form = panel.querySelector('form');
        const input = panel.querySelector('input');
        const statusEl = panel.querySelector('.search-status');
        const resultsEl = panel.querySelector('.search-results');
        const historyEl = panel.querySelector('.search-history-list');
        const historyContainer = panel.querySelector('.search-history');
        if (!form || !input || !statusEl || !resultsEl) return;

        const statusRow = document.createElement('div');
        statusRow.className = 'search-status-row';
        statusEl.parentNode.insertBefore(statusRow, statusEl);
        statusRow.appendChild(statusEl);

        const paginationEl = document.createElement('div');
        paginationEl.className = 'search-pagination hidden';
        paginationEl.innerHTML = `
            <button type="button" class="btn btn-ghost search-page-prev">Prev</button>
            <div class="search-pagination-info"></div>
            <button type="button" class="btn btn-ghost search-page-next">Next</button>
        `;
        statusRow.appendChild(paginationEl);
        const pagination = {
            el: paginationEl,
            info: paginationEl.querySelector('.search-pagination-info'),
            prev: paginationEl.querySelector('.search-page-prev'),
            next: paginationEl.querySelector('.search-page-next')
        };

        const state = states[mode] = {
            mode,
            form,
            input,
            statusEl,
            resultsEl,
            historyEl,
            historyContainer,
            pagination,
            query: '',
            results: [],
            status: 'Ready.',
            total: 0,
            page: 1,
            pageSize: 20
        };

        const applyStatus = () => {
            const pageInfo = state.total && state.total > state.results.length
                ? ` · page ${state.page}`
                : '';
            statusEl.textContent = `${state.status}${pageInfo}`;
        };

        const setStatus = text => {
            state.status = text;
            applyStatus();
        };

        const runSearch = async (query, page = 1) => {
            state.query = query;
            state.page = page;
            state.results = [];
            setStatus('Searching…');
            resultsEl.innerHTML = '';

            try {
                const res = await fetch(`/search?query=${encodeURIComponent(query)}&mode=${encodeURIComponent(mode)}&page=${page}`);
                if (!res.ok) {
                    setStatus('Error: ' + res.status);
                    return;
                }

                const data = await res.json();
                const books = Array.isArray(data?.books) ? data.books : [];
                if (!books.length) {
                    state.results = [];
                    setStatus('No results found.');
                    state.total = 0;
                    return;
                }

                state.results = books;
                state.total = typeof data?.total === 'number' ? data.total : books.length;
                state.pageSize = typeof data?.pageSize === 'number' ? data.pageSize : 20;
                state.page = typeof data?.page === 'number' ? data.page : page;
                setStatus(`Found ${state.total} result(s).`);
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
            if (historyContainer) historyContainer.classList.add('hidden');
        });

        if (pagination) {
            pagination.prev.addEventListener('click', () => {
                const prevPage = Math.max(1, state.page - 1);
                runSearch(state.query || input.value.trim(), prevPage);
            });
            pagination.next.addEventListener('click', () => {
                const totalPages = Math.max(1, Math.ceil((state.total || 0) / (state.pageSize || 20)));
                const nextPage = Math.min(totalPages, state.page + 1);
                runSearch(state.query || input.value.trim(), nextPage);
            });
        }

        input.addEventListener('focus', () => {
            renderHistory(state);
            updatePagination(state);
        });
        input.addEventListener('input', () => {
            renderHistory(state);
        });
        input.addEventListener('blur', () => {
            setTimeout(() => {
                if (historyContainer) historyContainer.classList.add('hidden');
            }, 120);
        });

        window.addEventListener('resize', () => positionHistory(state));
        window.addEventListener('scroll', () => positionHistory(state), { passive: true });

        applyStatus();
        updatePagination(state);
    }

    panels.forEach(attachPanel);

    window.bookwormBookSearch = {
        restore(mode = 'title') {
            const state = states[mode];
            if (!state) return;
            state.input.value = state.query;
            state.statusEl.textContent = state.status;
            classifyResults(state);
            updatePagination(state);
        },
        search(mode = 'title', query) {
            const state = states[mode];
            if (!state || !query) return;
            state.input.value = query;
            state.runSearch(query);
        }
    };
})();
