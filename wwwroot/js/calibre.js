(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('calibre-status');
    const resultsEl = document.getElementById('calibre-results');
    const searchInput = document.getElementById('calibre-search-input');
    const searchClear = document.getElementById('calibre-search-clear');
    const sortSelect = document.getElementById('calibre-sort');
    const pageSizeSelect = document.getElementById('calibre-page-size');
    const pagination = document.getElementById('calibre-pagination');
    const paginationInfo = document.getElementById('calibre-pagination-info');
    const paginationPrev = document.getElementById('calibre-pagination-prev');
    const paginationNext = document.getElementById('calibre-pagination-next');
    const paginationBottom = document.getElementById('calibre-pagination-bottom');
    const paginationBottomInfo = document.getElementById('calibre-pagination-bottom-info');
    const paginationBottomPrev = document.getElementById('calibre-pagination-bottom-prev');
    const paginationBottomNext = document.getElementById('calibre-pagination-bottom-next');
    if (!statusEl || !resultsEl) return;

    let loaded = false;
    let loading = false;
    let searchQuery = '';
    let lastSyncText = '';
    let allBooks = [];
    let searchCache = new WeakMap();
    let sortKey = 'title-asc';
    let page = 1;
    let pageSize = 30;

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
            copy.hardcover_id = raw.hardcoverId || raw.hardcover_id || null;
            copy.hardcoverId = copy.hardcover_id;
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

    function updatePaginationUI(totalVisible, totalPages, showingCount) {
        const sets = [
            { container: pagination, info: paginationInfo, prev: paginationPrev, next: paginationNext },
            { container: paginationBottom, info: paginationBottomInfo, prev: paginationBottomPrev, next: paginationBottomNext }
        ];
        const shouldShow = pageSize !== 0 && totalVisible > pageSize;
        sets.forEach(set => {
            if (!set?.container) return;
            if (!shouldShow) {
                set.container.classList.add('hidden');
                return;
            }
            set.container.classList.remove('hidden');
            if (set.info) set.info.textContent = `Page ${page} of ${totalPages}`;
            if (set.prev) set.prev.disabled = page <= 1;
            if (set.next) set.next.disabled = page >= totalPages;
        });
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
            updatePaginationUI(0, 1, 0);
            return;
        }

        const effectivePageSize = pageSize === 0 ? sorted.length : pageSize;
        const totalPages = Math.max(1, Math.ceil(sorted.length / effectivePageSize));
        if (page > totalPages) page = totalPages;
        if (page < 1) page = 1;
        const start = pageSize === 0 ? 0 : (page - 1) * effectivePageSize;
        const end = pageSize === 0 ? sorted.length : start + effectivePageSize;
        const pageItems = sorted.slice(start, end);

        pageItems.forEach(book => {
            try {
                resultsEl.appendChild(app.createBookCard(book, {
                    showWanted: false,
                    showAddToLibrary: false,
                    enableViewLink: false,
                    enableSearchLinks: true
                }));
            } catch (err) {
                console.error('Failed to render Calibre book', book, err);
            }
        });

        const base = normalizedQuery
            ? `${sorted.length} match(es).`
            : `${sorted.length} book(s).`;
        statusEl.textContent = lastSyncText
            ? `${base} ${lastSyncText}`
            : base;
        updatePaginationUI(sorted.length, totalPages, pageItems.length);
    }

    async function loadCalibreBooks(force = false) {
        if (loading && !force) return;
        loading = true;
        if (force) {
            loaded = false;
            allBooks = [];
            searchCache = new WeakMap();
            page = 1;
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
                    ? 'No books in Calibre mirror yet. Waiting for the next automatic sync.'
                    : 'Calibre not synced yet. Automatic sync will populate your library once the metadata path is set.';
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
            page = 1;
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

    window.bookwormCalibre = {
        ensureLoaded() {
            if (!loaded) {
                loadCalibreBooks();
            }
        },
        reload() {
            loaded = false;
            loadCalibreBooks();
        }
    };

    if (searchInput) {
        searchInput.addEventListener('input', (event) => {
            searchQuery = event.target.value || '';
            page = 1;
            renderBooks();
        });
    }
    if (searchClear && searchInput) {
        searchClear.addEventListener('click', () => {
            searchInput.value = '';
            searchQuery = '';
            page = 1;
            renderBooks();
            searchInput.focus();
        });
    }
    if (sortSelect) {
        sortSelect.value = sortKey;
        sortSelect.addEventListener('change', (event) => {
            sortKey = event.target.value || 'title-asc';
            page = 1;
            renderBooks();
        });
    }
    if (pageSizeSelect) {
        pageSizeSelect.value = String(pageSize);
        pageSizeSelect.addEventListener('change', (event) => {
            const value = parseInt(event.target.value, 10);
            pageSize = Number.isNaN(value) ? 30 : value;
            page = 1;
            renderBooks();
        });
    }
    [paginationPrev, paginationBottomPrev].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            if (page > 1) {
                page -= 1;
                renderBooks();
            }
        });
    });
    [paginationNext, paginationBottomNext].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            page += 1;
            renderBooks();
        });
    });

})();
