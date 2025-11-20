(function () {
    const state = {
        library: [],
        wanted: [],
        wantedLoading: false,
        wantedLoaded: false,
        hardcoverSyncEnabled: false
    };
    const HARDCOVER_SYNC_KEY = 'bookworm:hardcover-sync';

    const sections = {
        'discover-books': document.getElementById('section-discover-books'),
        'discover-author': document.getElementById('section-discover-author'),
        'discover-isbn': document.getElementById('section-discover-isbn'),
        library: document.getElementById('section-library'),
        wanted: document.getElementById('section-wanted'),
        'hardcover-wanted': document.getElementById('section-hardcover-wanted'),
        calibre: document.getElementById('section-calibre'),
        settings: document.getElementById('section-settings')
    };

    const navLinks = document.querySelectorAll('.nav-link[data-section]');
    const navMap = {};
    navLinks.forEach(link => {
        navMap[link.dataset.section] = link;
    });

    const refs = {
        topbarTitle: document.getElementById('topbar-title'),
        libraryStatus: document.getElementById('library-status'),
        libraryResults: document.getElementById('library-results'),
        wantedStatus: document.getElementById('wanted-status'),
        wantedResults: document.getElementById('wanted-results'),
        hardcoverToggle: document.getElementById('toggle-hardcover-sync'),
        hardcoverToggleStatus: document.getElementById('settings-hardcover-status'),
        calibrePathInput: document.getElementById('input-calibre-path'),
        calibrePathSave: document.getElementById('btn-save-calibre-path'),
        calibrePathStatus: document.getElementById('status-calibre-path'),
        calibreSettingsCard: document.getElementById('settings-calibre-card')
    };

    function isHardcoverBook(book) {
        return Boolean(book && book.source === 'hardcover' && book.id);
    }

    function updateHardcoverToggleUI() {
        if (refs.hardcoverToggle) {
            refs.hardcoverToggle.checked = !!state.hardcoverSyncEnabled;
        }
        if (refs.hardcoverToggleStatus) {
            refs.hardcoverToggleStatus.textContent = state.hardcoverSyncEnabled
                ? 'Syncing with Hardcover want-to-read list.'
                : 'Not syncing with Hardcover.';
        }
    }

    function persistHardcoverSyncSetting(enabled) {
        try {
            if (window.localStorage) {
                window.localStorage.setItem(HARDCOVER_SYNC_KEY, enabled ? '1' : '0');
            }
        } catch {
            // ignore storage failures
        }
    }

    function loadHardcoverSyncSetting() {
        try {
            if (window.localStorage) {
                const stored = window.localStorage.getItem(HARDCOVER_SYNC_KEY);
                state.hardcoverSyncEnabled = stored === '1';
            }
        } catch {
            state.hardcoverSyncEnabled = false;
        }
        updateHardcoverToggleUI();
    }

    function setHardcoverSyncEnabled(enabled, options) {
        const next = !!enabled;
        const prev = state.hardcoverSyncEnabled;
        state.hardcoverSyncEnabled = next;
        persistHardcoverSyncSetting(next);
        updateHardcoverToggleUI();
        if (next && !prev && (!options || options.triggerSync !== false)) {
            mirrorHardcoverWanted({ force: true, retries: 8 });
        }
    }

    async function refreshCalibrePathStatus() {
        if (!refs.calibrePathStatus) return;
        refs.calibrePathStatus.textContent = 'Checking Calibre path…';
        if (!refs.calibrePathInput) return;
        try {
            const res = await fetch('/settings/calibre');
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || 'Failed to load Calibre settings.');
            }
            const data = await res.json();
            if (refs.calibrePathInput) {
                refs.calibrePathInput.value = data?.path || '';
            }
            const exists = !!data?.exists;
            if (refs.calibrePathStatus) {
                refs.calibrePathStatus.textContent = exists
                    ? 'Calibre metadata detected.'
                    : 'No Calibre metadata configured.';
            }
            if (refs.calibreSettingsCard) {
                refs.calibreSettingsCard.classList.toggle('hidden', exists);
            }
        } catch (err) {
            console.error(err);
            if (refs.calibrePathStatus) {
                refs.calibrePathStatus.textContent = 'Unable to load Calibre settings.';
            }
        }
    }

    async function saveCalibrePathSetting(event) {
        if (event) {
            event.preventDefault();
        }
        if (!refs.calibrePathInput || !refs.calibrePathStatus) return;
        const rawPath = refs.calibrePathInput.value.trim();
        refs.calibrePathStatus.textContent = 'Saving Calibre path…';
        try {
            const res = await fetch('/settings/calibre', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: rawPath })
            });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || 'Unable to save Calibre path.');
            }
            await refreshCalibrePathStatus();
            window.bookwormCalibre && window.bookwormCalibre.reload();
        } catch (err) {
            console.error('Failed to save Calibre path', err);
            refs.calibrePathStatus.textContent = err?.message || 'Unable to save Calibre path.';
        }
    }

    function bookKey(book) {
        return book.slug || book.id || book.title;
    }

    function buildIsbnText(book) {
        return book.isbn13 || book.isbn10 || 'N/A';
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function renderPills(pills) {
        return pills.map(pill => `
            <div class="book-pill">
                <div class="book-pill-label">${pill.label}</div>
                <div class="book-pill-value">${pill.value}</div>
            </div>
        `).join('');
    }

    function createExpandableBlock(text, className, maxChars) {
        const safeText = text || (className === 'book-title' ? 'Untitled' : 'Unknown author');
        if (safeText.length <= maxChars) {
            return `<div class="${className}">${safeText}</div>`;
        }
        const shortText = `${safeText.slice(0, maxChars)}…`;
        const id = `expand-${Math.random().toString(36).slice(2, 9)}`;
        return `
            <div class="${className}">
                <span class="text-collapsed" data-target="${id}">${shortText}</span>
                <span class="text-expanded hidden" id="${id}">${safeText}</span>
                <button class="btn-text-toggle" data-target="${id}">More</button>
            </div>
        `;
    }

    function formatSourceLabel(book) {
        const raw = (book && book.source) ? String(book.source).toLowerCase() : '';
        if (!raw) return 'Local';
        if (raw.includes('hardcover')) return 'Hardcover.app';
        if (raw.includes('openlibrary')) return 'Open Library';
        return raw.charAt(0).toUpperCase() + raw.slice(1);
    }

    function createBookCard(book, options) {
        const opts = Object.assign({ showWanted: false, showAddToLibrary: false, useWantedLayout: false, enableViewLink: true }, options || {});
        const isbnText = book.isbn13 || book.isbn10 || '';
        const cardSpan = isbnText.length > 20 ? 2 : 1;
        const isCalibre = book.source === 'calibre';

        const div = document.createElement('div');
        div.className = 'book-card';
        div.style.setProperty('--book-card-span', cardSpan);

        const title = book.title || 'Untitled';
        const authorNames = (() => {
            if (Array.isArray(book.author_names) && book.author_names.length) return book.author_names;
            if (Array.isArray(book.authors) && book.authors.length) return book.authors;
            if (book.author) return [book.author];
            if (Array.isArray(book.cached_contributors) && book.cached_contributors.length) {
                return [book.cached_contributors[0].name].filter(Boolean);
            }
            return [];
        })();

        let coverUrl =
            (book.image && book.image.url) ||
            book.coverUrl ||
            (book.cover_i ? `https://covers.openlibrary.org/b/id/${book.cover_i}-L.jpg` : null);
        if (!coverUrl && Array.isArray(book.cached_contributors) && book.cached_contributors.length) {
            const contributorImage = book.cached_contributors[0].image;
            if (contributorImage && contributorImage.url) {
                coverUrl = contributorImage.url;
            }
        }

        const ratingRaw = book.rating ?? book.ratings_average ?? book.average_rating;
        const rating = typeof ratingRaw === 'number'
            ? Math.round(ratingRaw * 10) / 10
            : (ratingRaw || 'N/A');
        const editions = book.editions_count ?? book.edition_count ?? book.users_count ?? book.ratings_count ?? 0;

        let detailsUrl = null;
        if (book.source === 'openlibrary') {
            const prefer = value => Array.isArray(value) ? value[0] : value;
            let editionKey = prefer(book.cover_edition_key) || prefer(book.edition_key);
            if (editionKey) {
                detailsUrl = `https://openlibrary.org/books/${editionKey}`;
            } else if (book.slug) {
                detailsUrl = `https://openlibrary.org${book.slug}`;
            }
        } else if (book.slug || book.id) {
            detailsUrl = `https://hardcover.app/books/${book.slug || book.id}`;
        }
        const viewLink = opts.enableViewLink && detailsUrl
            ? `<a href="${detailsUrl}" target="_blank" class="btn btn-view">View more</a>`
            : '';

        let actionMarkup = '';
        if (opts.showWanted) {
            actionMarkup += `<button class="btn btn-wanted btn-wanted-action">Wanted</button>`;
        }
        if (opts.showAddToLibrary) {
            actionMarkup += `<button class="btn btn-primary btn-addlib-action">Add to library</button>`;
        }

        const leftPills = (() => {
            if (isCalibre) {
                const formatsValue = Array.isArray(book.formats) && book.formats.length
                    ? escapeHtml(book.formats.join(', '))
                    : 'Unknown';
                const sizeText = typeof book.size_mb === 'number'
                    ? `${book.size_mb.toFixed(1)} MB`
                    : 'Unknown';
                return [
                    { label: 'Formats:', value: formatsValue },
                    { label: 'Size:', value: sizeText }
                ];
            }
            return [
                { label: 'Editions:', value: editions },
                { label: 'Rating:', value: rating }
            ];
        })();

        const isbnDisplay = buildIsbnText(book);
        const isbnValue = isbnDisplay && isbnDisplay !== 'N/A'
            ? `<button class="isbn-link" data-isbn="${isbnDisplay.replace(/"/g, '&quot;')}">${isbnDisplay}</button>`
            : isbnDisplay;

        const rightPills = (() => {
            if (opts.useWantedLayout) {
                return [{ label: 'Source:', value: `<span class="book-source">${formatSourceLabel(book)}</span>` }];
            }
            if (isCalibre) {
                const publisher = book.publisher ? escapeHtml(book.publisher) : 'Unknown';
                const series = book.series ? escapeHtml(book.series) : 'Standalone';
                return [
                    { label: 'Publisher:', value: publisher },
                    { label: 'Series:', value: series }
                ];
            }
            return [{ label: 'ISBN:', value: isbnValue }];
        })();

        const isbnInlineBlock = opts.useWantedLayout && isbnValue
            ? `
                <div class="book-info-group">
                    <div class="book-info-label">ISBN</div>
                    <div class="book-isbn-inline">${isbnValue}</div>
                </div>
            `
            : '';

        const actionsHtml = [actionMarkup, viewLink].filter(Boolean).length
            ? `<div class="book-actions book-pill">${[actionMarkup, viewLink].filter(Boolean).join('')}</div>`
            : '';

        const coverElement = coverUrl
            ? `<img class="book-cover" loading="lazy" src="${coverUrl}" alt="Cover">`
            : `<div class="book-cover book-cover--placeholder">No Image Available</div>`;

        const titleBlock = `
            <div class="book-info-group">
                <div class="book-info-label">Title</div>
                ${createExpandableBlock(title, 'book-title', 60)}
            </div>
        `;

        const authorsMarkup = isCalibre
            ? renderAuthorPlain(authorNames)
            : renderAuthorLinks(authorNames);
        const calibreIsbnLine = (isCalibre && isbnDisplay && isbnDisplay !== 'N/A')
            ? `
                <div class="book-info-label book-isbn-label">ISBN</div>
                <div class="book-isbn-inline isbn-inline">${escapeHtml(isbnDisplay)}</div>
              `
            : '';

        const authorsBlock = `
            <div class="book-info-group">
                <div class="book-info-label">Authors</div>
                ${authorsMarkup}
                ${calibreIsbnLine}
            </div>
            ${isbnInlineBlock}
        `;

div.innerHTML = `
    <div class="book-media">
        ${coverElement}
    </div>

    <div class="book-body">
        ${titleBlock}
        ${authorsBlock}
    </div>

    <div class="book-meta-row">
        <div class="book-column">
            ${renderPills(leftPills)}
        </div>
        <div class="book-column">
            ${renderPills(rightPills)}
            ${actionsHtml}
        </div>
    </div>
`;

        const coverEl = div.querySelector('.book-cover');
        if (coverEl && coverUrl) {
            coverEl.addEventListener('click', () => openLightbox({
                coverUrl,
                title,
                slug: book.slug,
                description: book.description
            }));
        }

        const wantedBtn = div.querySelector('.btn-wanted-action');
        if (wantedBtn) {
            wantedBtn.addEventListener('click', () => addToWanted(book));
        }

        const addLibBtn = div.querySelector('.btn-addlib-action');
        if (addLibBtn) {
            addLibBtn.addEventListener('click', () => addToLibrary(book));
        }

        div.querySelectorAll('.btn-text-toggle.author-more-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const targetId = btn.dataset.target;
                const extra = div.querySelector(`#${targetId}`);
                if (!extra) return;
                const isHidden = extra.classList.contains('hidden');
                if (isHidden) {
                    extra.classList.remove('hidden');
                    btn.textContent = 'Less';
                } else {
                    extra.classList.add('hidden');
                    btn.textContent = 'More';
                }
            });
        });

        div.querySelectorAll('.btn-text-toggle').forEach(button => {
            button.addEventListener('click', () => {
                const targetId = button.dataset.target;
                const expanded = div.querySelector(`#${targetId}`);
                const collapsed = div.querySelector(`.text-collapsed[data-target="${targetId}"]`);
                if (!expanded || !collapsed) return;
                const isHidden = expanded.classList.contains('hidden');
                if (isHidden) {
                    expanded.classList.remove('hidden');
                    collapsed.classList.add('hidden');
                    button.textContent = 'Less';
                } else {
                    expanded.classList.add('hidden');
                    collapsed.classList.remove('hidden');
                    button.textContent = 'More';
                }
            });
        });

        div.querySelectorAll('.author-link').forEach(link => {
            link.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const name = link.dataset.author;
                if (!name) return;
                if (window.bookwormApp && window.bookwormApp.showSection) {
                    window.bookwormApp.showSection('discover-author');
                }
                if (window.bookwormAuthorSearch && window.bookwormAuthorSearch.search) {
                    window.bookwormAuthorSearch.search(name);
                }
            });
        });

        const isbnLink = div.querySelector('.isbn-link');
        if (isbnLink) {
            isbnLink.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const isbn = isbnLink.dataset.isbn;
                if (!isbn) return;
                if (window.bookwormApp && window.bookwormApp.showSection) {
                    window.bookwormApp.showSection('discover-isbn');
                }
                if (window.bookwormIsbnSearch && window.bookwormIsbnSearch.search) {
                    window.bookwormIsbnSearch.search(isbn);
                }
            });
        }

        return div;
    }

    const lightboxEl = document.getElementById('lightbox');
    const lightboxImage = document.getElementById('lightbox-image');
    const lightboxCaption = document.getElementById('lightbox-caption');
    const lightboxDescription = document.getElementById('lightbox-description');
    const lightboxClose = document.querySelector('.lightbox-close');

    async function openLightbox(meta) {
        if (!lightboxEl || !lightboxImage || !lightboxCaption) return;
        lightboxImage.src = meta.coverUrl;
        lightboxCaption.textContent = meta.title || '';
        if (lightboxDescription) {
            lightboxDescription.classList.add('hidden');
            lightboxDescription.textContent = '';
            const descriptionText = await resolveDescription(meta);
            if (descriptionText) {
                lightboxDescription.textContent = descriptionText;
                lightboxDescription.classList.remove('hidden');
            }
        }
        lightboxEl.classList.remove('hidden');
    }

    function closeLightbox() {
        if (!lightboxEl) return;
        lightboxEl.classList.add('hidden');
    }

    if (lightboxEl) {
        lightboxEl.addEventListener('click', (e) => {
            if (e.target === lightboxEl) closeLightbox();
        });
    }
    if (lightboxClose) {
        lightboxClose.addEventListener('click', closeLightbox);
    }

    async function resolveDescription(meta) {
        if (meta.description) return normalizeDescription(meta.description);
        if (!meta.slug || !meta.slug.startsWith('/works/')) return '';
        try {
            const res = await fetch(`https://openlibrary.org${meta.slug}.json`);
            if (!res.ok) return '';
            const data = await res.json();
            return normalizeDescription(data.description);
        } catch {
            return '';
        }
    }

    function normalizeDescription(desc) {
        if (!desc) return '';
        if (typeof desc === 'string') return desc.trim();
        if (typeof desc === 'object' && desc.value) return String(desc.value).trim();
        return '';
    }

    async function saveWantedRemote(key, book) {
        try {
            const res = await fetch('/wanted', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ key, book })
            });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || 'Failed to save wanted book.');
            }
        } catch (err) {
            throw err;
        }
    }

    async function deleteWantedRemote(key) {
        try {
            const res = await fetch(`/wanted/${encodeURIComponent(key)}`, { method: 'DELETE' });
            if (!res.ok && res.status !== 404) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || 'Failed to delete wanted book.');
            }
        } catch (err) {
            throw err;
        }
    }

    async function updateHardcoverWantState(book, shouldWant) {
        if (!state.hardcoverSyncEnabled || !isHardcoverBook(book)) return;
        try {
            const res = await fetch('/hardcover/want-to-read/status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ bookId: book.id, wantToRead: !!shouldWant })
            });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || 'Failed to update Hardcover want-to-read.');
            }
        } catch (err) {
            console.error('Hardcover sync failed', err);
            if (refs.hardcoverToggleStatus) {
                refs.hardcoverToggleStatus.textContent = 'Error syncing with Hardcover.';
            }
        }
    }

    async function mergeWantedBooks(books, options) {
        if (!Array.isArray(books) || !books.length) return;
        const opts = Object.assign({ skipHardcoverRemote: false }, options || {});
        const pending = [];
        let added = false;
        books.forEach(book => {
            const key = bookKey(book);
            const already = state.wanted.some(b => bookKey(b) === key);
            if (already) return;
            state.wanted.push(book);
            added = true;
            pending.push(saveWantedRemote(key, book).catch(err => {
                console.error('Failed to persist wanted book', err);
            }));
            if (!opts.skipHardcoverRemote && state.hardcoverSyncEnabled && isHardcoverBook(book)) {
                pending.push(updateHardcoverWantState(book, true));
            }
        });
        if (added) {
            renderWanted();
            refs.wantedStatus.textContent = `You have ${state.wanted.length} wanted book(s).`;
        }
        if (pending.length) {
            await Promise.allSettled(pending);
        }
    }

    function handleHardcoverWantedBooks(books) {
        if (!state.hardcoverSyncEnabled) return;
        mergeWantedBooks(books, { skipHardcoverRemote: true }).then(() => {
            if (refs.hardcoverToggleStatus) {
                refs.hardcoverToggleStatus.textContent = 'Synced with Hardcover.';
            }
        }).catch(err => {
            console.error('Hardcover merge failed', err);
            if (refs.wantedStatus) {
                refs.wantedStatus.textContent = 'Unable to mirror Hardcover list.';
            }
        });
    }

    function mirrorHardcoverWanted(options) {
        if (!state.hardcoverSyncEnabled) return;
        const opts = Object.assign({ force: false, retries: 0 }, options || {});
        const fetcher = window.bookwormHardcover && window.bookwormHardcover.fetchWantedBooks;
        if (!fetcher) {
            if (opts.retries > 0) {
                setTimeout(() => mirrorHardcoverWanted({
                    force: opts.force,
                    retries: opts.retries - 1
                }), 600);
            }
            return;
        }
        fetcher({ force: opts.force }).then(books => {
            handleHardcoverWantedBooks(books);
        }).catch(err => {
            console.error('Failed to sync Hardcover wanted list', err);
            if (opts.retries > 0) {
                setTimeout(() => mirrorHardcoverWanted({
                    force: opts.force,
                    retries: opts.retries - 1
                }), 900);
            } else if (refs.wantedStatus) {
                refs.wantedStatus.textContent = 'Unable to sync Hardcover wanted list.';
            }
        });
    }

    async function loadWantedFromServer() {
        state.wantedLoading = true;
        renderWanted();
        try {
            const res = await fetch('/wanted');
            if (!res.ok) {
                throw new Error(`Fetch failed (${res.status})`);
            }
            const data = await res.json();
            const items = Array.isArray(data?.items) ? data.items : [];
            state.wanted = items
                .map(item => item?.book)
                .filter(Boolean);
            state.wantedLoaded = true;
            state.wantedLoading = false;
            renderWanted();
        } catch (err) {
            console.error('Failed to load wanted books', err);
            state.wantedLoading = false;
            refs.wantedStatus.textContent = 'Unable to load wanted books.';
        }
    }

    function addToWanted(book) {
        const key = bookKey(book);
        if (!state.wanted.some(b => bookKey(b) === key)) {
            state.wanted.push(book);
            renderWanted();
            refs.wantedStatus.textContent = 'Book added to Wanted.';
            saveWantedRemote(key, book).catch(err => {
                console.error(err);
                refs.wantedStatus.textContent = 'Unable to save wanted book.';
                const idx = state.wanted.findIndex(b => bookKey(b) === key);
                if (idx !== -1) {
                    state.wanted.splice(idx, 1);
                    renderWanted();
                }
            });
            if (state.hardcoverSyncEnabled && isHardcoverBook(book)) {
                updateHardcoverWantState(book, true);
            }
        } else {
            refs.wantedStatus.textContent = 'Already in Wanted.';
        }
    }

    function addToLibrary(book) {
        const key = bookKey(book);
        if (!state.library.some(b => bookKey(b) === key)) {
            state.library.push(book);
            renderLibrary();
            refs.libraryStatus.textContent = 'Book added to your bookshelf.';
        } else {
            refs.libraryStatus.textContent = 'Already on your bookshelf.';
        }

        const idx = state.wanted.findIndex(b => bookKey(b) === key);
        if (idx !== -1) {
            const removedBook = state.wanted[idx];
            state.wanted.splice(idx, 1);
            renderWanted();
            deleteWantedRemote(key).catch(err => {
                console.error('Failed to delete wanted book', err);
            });
            if (state.hardcoverSyncEnabled && removedBook && isHardcoverBook(removedBook)) {
                updateHardcoverWantState(removedBook, false);
            }
        }
    }

    function renderLibrary() {
        const { libraryStatus, libraryResults } = refs;
        libraryResults.innerHTML = '';
        if (!state.library.length) {
            libraryStatus.textContent = 'No books on your bookshelf yet.';
            return;
        }

        libraryStatus.textContent = `You have ${state.library.length} book(s) on your bookshelf.`;
        state.library.forEach(book => {
            libraryResults.appendChild(createBookCard(book, { showWanted: false, showAddToLibrary: false }));
        });
    }

    function renderWanted() {
        const { wantedStatus, wantedResults } = refs;
        wantedResults.innerHTML = '';
        if (state.wantedLoading && !state.wantedLoaded) {
            wantedStatus.textContent = 'Loading wanted books...';
            return;
        }

        if (!state.wanted.length) {
            wantedStatus.textContent = state.wantedLoaded
                ? 'No wanted books yet.'
                : 'No wanted books yet.';
            return;
        }

        wantedStatus.textContent = `You have ${state.wanted.length} wanted book(s).`;
        state.wanted.forEach(book => {
            wantedResults.appendChild(createBookCard(book, {
                showWanted: false,
                showAddToLibrary: false,
                useWantedLayout: true
            }));
        });
    }

    const searchModeBySection = {
        'discover-books': 'title',
        'discover-author': 'author',
        'discover-isbn': 'isbn'
    };

    function showSection(sectionName) {
        navLinks.forEach(link => link.classList.remove('active'));
        if (navMap[sectionName]) {
            navMap[sectionName].classList.add('active');
        }

        Object.entries(sections).forEach(([name, el]) => {
            if (!el) return;
            el.classList.toggle('hidden', name !== sectionName);
        });

        if (searchModeBySection[sectionName]) {
            refs.topbarTitle.textContent = 'Search';
            const mode = searchModeBySection[sectionName];
            if (mode === 'author') {
                window.bookwormAuthorSearch && window.bookwormAuthorSearch.restore();
            } else if (mode === 'isbn') {
                window.bookwormIsbnSearch && window.bookwormIsbnSearch.restore();
            } else {
                window.bookwormBookSearch && window.bookwormBookSearch.restore(mode);
            }
        } else if (sectionName === 'library') {
            refs.topbarTitle.textContent = 'Bookshelf';
        } else if (sectionName === 'wanted') {
            refs.topbarTitle.textContent = 'Wanted';
        } else if (sectionName === 'hardcover-wanted') {
            refs.topbarTitle.textContent = 'Hardcover.app · Want to read';
            window.bookwormHardcover && window.bookwormHardcover.ensureLoaded();
        } else if (sectionName === 'calibre') {
            refs.topbarTitle.textContent = 'Calibre';
            window.bookwormCalibre && window.bookwormCalibre.ensureLoaded();
        } else if (sectionName === 'settings') {
            refs.topbarTitle.textContent = 'Settings';
        }
    }

    navLinks.forEach(link => {
        link.addEventListener('click', () => {
            const sectionName = link.getAttribute('data-section');
            showSection(sectionName);
        });
    });

    loadHardcoverSyncSetting();
    if (refs.hardcoverToggle) {
        refs.hardcoverToggle.addEventListener('change', (event) => {
            setHardcoverSyncEnabled(event.target.checked);
        });
    }
    if (refs.calibrePathSave) {
        refs.calibrePathSave.addEventListener('click', saveCalibrePathSetting);
    }
    if (refs.calibrePathInput) {
        refs.calibrePathInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                saveCalibrePathSetting(event);
            }
        });
    }
    refreshCalibrePathStatus();

    showSection('library');
    loadWantedFromServer();
    if (state.hardcoverSyncEnabled) {
        mirrorHardcoverWanted({ force: false, retries: 8 });
    }

    window.bookwormApp = {
        state,
        createBookCard,
        addToLibrary,
        addToWanted,
        renderLibrary,
        renderWanted,
        showSection,
        handleHardcoverWantedBooks,
        mirrorHardcoverWanted,
        isHardcoverSyncEnabled: () => state.hardcoverSyncEnabled,
        setHardcoverSyncEnabled
    };
    function renderAuthorLinks(names) {
        if (!names.length) {
            return `<div class="book-author">Unknown author</div>`;
        }
        const maxPreview = 4;
        const preview = names.slice(0, maxPreview);
        const hasMore = names.length > maxPreview;

        const renderChip = (name, idx, includeSep = true) => {
            const safeAttr = name.replace(/"/g, '&quot;');
            const sep = includeSep && idx < preview.length - 1 ? '<span class="author-sep">, </span>' : '';
            return `<span class="author-chip"><button class="author-link" data-author="${safeAttr}">${name}</button>${sep}</span>`;
        };

        let html = preview.map((name, idx) => renderChip(name, idx)).join('');

        if (hasMore) {
            const hiddenList = names.slice(maxPreview).map((name, idx) => renderChip(name, idx, idx < names.length - maxPreview - 1)).join('');
            const listId = `author-extra-${Math.random().toString(36).slice(2, 9)}`;
            html += `
                <span class="author-more">
                    <button class="btn-text-toggle author-more-btn" data-target="${listId}">More</button>
                </span>
                <span id="${listId}" class="author-extra hidden">
                    ${hiddenList}
                </span>
            `;
        }

        return `<div class="book-author-links">${html}</div>`;
    }

    function renderAuthorPlain(names) {
        if (!names.length) {
            return `<div class="book-author">Unknown author</div>`;
        }
        const text = names.join(', ');
        return `<div class="book-author">${escapeHtml(text)}</div>`;
    }
})();
