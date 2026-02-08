(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-wanted-status');
    const disclaimerEl = document.getElementById('hardcover-wanted-disclaimer');
    const mismatchContainer = document.getElementById('hardcover-mismatch-container');
    const mismatchToggle = document.getElementById('hardcover-mismatch-toggle');
    const mismatchListEl = document.getElementById('hardcover-mismatch-list');
    const mismatchPage = document.getElementById('section-hardcover-mismatch');
    const mismatchPageStatus = document.getElementById('hardcover-mismatch-page-status');
    const mismatchPageList = document.getElementById('hardcover-mismatch-page-list');
    const mismatchPageBack = document.getElementById('hardcover-mismatch-back');
    const resultsEl = document.getElementById('hardcover-wanted-results');
    const searchInput = document.getElementById('hardcover-search-input');
    const searchClear = document.getElementById('hardcover-search-clear');
    const sortSelect = document.getElementById('hardcover-sort');
    const pageSizeSelect = document.getElementById('hardcover-page-size');
    const pagination = document.getElementById('hardcover-pagination');
    const paginationInfo = document.getElementById('hardcover-pagination-info');
    const paginationPrev = document.getElementById('hardcover-pagination-prev');
    const paginationNext = document.getElementById('hardcover-pagination-next');
    const paginationBottom = document.getElementById('hardcover-pagination-bottom');
    const paginationBottomInfo = document.getElementById('hardcover-pagination-bottom-info');
    const paginationBottomPrev = document.getElementById('hardcover-pagination-bottom-prev');
    const paginationBottomNext = document.getElementById('hardcover-pagination-bottom-next');
    let loaded = false;
    let loading = false;
    let cachedBooks = [];
    let cachedFromDb = [];
    let missingWanted = [];
    let missingCount = 0;
    let lastLoadPromise = null;
    let wantState = null;
    let searchQuery = '';
    let sortKey = 'title-asc';
    let page = 1;
    let pageSize = 30;
    const searchCache = new WeakMap();
    const isbnCache = new WeakMap();

    function coerceArray(value) {
        if (Array.isArray(value)) return value;
        if (value == null) return [];
        if (typeof value === 'string') {
            try {
                const parsed = JSON.parse(value);
                if (Array.isArray(parsed)) return parsed;
            } catch {
                // ignore malformed JSON
            }
        }
        return [];
    }

    function cleanIsbn(value) {
        return typeof value === 'string' ? value.trim() : '';
    }

    function normalizeIsbn(value) {
        return typeof value === 'string'
            ? value.replace(/[^0-9Xx]/g, '').toUpperCase()
            : '';
    }

    function parseHardcoverErrorText(text) {
        if (!text) return null;
        try {
            const json = JSON.parse(text);
            if (json && typeof json === 'object') {
                if (typeof json.error === 'string') return json.error;
                if (typeof json.title === 'string') return json.title;
                if (Array.isArray(json.errors) && json.errors.length) {
                    const first = json.errors[0];
                    if (first && typeof first.message === 'string') {
                        return first.message;
                    }
                }
            }
        } catch {
            // not JSON
        }
        return text;
    }

    async function fetchHardcoverWantState(forceReload = false) {
        if (wantState && !forceReload) {
            return wantState;
        }
        try {
            const res = await fetch('/hardcover/want-to-read/state');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            wantState = await res.json();
        } catch (err) {
            console.warn('Failed to load Hardcover want-to-read state', err);
            wantState = null;
        }
        return wantState;
    }

    function bookKey(book) {
        if (!book) return '';
        return book.slug || book.id || book.book_id || book.bookId || book.title || '';
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function collectIsbnCandidates(book) {
        if (!book) return [];
        if (isbnCache.has(book)) return isbnCache.get(book);
        const values = [];
        const push = (val) => {
            const n = normalizeIsbn(val);
            if (n) values.push(n);
        };
        push(book.isbn13 || book.isbn_13);
        push(book.isbn10 || book.isbn_10);
        if (book.default_physical_edition) {
            push(book.default_physical_edition.isbn_13);
            push(book.default_physical_edition.isbn_10);
        }
        if (book.default_ebook_edition) {
            push(book.default_ebook_edition.isbn_13);
            push(book.default_ebook_edition.isbn_10);
        }
        if (Array.isArray(book.isbn13)) book.isbn13.forEach(push);
        if (Array.isArray(book.isbn_13)) book.isbn_13.forEach(push);
        if (Array.isArray(book.isbn10)) book.isbn10.forEach(push);
        if (Array.isArray(book.isbn_10)) book.isbn_10.forEach(push);
        const distinct = Array.from(new Set(values));
        isbnCache.set(book, distinct);
        return distinct;
    }

    function extractHardcoverIds(book) {
        if (!book) return [];
        const ids = [
            book.hardcover_id,
            book.hardcoverId,
            book.book_id,
            book.bookId,
            book.id
        ].map(val => {
            if (val == null) return '';
            if (typeof val === 'number') return String(val);
            return String(val).trim();
        }).filter(Boolean);
        return Array.from(new Set(ids));
    }

    function extractNames(list) {
        return list.map(item => {
            if (typeof item === 'string') return item;
            if (item && typeof item === 'object') {
                if (typeof item.name === 'string') return item.name;
                if (typeof item.full_name === 'string') return item.full_name;
                if (typeof item.display_name === 'string') return item.display_name;
                if (item.user && typeof item.user.name === 'string') return item.user.name;
                if (item.user && typeof item.user.username === 'string') return item.user.username;
            }
            return null;
        }).filter(Boolean);
    }

    function normalizeHardcoverBook(book) {
        const normalized = Object.assign({}, book);
        const authorsArray = coerceArray(book.authors);
        const contributorsArray = coerceArray(book.cached_contributors);
        const contributionsArray = coerceArray(book.contributions);
        const contributionAuthors = contributionsArray
            .map(entry => entry && entry.author)
            .filter(Boolean);
        const authorSources = [
            ...extractNames(authorsArray),
            ...extractNames(contributorsArray),
            ...extractNames(contributionAuthors)
        ];

        if (authorSources.length) {
            const unique = Array.from(new Set(authorSources));
            normalized.authors = unique;
            normalized.author_names = unique;
        }

        normalized.cached_contributors = contributorsArray;
        normalized.contributions = contributionsArray;

        const isbn13 = cleanIsbn(
            book?.default_physical_edition?.isbn_13 ||
            book?.default_ebook_edition?.isbn_13 ||
            ''
        );
        const isbn10 = cleanIsbn(
            book?.default_physical_edition?.isbn_10 ||
            book?.default_ebook_edition?.isbn_10 ||
            ''
        );

        if (isbn13) {
            normalized.isbn13 = isbn13;
        }
        if (isbn10) {
            normalized.isbn10 = isbn10;
        }

        if (!normalized.image && book.image && book.image.url) {
            normalized.image = { url: book.image.url };
        }

        if (typeof normalized.edition_count !== 'number' && typeof normalized.editions_count === 'number') {
            normalized.edition_count = normalized.editions_count;
        }

        normalized.source = 'hardcover';
        if (!normalized.slug && book.slug) {
            normalized.slug = book.slug;
        }
        if (!normalized.id && book.id) {
            normalized.id = book.id;
        }

        return normalized;
    }

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
        if (Array.isArray(book.cached_contributors)) {
            book.cached_contributors.forEach(c => push(c?.name));
        }
        if (Array.isArray(book.contributions)) {
            book.contributions.forEach(c => push(c?.author?.name));
        }
        const isbn = cleanIsbn(book.isbn13 || book.isbn10 || '');
        if (isbn) push(isbn);

        const text = parts.join(' ');
        searchCache.set(book, text);
        return text;
    }

    function sharesHardcoverOrIsbn(left, right) {
        if (!left || !right) return false;
        const leftIds = extractHardcoverIds(left);
        const rightIds = extractHardcoverIds(right);
        if (leftIds.length && rightIds.some(id => leftIds.includes(id))) return true;

        const leftIsbns = collectIsbnCandidates(left);
        const rightIsbns = collectIsbnCandidates(right);
        return leftIsbns.length && rightIsbns.some(isbn => leftIsbns.includes(isbn));
    }

    function resolveCoverUrl(book) {
        if (!book) return '';
        const candidates = [
            book.coverUrl,
            book.cover_url,
            book.image && book.image.url,
            book.cover,
            book.image_url
        ].filter(Boolean);
        if (candidates.length) return candidates[0];
        const coverId = book.cover_i || book.coverId;
        if (coverId) {
            return `https://covers.openlibrary.org/b/id/${coverId}-M.jpg`;
        }
        return '';
    }

    function matchesQuery(book, normalizedQuery) {
        if (!normalizedQuery) return true;
        return buildSearchKey(book).includes(normalizedQuery);
    }

    function primaryAuthor(book) {
        if (!book) return '';
        const authors = Array.isArray(book.author_names) && book.author_names.length
            ? book.author_names
            : Array.isArray(book.authors) && book.authors.length
                ? book.authors
                : [];
        if (authors.length) return authors[0];
        if (Array.isArray(book.cached_contributors)) {
            const contrib = book.cached_contributors.find(c => c && typeof c.name === 'string');
            if (contrib) return contrib.name;
        }
        if (Array.isArray(book.contributions)) {
            const contrib = book.contributions.find(c => c?.author?.name);
            if (contrib && contrib.author.name) return contrib.author.name;
        }
        return '';
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

    function resolveHardcoverNumericId(book) {
        const ids = extractHardcoverIds(book);
        for (const id of ids) {
            const parsed = parseInt(id, 10);
            if (!Number.isNaN(parsed) && parsed > 0) {
                return parsed;
            }
        }
        return null;
    }

    async function removeHardcoverBook(book) {
        const numericId = resolveHardcoverNumericId(book);
        if (!numericId) {
            statusEl.textContent = 'Cannot remove: missing Hardcover id.';
            return;
        }

        const payload = { book_id: numericId, status_id: 6, wantToRead: false };
        statusEl.textContent = 'Removing from Hardcover…';

        try {
            const res = await fetch('/hardcover/want-to-read/status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const text = await res.text();
            if (!res.ok) {
                const detail = parseHardcoverErrorText(text);
                throw new Error(detail || `HTTP ${res.status}`);
            }
            cachedBooks = cachedBooks.filter(entry => bookKey(entry) !== bookKey(book));
            renderBooks();
            statusEl.textContent = 'Removed from Hardcover want-to-read.';
        } catch (err) {
            console.warn('Hardcover removeBook failed', err);
            statusEl.textContent = 'Unable to remove from Hardcover.';
        } finally {
            updateMismatchNotice();
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
        resultsEl.innerHTML = '';
        const total = cachedBooks.length;
        if (!total) {
            statusEl.textContent = loaded
                ? 'No “want to read” books on Hardcover yet.'
                : statusEl.textContent;
            updateMismatchNotice();
            return;
        }

        const rawQuery = searchQuery || '';
        const normalizedQuery = rawQuery.trim().toLowerCase();
        const visible = normalizedQuery
            ? cachedBooks.filter(book => matchesQuery(book, normalizedQuery))
            : cachedBooks;
        const sorted = sortBooks(visible);

        if (!sorted.length) {
            statusEl.textContent = rawQuery
                ? `No matches for "${rawQuery.trim()}".`
                : 'No “want to read” books on Hardcover yet.';
            updateMismatchNotice();
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
            resultsEl.appendChild(app.createBookCard(book, {
                showWanted: false,
                showAddToLibrary: false,
                useWantedLayout: false,
                showDeletePill: true,
                deleteLabel: 'Delete',
                onDelete: () => removeHardcoverBook(book)
            }));
        });

        const base = normalizedQuery
            ? `${sorted.length} match(es) on Hardcover.`
            : `${total} “want to read” book(s) on Hardcover.`;
        statusEl.textContent = base;
        updatePaginationUI(sorted.length, totalPages, pageItems.length);
        updateMismatchNotice();
    }

    function updateMismatchNotice() {
        if (!disclaimerEl) return;
        // Always show the note to set expectations, even if counts aren't ready yet.
        disclaimerEl.classList.remove('hidden');

        if (!loaded) {
            disclaimerEl.textContent = 'Checking sync with Hardcover…';
            return;
        }

        if (!app || !app.state || !app.state.wantedLoaded) {
            disclaimerEl.textContent = 'Loading Wanted shelf to compare against Hardcover…';
            return;
        }

        const wanted = Array.isArray(app.state.wanted) ? app.state.wanted : [];
        if (!wanted.length) {
            disclaimerEl.textContent = 'No books in Wanted to compare with Hardcover.';
            return;
        }

        missingWanted = wanted.filter(local => !cachedBooks.some(remote => sharesHardcoverOrIsbn(local, remote)));
        missingCount = missingWanted.length;
        renderMismatchList();

        if (missingCount > 0) {
            disclaimerEl.innerHTML = `Some Wanted books may not sync to Hardcover due to ISBN mismatches. Not synced: ${missingCount} <button id="hardcover-mismatch-link" class="btn btn-ghost btn-inline" type="button">View list</button>`;
        } else {
            disclaimerEl.textContent = 'All Wanted books with matching ISBN/IDs are on Hardcover.';
        }
        wireMismatchLink();
        renderMismatchPageList();
    }

    function renderMismatchList() {
        if (!mismatchContainer || !mismatchToggle || !mismatchListEl) return;
        const count = missingWanted.length;
        if (count === 0) {
            mismatchContainer.classList.add('hidden');
            mismatchListEl.classList.add('hidden');
            mismatchToggle.textContent = 'Show not-synced list';
            mismatchListEl.innerHTML = '';
            return;
        }

        mismatchContainer.classList.remove('hidden');
        mismatchToggle.textContent = `Show not-synced list (${count})`;

        const items = missingWanted.map(book => {
            const title = book?.title || book?.slug || 'Untitled';
            const authors = Array.isArray(book?.author_names) ? book.author_names.join(', ') : '';
            const isbns = collectIsbnCandidates(book).join(', ');
            const safeTitle = window.bookwormApp && window.bookwormApp.escapeHtml
                ? window.bookwormApp.escapeHtml(title)
                : title;
            return `<li><strong>${safeTitle}</strong>${authors ? ` — ${authors}` : ''}${isbns ? ` <span class="mismatch-isbn">(ISBN: ${isbns})</span>` : ''}</li>`;
        }).join('');

        mismatchListEl.innerHTML = `<ul class="mismatch-list">${items}</ul>`;
    }

    function wireMismatchLink() {
        if (!disclaimerEl) return;
        if (wireMismatchLink._attached) return;
        disclaimerEl.addEventListener('click', (event) => {
            if (event.target && event.target.id === 'hardcover-mismatch-link') {
                showMismatchPage();
            }
        });
        wireMismatchLink._attached = true;
    }

    function handleMismatchDelete(book) {
        if (!app || typeof app.removeFromWanted !== 'function') {
            return;
        }
        try {
            app.removeFromWanted(book);
        } catch (err) {
            console.warn('Failed to remove wanted book while fixing mismatch', err);
        }
        missingWanted = missingWanted.filter(item => bookKey(item) !== bookKey(book));
        missingCount = missingWanted.length;
        renderMismatchPageList();
        renderMismatchList();
        updateMismatchNotice();
    }

    function renderMismatchPageList() {
        if (!mismatchPageList || !mismatchPageStatus) return;
        mismatchPageList.innerHTML = '';
        if (missingCount === 0) {
            mismatchPageStatus.textContent = 'All Wanted books with matching ISBN/IDs are on Hardcover.';
            return;
        }
        mismatchPageStatus.textContent = `Showing ${missingCount} not-synced book(s).`;
        missingWanted.forEach(book => {
            let card = null;
            if (window.bookwormApp && typeof window.bookwormApp.createBookCard === 'function') {
                card = window.bookwormApp.createBookCard(book, {
                    showWanted: false,
                    showAddToLibrary: false,
                    useWantedLayout: true,
                    showRemoveWanted: false,
                    showIsbnInline: true,
                    enableViewLink: true,
                    showHardcoverStatus: false,
                    showDeletePill: true,
                    deleteLabel: 'Delete',
                    onDelete: () => handleMismatchDelete(book)
                });
            }
            if (!card) {
                const fallback = document.createElement('div');
                fallback.className = 'card mismatch-card';
                fallback.textContent = book?.title || book?.slug || 'Untitled';
                mismatchPageList.appendChild(fallback);
                return;
            }
            card.classList.add('mismatch-book-card');
            const badge = document.createElement('div');
            badge.className = 'mismatch-badge';
            badge.textContent = 'Not synced';
            card.appendChild(badge);

            mismatchPageList.appendChild(card);
        });
    }

    function showMismatchList(forceShow) {
        if (!mismatchContainer || !mismatchToggle || !mismatchListEl) return;
        renderMismatchList();
        mismatchContainer.classList.remove('hidden');
        mismatchListEl.classList.remove('hidden');
        mismatchToggle.textContent = `Hide not-synced list (${missingCount})`;
        if (forceShow) return;
    }

    function showMismatchPage() {
        renderMismatchPageList();
        if (mismatchPage) {
            if (window.bookwormApp && typeof window.bookwormApp.showSection === 'function') {
                window.bookwormApp.showSection('hardcover-mismatch');
            } else {
                mismatchPage.classList.remove('hidden');
            }
        } else {
            showMismatchList(true);
        }
    }

    function loadHardcoverWanted(forceReload = false) {
        if (loading && lastLoadPromise) {
            return lastLoadPromise;
        }
        if (loaded && !forceReload) {
            return Promise.resolve(cachedBooks);
        }

        loading = true;
        statusEl.textContent = 'Loading from Hardcover…';
        resultsEl.innerHTML = '';

        lastLoadPromise = (async () => {
            let errorMessage = '';
            try {
                const state = await fetchHardcoverWantState(forceReload);
                if (state && state.configured === false) {
                    if (!cachedBooks.length && cachedFromDb.length) {
                        cachedBooks = cachedFromDb.slice();
                    }

                    const hasCache = cachedBooks.length > 0;
                    const message = hasCache
                        ? `Using cached Hardcover want-to-read (${cachedBooks.length}). Set Hardcover__ApiKey to refresh.`
                        : 'Hardcover API key not configured. Set Hardcover__ApiKey to enable Hardcover want-to-read sync.';
                    statusEl.textContent = message;
                    loaded = true;
                    if (hasCache) {
                        page = 1;
                        renderBooks();
                        statusEl.textContent = message;
                    }
                    return cachedBooks;
                }

                const res = await fetch('/hardcover/want-to-read');
                if (!res.ok) {
                    let detail = '';
                    try {
                        detail = await res.text();
                    } catch {
                        // ignore
                    }
                    const parsedDetail = parseHardcoverErrorText(detail);
                    errorMessage = parsedDetail || detail || `Error loading from Hardcover (HTTP ${res.status}).`;
                    if (cachedBooks.length) {
                        errorMessage += ` Showing cached results (${cachedBooks.length}).`;
                    }
                    statusEl.textContent = errorMessage;
                    loaded = cachedBooks.length > 0;
                    if (loaded) {
                        renderBooks();
                        statusEl.textContent = errorMessage;
                    }
                    return cachedBooks;
                }

                const data = await res.json();
                const meData = data?.data?.me;
                const firstUser = Array.isArray(meData)
                    ? (meData.length ? meData[0] : null)
                    : (meData && typeof meData === 'object' ? meData : null);
                const userBooks = firstUser?.user_books ?? firstUser?.user_book ?? [];

                cachedBooks = [];
                if (userBooks.length) {
                    userBooks.forEach(entry => {
                        const book = entry.book;
                        if (!book) return;
                        const normalized = normalizeHardcoverBook(book);
                        cachedBooks.push(normalized);
                    });
                }

                loaded = true;
                page = 1;
                renderBooks();

                if (typeof app.handleHardcoverWantedBooks === 'function') {
                    app.handleHardcoverWantedBooks(cachedBooks);
                }
                try {
                    await fetch('/hardcover/want-to-read/cache', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ books: cachedBooks })
                    });
                } catch (err) {
                    console.warn('Failed to cache Hardcover want-to-read', err);
                }
                return cachedBooks;
            } catch (err) {
                console.error('Failed to load Hardcover want-to-read', err);
                const fallback = errorMessage || err?.message || 'Error talking to Bookworm API.';
                statusEl.textContent = fallback;
                if (!cachedBooks.length && cachedFromDb.length) {
                    cachedBooks = cachedFromDb.slice();
                    loaded = true;
                    page = 1;
                    renderBooks();
                    statusEl.textContent = fallback;
                }
                return cachedBooks;
            } finally {
                loading = false;
                updateMismatchNotice();
            }
        })();

        return lastLoadPromise;
    }

    async function loadCachedBooks() {
        try {
            const res = await fetch('/hardcover/want-to-read/cache');
            if (!res.ok) return;
            const data = await res.json();
            const items = Array.isArray(data?.books) ? data.books : [];
            if (!items.length) return;
            cachedFromDb = items.map(normalizeHardcoverBook);
            if (!cachedBooks.length) {
                cachedBooks = cachedFromDb.slice();
                loaded = true;
                page = 1;
                renderBooks();
                updateMismatchNotice();
            }
        } catch (err) {
            console.warn('Failed to load cached Hardcover want-to-read', err);
        }
    }

    window.bookwormHardcover = {
        ensureLoaded(forceReload) {
            return loadHardcoverWanted(!!forceReload);
        },
        reload() {
            return loadHardcoverWanted(true);
        },
        getCachedBooks() {
            return cachedBooks.slice();
        },
        fetchWantedBooks(options) {
            const opts = Object.assign({ force: false }, options || {});
            return loadHardcoverWanted(!!opts.force);
        },
        normalizeBook: normalizeHardcoverBook
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
    if (mismatchToggle && mismatchListEl) {
        mismatchToggle.addEventListener('click', () => {
            const isHidden = mismatchListEl.classList.contains('hidden');
            mismatchListEl.classList.toggle('hidden', !isHidden);
            mismatchToggle.textContent = isHidden
                ? `Hide not-synced list (${missingCount})`
                : `Show not-synced list (${missingCount})`;
            if (isHidden && mismatchContainer) {
                mismatchContainer.classList.remove('hidden');
            }
            if (isHidden) {
                renderMismatchList();
            }
        });
    }
    if (mismatchPageBack) {
        mismatchPageBack.addEventListener('click', () => {
            if (window.bookwormApp && typeof window.bookwormApp.showSection === 'function') {
                window.bookwormApp.showSection('hardcover-wanted');
            } else if (mismatchPage) {
                mismatchPage.classList.add('hidden');
            }
        });
    }
    loadCachedBooks().finally(() => {
        loadHardcoverWanted();
    });
})();
