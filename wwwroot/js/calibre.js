(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('calibre-status');
    const resultsEl = document.getElementById('calibre-results');
    const syncBtn = document.getElementById('btn-calibre-sync');
    const searchInput = document.getElementById('calibre-search-input');
    const searchClear = document.getElementById('calibre-search-clear');
    const sortSelect = document.getElementById('calibre-sort');
    if (!statusEl || !resultsEl) return;

    let loaded = false;
    let loading = false;
    let syncing = false;
    let searchQuery = '';
    let lastSyncText = '';
    let allBooks = [];
    let searchCache = new WeakMap();
    let sortKey = 'title-asc';

    function toArray(value) {
        if (Array.isArray(value)) return value;
        if (value == null) return [];
        return [value].filter(Boolean);
    }

    const normalizeCalibreBook = (app && typeof app.normalizeCalibreBook === 'function')
        ? app.normalizeCalibreBook
        : function normalizeCalibreBook(raw) {
            const copy = Object.assign({}, raw);
            copy.source = 'calibre';
            copy.calibre_id = raw.id;
            copy.id = `calibre-${raw.id}`;
            copy.slug = copy.id;

            const authors = toArray(raw.authorNames || raw.authors || [])
                .map(value => typeof value === 'string' ? value.trim() : String(value))
                .filter(Boolean);
            if (authors.length) {
                copy.author_names = authors;
                copy.authors = authors;
            }

            copy.formats = toArray(raw.formats).map(String);
            copy.tags = toArray(raw.tags).map(String);
            copy.publisher = raw.publisher || null;
            copy.series = raw.series || null;

            const isbn = typeof raw.isbn === 'string' ? raw.isbn.trim() : '';
            if (isbn) {
                if (isbn.replace(/[-\s]/g, '').length > 10) {
                    copy.isbn13 = isbn;
                } else {
                    copy.isbn10 = isbn;
                }
            }

            if (typeof raw.fileSizeMb === 'number') {
                copy.size_mb = Math.round(raw.fileSizeMb * 10) / 10;
            }

            if (typeof raw.rating === 'number') {
                copy.rating = raw.rating > 5 ? Math.round((raw.rating / 2) * 10) / 10 : raw.rating;
            }

            copy.description = raw.description || (raw.path ? `Calibre path: ${raw.path}` : copy.description);
            const resolvedCover = raw.coverUrl || (raw.hasCover && raw.id != null ? `/calibre-covers/${raw.id}.jpg` : null);
            if (resolvedCover) {
                copy.image = { url: resolvedCover };
                copy.coverUrl = resolvedCover;
            }
            return copy;
        };

    function buildSearchKey(book) {
        if (!book) return '';
        if (searchCache.has(book)) {
            return searchCache.get(book);
        }
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
        push(book.authors);
        push(book.author_names);
        push(book.series);
        push(book.publisher);
        push(book.tags);
        push(book.genre);
        push(book.base_genres);
        push(book.formats);
        if (typeof book.cached_tags === 'string') {
            push(book.cached_tags);
        } else if (Array.isArray(book.cached_tags)) {
            book.cached_tags.forEach(push);
        }
        const isbn = (book.isbn13 || book.isbn10 || '').trim();
        if (isbn) push(isbn);

        const text = parts.join(' ');
        searchCache.set(book, text);
        return text;
    }

    function matchesQuery(book, normalizedQuery) {
        if (!normalizedQuery) return true;
        return buildSearchKey(book).includes(normalizedQuery);
    }

    function primaryAuthor(book) {
        const authors = Array.isArray(book.author_names) && book.author_names.length
            ? book.author_names
            : Array.isArray(book.authors) && book.authors.length
                ? book.authors
                : [];
        return authors.length ? authors[0] : '';
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

    function renderBooks() {
        if (!allBooks.length) return;
        resultsEl.innerHTML = '';
        const rawQuery = searchQuery || '';
        const normalizedQuery = rawQuery.trim().toLowerCase();
        const visible = normalizedQuery
            ? allBooks.filter(book => matchesQuery(book, normalizedQuery))
            : allBooks;
        const sorted = sortBooks(visible);

        if (!sorted.length) {
            statusEl.textContent = rawQuery
                ? `No matches for "${rawQuery.trim()}". ${lastSyncText}`
                : `No books found in Calibre mirror. ${lastSyncText}`;
            return;
        }

        let rendered = 0;
        sorted.forEach(book => {
            try {
                resultsEl.appendChild(app.createBookCard(book, {
                    showWanted: false,
                    showAddToLibrary: false,
                    enableViewLink: false
                }));
                rendered += 1;
            } catch (err) {
                console.error('Failed to render Calibre book', book, err);
            }
        });

        const prefix = normalizedQuery
            ? `Showing ${rendered} of ${allBooks.length} Calibre book(s).`
            : `Showing ${rendered} of ${allBooks.length} book(s).`;
        statusEl.textContent = lastSyncText
            ? `${prefix} ${lastSyncText}`
            : prefix;
    }

    async function loadCalibreBooks(force = false) {
        if (loading && !force) return;
        loading = true;
        if (force) {
            loaded = false;
            allBooks = [];
            searchCache = new WeakMap();
        }
        statusEl.textContent = 'Loading Calibre library…';
        resultsEl.innerHTML = '';

        try {
            const res = await fetch(`/calibre/books?take=0&ts=${Date.now()}`, { cache: 'no-store' });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                statusEl.textContent = detail || 'Unable to load Calibre library.';
                loading = false;
                return;
            }

            const data = await res.json();
            const books = Array.isArray(data?.books) ? data.books : [];
            const syncInfo = data?.lastSync
                ? `Last sync: ${new Date(data.lastSync).toLocaleString()}`
                : 'Not synced yet.';

            if (!books.length) {
                allBooks = [];
                lastSyncText = syncInfo;
                loaded = true;
                statusEl.textContent = data?.lastSync
                    ? 'No books in Calibre mirror. Click “Sync Calibre” to refresh.'
                    : 'Calibre not synced yet. Click “Sync Calibre” to import your library.';
                if (typeof app.setLibraryFromCalibre === 'function') {
                    app.setLibraryFromCalibre(books, syncInfo);
                }
                loaded = true;
                loading = false;
                return;
            }

            const normalized = books.map(normalizeCalibreBook);
            allBooks = normalized;
            lastSyncText = syncInfo;
            loaded = true;
            renderBooks();
            if (typeof app.setLibraryFromCalibre === 'function') {
                app.setLibraryFromCalibre(books, syncInfo);
            }
        } catch (err) {
            console.error('Failed to load Calibre library', err);
            statusEl.textContent = 'Error loading Calibre library.';
        } finally {
            loading = false;
        }
    }

    async function syncCalibreLibrary() {
        if (syncing) return;
        syncing = true;
        if (syncBtn) syncBtn.disabled = true;
        statusEl.textContent = 'Syncing Calibre library…';
        try {
            const res = await fetch('/calibre/sync', { method: 'POST' });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                statusEl.textContent = detail || 'Unable to sync Calibre library.';
                return;
            }
            await loadCalibreBooks(true);
        } catch (err) {
            console.error('Calibre sync failed', err);
            statusEl.textContent = 'Error syncing Calibre library.';
        } finally {
            syncing = false;
            if (syncBtn) syncBtn.disabled = false;
        }
    }

    window.bookwormCalibre = {
        ensureLoaded() {
            if (!loaded) {
                loadCalibreBooks();
            }
        },
        reload() {
            loaded = false;
            loadCalibreBooks();
        },
        sync: syncCalibreLibrary
    };

    if (syncBtn) {
        syncBtn.addEventListener('click', syncCalibreLibrary);
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

    document.addEventListener('DOMContentLoaded', () => {
        if (syncBtn) {
            syncBtn.disabled = false;
        }
    });
})();
