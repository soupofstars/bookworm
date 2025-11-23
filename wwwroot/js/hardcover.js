(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-wanted-status');
    const resultsEl = document.getElementById('hardcover-wanted-results');
    let loaded = false;
    let loading = false;
    let cachedBooks = [];
    let lastLoadPromise = null;

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
                if (!userBooks.length) {
                    statusEl.textContent = 'No “want to read” books on Hardcover yet.';
                    loaded = true;
                    cachedBooks = [];
                    if (typeof app.handleHardcoverWantedBooks === 'function') {
                        app.handleHardcoverWantedBooks(cachedBooks);
                    }
                    return cachedBooks;
                }

                statusEl.textContent = `You have ${userBooks.length} “want to read” book(s) on Hardcover.`;
                resultsEl.innerHTML = '';

                userBooks.forEach(entry => {
                    const book = entry.book;
                    if (!book) return;
                    const normalized = normalizeHardcoverBook(book);
                    cachedBooks.push(normalized);
                    resultsEl.appendChild(app.createBookCard(normalized));
                });

                loaded = true;
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
})();
