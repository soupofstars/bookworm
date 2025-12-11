(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-owned-status');
    const resultsEl = document.getElementById('hardcover-owned-results');
    const searchInput = document.getElementById('hardcover-owned-search-input');
    const searchClear = document.getElementById('hardcover-owned-search-clear');
    const sortSelect = document.getElementById('hardcover-owned-sort');

    let cachedBooks = [];
    let listMeta = null;
    let searchQuery = '';
    let sortKey = 'title-asc';
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
            return;
        }

        sorted.forEach(book => {
            resultsEl.appendChild(app.createBookCard(book, { showWanted: false, useWantedLayout: false }));
        });

        const listLabel = (() => {
            if (!listMeta) return 'Hardcover list';
            const ownerSuffix = listMeta.owner ? ` by ${listMeta.owner}` : '';
            if (listMeta.name) return `Hardcover list “${listMeta.name}”${ownerSuffix}`;
            return `Hardcover list #${listMeta.id}${ownerSuffix}`;
        })();

        statusEl.textContent = normalizedQuery
            ? `Showing ${sorted.length} of ${total} book(s) in ${listLabel}.`
            : `You have ${total} book(s) in ${listLabel}.`;
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
                    try {
                        const data = await res.json();
                        if (data && (data.error || data.detail || data.title)) {
                            message = data.error || data.detail || data.title || message;
                        }
                    } catch {
                        try {
                            const text = await res.text();
                            if (text) message = text;
                        } catch {
                            // ignore
                        }
                    }
                    if (statusEl) {
                        statusEl.textContent = message;
                    }
                    loaded = false;
                    throw new Error(message);
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
                loaded = true;
                renderBooks();
                return cachedBooks;
            } catch (err) {
                console.error('Failed to load Hardcover owned list', err);
                throw err;
            } finally {
                loading = false;
            }
        })();

        return lastLoadPromise;
    }

    if (searchInput) {
        searchInput.addEventListener('input', (event) => {
            searchQuery = event.target.value || '';
            renderBooks();
        });
    }
    if (searchClear && searchInput) {
        searchClear.addEventListener('click', () => {
            searchInput.value = '';
            searchQuery = '';
            renderBooks();
            searchInput.focus();
        });
    }
    if (sortSelect) {
        sortSelect.value = sortKey;
        sortSelect.addEventListener('change', (event) => {
            sortKey = event.target.value || 'title-asc';
            renderBooks();
        });
    }

    window.bookwormHardcoverOwned = {
        ensureLoaded(forceReload) {
            return loadOwnedList(!!forceReload);
        },
        reload() {
            return loadOwnedList(true);
        }
    };
})();
