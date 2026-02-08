(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-owned-status');
    const resultsEl = document.getElementById('hardcover-owned-results');
    const searchInput = document.getElementById('hardcover-owned-search-input');
    const searchClear = document.getElementById('hardcover-owned-search-clear');
    const sortSelect = document.getElementById('hardcover-owned-sort');
    const paginationBars = [
        {
            el: document.getElementById('hardcover-owned-pagination'),
            info: document.getElementById('hardcover-owned-pagination-info'),
            prev: document.getElementById('hardcover-owned-pagination-prev'),
            next: document.getElementById('hardcover-owned-pagination-next')
        },
        {
            el: document.getElementById('hardcover-owned-pagination-bottom'),
            info: document.getElementById('hardcover-owned-pagination-bottom-info'),
            prev: document.getElementById('hardcover-owned-pagination-bottom-prev'),
            next: document.getElementById('hardcover-owned-pagination-bottom-next')
        }
    ];

    let cachedBooks = [];
    let listMeta = null;
    let searchQuery = '';
    let sortKey = 'title-asc';
    let currentPage = 1;
    const PAGE_SIZE = 10;
    const AUTO_PAGE_INTERVAL_MS = 0; // disable auto paging (manual only)
    let autoPageTimer = null;
    let loaded = false;
    let loading = false;
    let lastLoadPromise = null;

    function normalizeBook(raw) {
        if (window.bookwormHardcover && typeof window.bookwormHardcover.normalizeBook === 'function') {
            return window.bookwormHardcover.normalizeBook(raw);
        }
        const copy = Object.assign({ source: 'hardcover' }, raw || {});
        return copy;
    }

    function primaryAuthor(book) {
        const authors = Array.isArray(book?.author_names)
            ? book.author_names
            : Array.isArray(book?.authors) ? book.authors : [];
        return authors.length ? String(authors[0]) : '';
    }

    function sortBooks(list) {
        const arr = Array.isArray(list) ? list.slice() : [];
        const byString = (getter, direction) => (a, b) => {
            const av = getter(a).toLowerCase();
            const bv = getter(b).toLowerCase();
            if (av === bv) return 0;
            return direction === 'asc' ? (av < bv ? -1 : 1) : (av > bv ? -1 : 1);
        };
        const getTitle = (book) => book?.title || '';

        switch (sortKey) {
            case 'title-desc':
                return arr.sort(byString(getTitle, 'desc'));
            case 'author-asc':
                return arr.sort(byString(primaryAuthor, 'asc'));
            case 'author-desc':
                return arr.sort(byString(primaryAuthor, 'desc'));
            case 'title-asc':
            default:
                return arr.sort(byString(getTitle, 'asc'));
        }
    }

    function buildSearchKey(book) {
        if (!book) return '';
        const parts = [];
        const push = (val) => {
            if (!val) return;
            if (Array.isArray(val)) {
                val.forEach(push);
                return;
            }
            if (typeof val === 'object') return;
            const str = String(val).trim();
            if (str) parts.push(str.toLowerCase());
        };

        push(book.title);
        push(book.slug);
        push(book.author);
        push(book.author_names);
        push(book.authors);
        push(book.tags);
        push(book.cached_tags);
        push(book.isbn13 || book.isbn_13);
        push(book.isbn10 || book.isbn_10);
        return parts.join(' ');
    }

    function matchesQuery(book, normalizedQuery) {
        if (!normalizedQuery) return true;
        const haystack = buildSearchKey(book);
        return haystack.includes(normalizedQuery);
    }

    function renderBooks() {
        if (!resultsEl || !statusEl) return;
        resultsEl.innerHTML = '';

        const total = cachedBooks.length;
        if (!total) {
            statusEl.textContent = loaded
                ? 'No books found in your Hardcover owned list yet.'
                : statusEl.textContent;
            updatePaginationUI(0, 1, 0);
            stopAutoPaging();
            return;
        }

        const normalizedQuery = (searchQuery || '').trim().toLowerCase();
        const visible = normalizedQuery
            ? cachedBooks.filter(book => matchesQuery(book, normalizedQuery))
            : cachedBooks;
        const sorted = sortBooks(visible);

        if (!sorted.length) {
            statusEl.textContent = normalizedQuery
                ? `No matches for "${searchQuery.trim()}".`
                : 'No books found in your Hardcover owned list yet.';
            updatePaginationUI(0, 1, 0);
            stopAutoPaging();
            return;
        }

        const totalVisible = sorted.length;
        const totalPages = Math.max(1, Math.ceil(totalVisible / PAGE_SIZE));
        if (currentPage > totalPages) currentPage = totalPages;
        if (currentPage < 1) currentPage = 1;

        const start = (currentPage - 1) * PAGE_SIZE;
        const end = start + PAGE_SIZE;
        const pageItems = sorted.slice(start, end);

        pageItems.forEach(book => {
            resultsEl.appendChild(app.createBookCard(book, { showWanted: false, useWantedLayout: false }));
        });

        const listLabel = (() => {
            if (!listMeta) return 'Hardcover list';
            const ownerSuffix = listMeta.owner ? ` by ${listMeta.owner}` : '';
            if (listMeta.name) return `Hardcover list “${listMeta.name}”${ownerSuffix}`;
            return `Hardcover list #${listMeta.id}${ownerSuffix}`;
        })();

        statusEl.textContent = normalizedQuery
            ? `${visible.length} match(es) in ${listLabel}. Page ${currentPage} of ${totalPages}.`
            : `${total} book(s) in ${listLabel}. Page ${currentPage} of ${totalPages}.`;

        updatePaginationUI(totalVisible, totalPages, pageItems.length);
        restartAutoPaging(totalPages);
    }

    function updatePaginationUI(totalVisible, totalPages, showingCount) {
        const shouldShow = totalVisible > PAGE_SIZE;
        paginationBars.forEach(bar => {
            if (!bar?.el) return;
            if (!shouldShow) {
                bar.el.classList.add('hidden');
                return;
            }

            bar.el.classList.remove('hidden');
            if (bar.info) {
                bar.info.textContent = `Page ${currentPage} of ${totalPages}`;
            }
            if (bar.prev) bar.prev.disabled = currentPage <= 1;
            if (bar.next) bar.next.disabled = currentPage >= totalPages;
        });
    }

    function stopAutoPaging() {
        if (autoPageTimer) {
            clearTimeout(autoPageTimer);
            autoPageTimer = null;
        }
    }

    function restartAutoPaging(totalPages) {
        stopAutoPaging();
        if (AUTO_PAGE_INTERVAL_MS <= 0 || totalPages <= 1) return;
        autoPageTimer = setTimeout(() => {
            currentPage = currentPage >= totalPages ? 1 : currentPage + 1;
            renderBooks();
        }, AUTO_PAGE_INTERVAL_MS);
    }

    async function loadOwnedList(forceReload = false) {
        if (loading && lastLoadPromise) {
            return lastLoadPromise;
        }
        if (loaded && !forceReload) {
            return Promise.resolve(cachedBooks);
        }

        loading = true;
        if (statusEl) {
            statusEl.textContent = 'Loading Hardcover owned list…';
        }
        if (resultsEl) {
            resultsEl.innerHTML = '';
        }

        lastLoadPromise = (async () => {
            try {
                const res = await fetch('/hardcover/owned');
                if (!res.ok) {
                    let message = `Unable to load Hardcover owned list (HTTP ${res.status})`;
                    let rawDetail = '';
                    try {
                        const data = await res.json();
                        if (data && (data.error || data.detail || data.title)) {
                            message = data.error || data.detail || data.title || message;
                        }
                    } catch {
                        try {
                            rawDetail = await res.text();
                            if (rawDetail) message = rawDetail;
                        } catch {
                            // ignore
                        }
                    }
                    if (rawDetail && rawDetail.trim().startsWith('{')) {
                        try {
                            const parsed = JSON.parse(rawDetail);
                            if (parsed && typeof parsed.error === 'string') {
                                message = parsed.error;
                            }
                        } catch {
                            // ignore parse errors
                        }
                    }
                    if (res.status === 429 && !message.toLowerCase().includes('throttle')) {
                        message = 'Hardcover rate limited. Please retry in a minute.';
                    }
                    if (statusEl) {
                        const suffix = cachedBooks.length ? ` Showing cached results (${cachedBooks.length}).` : '';
                        statusEl.textContent = `${message}${suffix}`;
                    }
                    loaded = cachedBooks.length > 0;
                    if (loaded) {
                        renderBooks();
                    }
                    return cachedBooks;
                }

                const data = await res.json();
                const listInfo = data?.list || null;
                if (!listInfo) {
                    cachedBooks = [];
                    listMeta = null;
                    loaded = false;
                    if (statusEl) {
                        statusEl.textContent = 'Hardcover list not found. Check the list id in Settings.';
                    }
                    return [];
                }

                const items = Array.isArray(data?.books) ? data.books : [];
                listMeta = listInfo;

                cachedBooks = items.map(normalizeBook).filter(Boolean);
                currentPage = 1;
                loaded = true;
                renderBooks();
                return cachedBooks;
            } catch (err) {
                console.error('Failed to load Hardcover owned list', err);
                if (statusEl) {
                    const message = (err && err.message) ? err.message : 'Error talking to Bookworm API.';
                    statusEl.textContent = message;
                }
                loaded = cachedBooks.length > 0;
                if (loaded) {
                    renderBooks();
                }
                return cachedBooks;
            } finally {
                loading = false;
            }
        })();

        return lastLoadPromise;
    }

    if (searchInput) {
        searchInput.addEventListener('input', (event) => {
            searchQuery = event.target.value || '';
            currentPage = 1;
            renderBooks();
        });
    }
    if (searchClear && searchInput) {
        searchClear.addEventListener('click', () => {
            searchInput.value = '';
            searchQuery = '';
            currentPage = 1;
            renderBooks();
            searchInput.focus();
        });
    }
    if (sortSelect) {
        sortSelect.value = sortKey;
        sortSelect.addEventListener('change', (event) => {
            sortKey = event.target.value || 'title-asc';
            currentPage = 1;
            renderBooks();
        });
    }
    paginationBars.forEach(bar => {
        if (bar.prev) {
            bar.prev.addEventListener('click', () => {
                if (currentPage > 1) {
                    currentPage -= 1;
                    renderBooks();
                }
            });
        }
        if (bar.next) {
            bar.next.addEventListener('click', () => {
                currentPage += 1;
                renderBooks();
            });
        }
    });

    window.bookwormHardcoverOwned = {
        ensureLoaded(forceReload) {
            return loadOwnedList(!!forceReload);
        },
        reload() {
            return loadOwnedList(true);
        }
    };

    window.addEventListener('beforeunload', () => {
        stopAutoPaging();
    });
})();
