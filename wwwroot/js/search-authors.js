(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const panel = document.querySelector('.search-panel[data-mode="author"]');
    if (!panel) return;

    const form = panel.querySelector('form');
    const input = panel.querySelector('input');
    const statusEl = panel.querySelector('.search-status');
    const resultsEl = panel.querySelector('.search-results');
    const historyEl = panel.querySelector('.search-history-list');
    const historyContainer = panel.querySelector('.search-history');
    if (!form || !input || !statusEl || !resultsEl) return;

    const paginationEl = document.createElement('div');
    paginationEl.className = 'search-pagination hidden';
    paginationEl.innerHTML = `
        <button type="button" class="btn btn-ghost search-page-prev">Prev</button>
        <div class="search-pagination-info"></div>
        <button type="button" class="btn btn-ghost search-page-next">Next</button>
    `;
    resultsEl.parentNode.insertBefore(paginationEl, resultsEl);
    const pagination = {
        el: paginationEl,
        info: paginationEl.querySelector('.search-pagination-info'),
        prev: paginationEl.querySelector('.search-page-prev'),
        next: paginationEl.querySelector('.search-page-next')
    };

    const bookStatusEl = document.createElement('div');
    bookStatusEl.className = 'status search-status hidden';
    const bookResultsEl = document.createElement('div');
    bookResultsEl.className = 'search-results results-grid author-books-grid';
    resultsEl.insertAdjacentElement('afterend', bookStatusEl);
    bookStatusEl.insertAdjacentElement('afterend', bookResultsEl);

    const wikiImageCache = new Map();

    const state = {
        query: '',
        status: 'Ready.',
        results: [],
        total: 0,
        page: 1,
        pageSize: 20,
        historyLoaded: false
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

    const updatePagination = () => {
        if (!pagination?.el) return;
        const total = state.total || 0;
        const pageSize = state.pageSize || 20;
        const page = state.page || 1;
        const totalPages = Math.max(1, Math.ceil(total / pageSize));
        if (!total || totalPages <= 1) {
            pagination.el.classList.add('hidden');
            return;
        }
        pagination.el.classList.remove('hidden');
        if (pagination.info) {
            const showing = state.results ? state.results.length : 0;
            pagination.info.textContent = `Page ${page} of ${totalPages} · Showing ${showing} of ${total} results`;
        }
        if (pagination.prev) pagination.prev.disabled = page <= 1;
        if (pagination.next) pagination.next.disabled = page >= totalPages;
    };

    const fetchWikiThumb = async (name) => {
        if (!name) return null;
        if (wikiImageCache.has(name)) return wikiImageCache.get(name);
        try {
            const url = `https://en.wikipedia.org/w/api.php?action=query&origin=*&format=json&prop=pageimages&piprop=thumbnail&pithumbsize=240&titles=${encodeURIComponent(name)}`;
            const res = await fetch(url);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            const pages = data?.query?.pages;
            if (pages) {
                const first = Object.values(pages)[0];
                const thumb = first?.thumbnail?.source;
                if (thumb) {
                    wikiImageCache.set(name, thumb);
                    return thumb;
                }
            }
        } catch (err) {
            console.warn('Wiki image lookup failed', err);
        }
        wikiImageCache.set(name, null);
        return null;
    };

    const setAuthorAvatar = (author, avatarEl) => {
        const initial = author.name ? (author.name[0] || '?') : '👤';
        const applyInitial = () => {
            avatarEl.innerHTML = '';
            avatarEl.textContent = initial;
            avatarEl.classList.add('author-avatar--placeholder');
        };

        const attemptImg = (url) => {
            return new Promise(resolve => {
                if (!url) return resolve(false);
                const img = new Image();
                img.alt = `${author.name || 'Author'} portrait`;
                img.loading = 'lazy';
                img.onerror = () => resolve(false);
                img.onload = () => {
                    const tooSmall = !img.naturalWidth || !img.naturalHeight || img.naturalWidth <= 1 || img.naturalHeight <= 1;
                    if (tooSmall) {
                        resolve(false);
                        return;
                    }
                    avatarEl.classList.remove('author-avatar--placeholder');
                    avatarEl.innerHTML = '';
                    avatarEl.appendChild(img);
                    resolve(true);
                };
                img.src = url;
            });
        };

        applyInitial();
        const olKey = author.key || author.slug || null;
        const olCover = olKey ? `https://covers.openlibrary.org/a/olid/${olKey}-L.jpg` : null;

        attemptImg(olCover).then(ok => {
            if (ok) return;
            const alt = author.image?.url || null;
            attemptImg(alt).then(ok2 => {
                if (ok2) return;
                fetchWikiThumb(author.name).then(url => {
                    if (url) {
                        attemptImg(url);
                    }
                });
            });
        });
    };

    const renderResults = () => {
        resultsEl.innerHTML = '';
        if (!state.results.length) {
            updatePagination();
            return;
        }

        const list = document.createElement('div');
        list.className = 'author-results';

        state.results.forEach(author => {
            const row = document.createElement('div');
            row.className = 'author-row';
            row.addEventListener('click', () => showAuthorBooks(author.name));

            const avatar = document.createElement('div');
            avatar.className = 'author-avatar';
            setAuthorAvatar(author, avatar);

            const body = document.createElement('div');
            body.className = 'author-body';

            const header = document.createElement('div');
            header.className = 'author-header';

            const name = document.createElement('div');
            name.className = 'author-name';
            name.textContent = author.name || 'Unknown author';

            const meta = document.createElement('div');
            meta.className = 'author-meta';
            const life = [author.birth_date, author.death_date].filter(Boolean).join(' – ');
            if (life) {
                const lifeSpan = document.createElement('span');
                lifeSpan.textContent = life;
                meta.appendChild(lifeSpan);
            }
            if (typeof author.work_count === 'number') {
                const works = document.createElement('span');
                works.textContent = `${author.work_count} work(s)`;
                meta.appendChild(works);
            }

            header.appendChild(name);
            header.appendChild(meta);

            const topWork = document.createElement('div');
            topWork.className = 'author-topwork';
            topWork.textContent = author.top_work ? `Top work: ${author.top_work}` : 'Top work: Unknown';

            const subjects = document.createElement('div');
            subjects.className = 'author-subjects';
            const tags = Array.isArray(author.top_subjects) ? author.top_subjects : [];
            if (tags.length) {
                subjects.innerHTML = tags.map(tag => `<span class="author-tag">${tag}</span>`).join('');
            }

            body.appendChild(header);
            body.appendChild(topWork);
            if (subjects.innerHTML) {
                body.appendChild(subjects);
            }

            row.appendChild(avatar);
            row.appendChild(body);
            list.appendChild(row);
        });

        resultsEl.appendChild(list);
        updatePagination();
    };

    const showAuthorBooks = async (authorName) => {
        if (!authorName) return;
        // Clear author list when drilling into books
        state.total = 0;
        state.results = [];
        resultsEl.innerHTML = '';
        resultsEl.classList.add('hidden');
        if (pagination?.el) pagination.el.classList.add('hidden');
        statusEl.textContent = '';
        bookResultsEl.innerHTML = '';
        bookStatusEl.classList.remove('hidden');
        bookStatusEl.textContent = `Loading books by ${authorName}…`;
        try {
            const res = await fetch(`/search?query=${encodeURIComponent(authorName)}&mode=author&page=1`);
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                bookStatusEl.textContent = detail
                    ? `Error loading books (${res.status}): ${detail}`
                    : `Error loading books (${res.status}).`;
                return;
            }
            const data = await res.json();
            const books = Array.isArray(data?.books) ? data.books : [];
            if (!books.length) {
                bookStatusEl.textContent = `No books found for ${authorName}.`;
                return;
            }
            bookStatusEl.textContent = `Books by ${authorName} · ${data?.total ?? books.length} result(s).`;
            books.forEach(book => {
                bookResultsEl.appendChild(app.createBookCard(book, {
                    showWanted: true,
                    sourceInRightPill: true,
                    showIsbnInline: true
                }));
            });
        } catch (err) {
            console.error('Author books load failed', err);
            bookStatusEl.textContent = 'Error loading books.';
        }
    };

    const runSearch = async (query, page = 1) => {
        state.query = query;
        state.page = page;
        state.results = [];
        state.total = 0;
        bookResultsEl.innerHTML = '';
        bookStatusEl.classList.add('hidden');
        resultsEl.classList.remove('hidden');
        setStatus('Searching…');
        resultsEl.innerHTML = '';
        updatePagination();

        try {
            const res = await fetch(`/search/authors?query=${encodeURIComponent(query)}&page=${page}`);
            if (!res.ok) {
                setStatus('Error: ' + res.status);
                return;
            }

            const data = await res.json();
            const authors = Array.isArray(data?.authors) ? data.authors : [];
            if (!authors.length) {
                state.results = [];
                state.total = typeof data?.total === 'number' ? data.total : 0;
                setStatus('No results found.');
                updatePagination();
                return;
            }

            state.results = authors;
            state.total = typeof data?.total === 'number' ? data.total : authors.length;
            state.pageSize = typeof data?.pageSize === 'number' ? data.pageSize : state.pageSize;
            state.page = typeof data?.page === 'number' ? data.page : page;
            setStatus(`Found ${state.total} author(s).`);
            renderResults();
        } catch (err) {
            console.error(err);
            setStatus('Error talking to Bookworm API.');
            updatePagination();
        }
    };

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
            const res = await fetch(`/search/history/authors?query=${encodeURIComponent(query)}`, { method: 'DELETE' });
            if (!res.ok && res.status !== 404) {
                console.warn('Failed to delete author history', res.status);
            }
        } catch (err) {
            console.warn('Delete author history failed', err);
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
            const res = await fetch('/search/history/authors?take=8');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            const entries = Array.isArray(data?.items) ? data.items : [];
            if (!entries.length) {
                showEmpty('No recent author searches.');
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
            console.warn('Failed to load author search history', err);
            showEmpty('Unable to load search history.');
        }
    }

    state.runSearch = runSearch;

    if (pagination) {
        pagination.prev.addEventListener('click', () => {
            const prevPage = Math.max(1, (state.page || 1) - 1);
            runSearch(state.query || input.value.trim(), prevPage);
        });
        pagination.next.addEventListener('click', () => {
            const totalPages = Math.max(1, Math.ceil((state.total || 0) / (state.pageSize || 20)));
            const nextPage = Math.min(totalPages, (state.page || 1) + 1);
            runSearch(state.query || input.value.trim(), nextPage);
        });
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

    window.bookwormAuthorSearch = {
        restore() {
            input.value = state.query;
            applyStatus();
            renderResults();
            updatePagination();
        },
        search(query) {
            if (!query) return;
            input.value = query;
            runSearch(query);
        }
    };
})();
