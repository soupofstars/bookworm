(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('calibre-status');
    const resultsEl = document.getElementById('calibre-results');
    const syncBtn = document.getElementById('btn-calibre-sync');
    if (!statusEl || !resultsEl) return;

    let loaded = false;
    let loading = false;
    let syncing = false;

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

    async function loadCalibreBooks() {
        if (loading) return;
        loading = true;
        statusEl.textContent = 'Loading Calibre library…';
        resultsEl.innerHTML = '';

        try {
            const res = await fetch('/calibre/books?take=0');
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

            let rendered = 0;
            books.forEach(book => {
                try {
                    resultsEl.appendChild(app.createBookCard(normalizeCalibreBook(book), {
                        showWanted: false,
                        showAddToLibrary: false,
                        enableViewLink: false
                    }));
                    rendered += 1;
                } catch (err) {
                    console.error('Failed to render Calibre book', book, err);
                }
            });
            statusEl.textContent = `Calibre library: showing ${rendered} of ${books.length} book(s). ${syncInfo}`;
            if (typeof app.setLibraryFromCalibre === 'function') {
                app.setLibraryFromCalibre(books, syncInfo);
            }
            loaded = true;
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
            await loadCalibreBooks();
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

    document.addEventListener('DOMContentLoaded', () => {
        if (syncBtn) {
            syncBtn.disabled = false;
        }
    });
})();
