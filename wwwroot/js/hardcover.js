(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-wanted-status');
    const resultsEl = document.getElementById('hardcover-wanted-results');
    const searchInput = document.getElementById('hardcover-search-input');
    const searchClear = document.getElementById('hardcover-search-clear');
    const sortSelect = document.getElementById('hardcover-sort');
    let loaded = false;
    let loading = false;
    let cachedBooks = [];
    let lastLoadPromise = null;
    let searchQuery = '';
    let sortKey = 'title-asc';
    const searchCache = new WeakMap();

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

    function renderBooks() {
        resultsEl.innerHTML = '';
        const total = cachedBooks.length;
        if (!total) {
            statusEl.textContent = loaded
                ? 'No “want to read” books on Hardcover yet.'
                : statusEl.textContent;
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
            return;
        }

        sorted.forEach(book => {
            resultsEl.appendChild(app.createBookCard(book));
        });

        statusEl.textContent = normalizedQuery
            ? `Showing ${sorted.length} of ${total} “want to read” book(s) on Hardcover.`
            : `You have ${total} “want to read” book(s) on Hardcover.`;
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
            try {
                const res = await fetch('/hardcover/want-to-read');
                if (!res.ok) {
                    let detail = '';
                    try {
                        detail = await res.text();
                    } catch {
                        // ignore
                    }
                    const suffix = detail ? ` (${detail})` : '';
                    statusEl.textContent = 'Error loading from Hardcover: ' + res.status + suffix;
                    throw new Error('Hardcover load failed');
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
                renderBooks();

                if (typeof app.handleHardcoverWantedBooks === 'function') {
                    app.handleHardcoverWantedBooks(cachedBooks);
                }
                return cachedBooks;
            } catch (err) {
                console.error(err);
                statusEl.textContent = 'Error talking to Bookworm API.';
                throw err;
            } finally {
                loading = false;
            }
        })();

        return lastLoadPromise;
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
})();
