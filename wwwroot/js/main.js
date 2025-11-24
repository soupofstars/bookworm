(function () {
    const state = {
        library: [],
        libraryLoading: false,
        libraryLoaded: false,
        librarySyncText: '',
        librarySearchQuery: '',
        wanted: [],
        wantedLoading: false,
        wantedLoaded: false,
        wantedSearchQuery: '',
        hardcoverSyncEnabled: false
    };
    const HARDCOVER_SYNC_KEY = 'bookworm:hardcover-sync';
    const HARDCOVER_SYNC_INTERVAL_MS = 5 * 60 * 1000; // periodic Hardcover sync when enabled
    const hardcoverIdCache = new Map();
    const wantedSearchCache = new WeakMap();
    const librarySearchCache = new WeakMap();

    const sections = {
        'discover-books': document.getElementById('section-discover-books'),
        'discover-author': document.getElementById('section-discover-author'),
        'discover-isbn': document.getElementById('section-discover-isbn'),
        library: document.getElementById('section-library'),
        wanted: document.getElementById('section-wanted'),
        suggested: document.getElementById('section-suggested'),
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
        librarySearchInput: document.getElementById('library-search-input'),
        librarySearchClear: document.getElementById('library-search-clear'),
        wantedStatus: document.getElementById('wanted-status'),
        wantedResults: document.getElementById('wanted-results'),
        wantedSearchInput: document.getElementById('wanted-search-input'),
        wantedSearchClear: document.getElementById('wanted-search-clear'),
        hardcoverToggle: document.getElementById('toggle-hardcover-sync'),
        hardcoverToggleStatus: document.getElementById('settings-hardcover-status'),
        calibrePathInput: document.getElementById('input-calibre-path'),
        calibrePathSave: document.getElementById('btn-save-calibre-path'),
        calibrePathStatus: document.getElementById('status-calibre-path'),
        calibreSettingsCard: document.getElementById('settings-calibre-card')
    };

    let hardcoverSyncTimer = null;

    function isHardcoverBook(book) {
        return Boolean(book && book.source === 'hardcover');
    }

    function normalizeCalibreBook(raw) {
        const toArray = (value) => {
            if (Array.isArray(value)) return value;
            if (value == null) return [];
            return [value].filter(Boolean);
        };

        const copy = Object.assign({}, raw);
        copy.source = 'calibre';
        copy.calibre_id = raw.id;
        const baseId = raw.id != null ? `calibre-${raw.id}` : (raw.slug || raw.title || Math.random().toString(36).slice(2, 8));
        copy.id = baseId;
        copy.slug = baseId;

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
            if (isbn.replace(/[-\\s]/g, '').length > 10) {
                copy.isbn13 = isbn;
            } else {
                copy.isbn10 = isbn;
            }
        }

        if (typeof raw.fileSizeMb === 'number') {
            copy.size_mb = Math.round(raw.fileSizeMb * 10) / 10;
        }

        if (typeof raw.rating === 'number') {
            copy.rating = raw.rating > 5
                ? Math.round((raw.rating / 2) * 10) / 10
                : raw.rating;
        }

        copy.description = raw.description || (raw.path ? `Calibre path: ${raw.path}` : copy.description);
        const resolvedCover = raw.coverUrl || (raw.hasCover && raw.id != null ? `/calibre-covers/${raw.id}.jpg` : null);
        if (resolvedCover) {
            copy.image = { url: resolvedCover };
            copy.coverUrl = resolvedCover;
        }
        return copy;
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
        if (next) {
            startHardcoverSyncTimer(false);
        } else {
            stopHardcoverSyncTimer();
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
            ${(function () {
                const hasLabel = typeof pill.label === 'string' && pill.label.trim().length > 0;
                const extraClass = pill.className ? ` ${pill.className}` : '';
                const noLabelClass = hasLabel ? '' : ' book-pill--nolabel';
                const labelMarkup = hasLabel ? `<div class="book-pill-label">${pill.label}</div>` : '';
                return `<div class="book-pill${extraClass}${noLabelClass}">
                    ${labelMarkup}
                    <div class="book-pill-value">${pill.value}</div>
                </div>`;
            })()}
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
        if (raw.includes('hardcover')) return 'Hardcover';
        if (raw.includes('openlibrary')) return 'Open Library';
        return raw.charAt(0).toUpperCase() + raw.slice(1);
    }

    function formatSourceValue(book, detailsUrl) {
        const label = formatSourceLabel(book);
        const isHardcover = (book && book.source || '').toString().toLowerCase().includes('hardcover');
        if (isHardcover && detailsUrl) {
            const safeUrl = escapeHtml(detailsUrl);
            const safeLabel = escapeHtml(label);
            return `<a class="book-source-link" href="${safeUrl}" target="_blank" rel="noreferrer">${safeLabel}</a>`;
        }
        return `<span class="book-source">${escapeHtml(label)}</span>`;
    }

    function createBookCard(book, options) {
        const opts = Object.assign(
            {
                showWanted: false,
                showAddToLibrary: false,
                useWantedLayout: false,
                showRemoveWanted: false,
                enableViewLink: true,
                matchScore: null,
                matchScoreLabel: null,
                onRemoveFromWanted: null
            },
            options || {});
        const key = bookKey(book);
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
        let viewLinkForActions = viewLink;
        if (opts.useWantedLayout) {
            // Wanted layout surfaces source and action pills; drop extra view button.
            viewLinkForActions = '';
        }

        let actionMarkup = '';
        if (opts.showWanted) {
            actionMarkup += `<button class="btn btn-wanted btn-wanted-action">Mark as wanted</button>`;
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
            const pills = [
                { label: 'Editions:', value: editions },
                { label: 'Rating:', value: rating }
            ];
            if (opts.useWantedLayout && opts.showRemoveWanted) {
                pills.push({
                    label: 'Source:',
                    value: formatSourceValue(book, detailsUrl)
                });
            }
            return pills;
        })();

        const isbnDisplay = buildIsbnText(book);
        const isbnValue = isbnDisplay && isbnDisplay !== 'N/A'
            ? `<button class="isbn-link" data-isbn="${isbnDisplay.replace(/"/g, '&quot;')}">${isbnDisplay}</button>`
            : isbnDisplay;

        const rightPills = (() => {
            if (opts.useWantedLayout) {
                if (typeof opts.matchScore === 'number') {
                    const scoreText = escapeHtml(String(opts.matchScore));
                    return [{ label: 'Match score:', value: `${scoreText}`, className: 'book-pill--compact' }];
                }
                if (opts.showRemoveWanted) {
                    const safeKey = escapeHtml(key);
                    return [{
                        label: '',
                        value: `<button class="btn btn-ghost btn-remove-wanted" data-key="${safeKey}">Request Removal</button>`
                    }];
                }
                return [{ label: 'Source:', value: formatSourceValue(book, detailsUrl) }];
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

        let inlineViewLink = '';
        if (opts.useWantedLayout && viewLink) {
            inlineViewLink = `<div class="book-inline-action">${viewLink}</div>`;
            viewLinkForActions = '';
        }

        const isbnInlineBlock = opts.useWantedLayout && (isbnValue || inlineViewLink)
            ? `
                <div class="book-info-group">
                    ${isbnValue ? `
                        <div class="book-info-label">ISBN</div>
                        <div class="book-isbn-inline">${isbnValue}</div>
                    ` : ''}
                    ${opts.useWantedLayout ? '' : inlineViewLink}
                </div>
            `
            : '';

        let actionsHtml = '';
        const combinedActions = [actionMarkup, viewLinkForActions].filter(Boolean).join('');
        if (combinedActions) {
            actionsHtml = opts.useWantedLayout
                ? `<div class="book-actions">${combinedActions}</div>`
                : `<div class="book-actions book-pill">${combinedActions}</div>`;
        }

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
            wantedBtn.addEventListener('click', () => {
                if (typeof opts.onAddToWanted === 'function') {
                    opts.onAddToWanted(book);
                } else {
                    addToWanted(book);
                }
            });
        }

        const removeWantedBtn = div.querySelector('.btn-remove-wanted');
        if (removeWantedBtn) {
            removeWantedBtn.addEventListener('click', () => {
                if (typeof opts.onRemoveFromWanted === 'function') {
                    opts.onRemoveFromWanted(book);
                } else {
                    removeFromWanted(book);
                }
            });
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

    async function readHardcoverError(res) {
        try {
            const text = await res.text();
            if (!text) return null;
            return parseHardcoverErrorText(text);
        } catch {
            return null;
        }
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

    function normalizeIsbnValue(value) {
        if (typeof value !== 'string') return null;
        const cleaned = value.replace(/[^0-9Xx]/g, '').toUpperCase();
        return cleaned.length >= 10 ? cleaned : null;
    }

    function collectIsbnCandidates(book) {
        const candidates = [];
        const push = (val) => {
            const normalized = normalizeIsbnValue(val);
            if (normalized) candidates.push(normalized);
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
        return Array.from(new Set(candidates));
    }

    function extractIsbnCandidate(book) {
        const candidates = collectIsbnCandidates(book);
        return candidates.length ? candidates[0] : null;
    }

    async function resolveHardcoverBookId(book) {
        if (!book) return null;
        const existingId = book.id || book.book_id || book.bookId;
        if (isValidHardcoverId(existingId)) return existingId;

        const isbn = extractIsbnCandidate(book);
        if (!isbn) return null;
        if (hardcoverIdCache.has(isbn)) {
            return hardcoverIdCache.get(isbn);
        }

        try {
            const res = await fetch('/hardcover/book/resolve', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ isbn })
            });
            if (!res.ok) {
                const detail = await readHardcoverError(res);
                console.warn('Failed to resolve Hardcover id', res.status, detail);
                return null;
            }
            const data = await res.json();
            const resolvedId = data?.id;
            if (resolvedId) {
                hardcoverIdCache.set(isbn, resolvedId);
                return resolvedId;
            }
        } catch (err) {
            console.warn('Error resolving Hardcover id', err);
        }

        return null;
    }

    function isValidHardcoverId(value) {
        if (value == null) return false;
        const str = String(value).trim();
        if (!str) return false;
        const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
        const digits = /^[0-9]+$/;
        return uuid.test(str) || digits.test(str);
    }

    async function updateHardcoverWantState(book, shouldWant) {
        if (!state.hardcoverSyncEnabled || !isHardcoverBook(book)) return;
        const targetStatusId = shouldWant ? 1 : 6;
        const actionLabel = shouldWant ? 'want-to-read' : 'remove';
        let bookId = book.id ?? book.book_id ?? book.bookId;
        if (!isValidHardcoverId(bookId)) {
            bookId = await resolveHardcoverBookId(book);
        }
        if (!isValidHardcoverId(bookId)) {
            const msg = 'Cannot sync to Hardcover: missing/invalid book id.';
            if (refs.hardcoverToggleStatus) {
                refs.hardcoverToggleStatus.textContent = msg;
            }
            console.warn(msg, book);
            return;
        }
        const payload = { book_id: bookId, status_id: targetStatusId, wantToRead: shouldWant };
        console.debug('[Hardcover] status payload', payload);
        try {
            const url = '/hardcover/want-to-read/status?debug=1';
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const resText = await res.text();
            console.debug('[Hardcover] status response', { status: res.status, body: resText, payload });
            if (!res.ok) {
                const detail = parseHardcoverErrorText(resText);
                const message = detail || `Failed to ${actionLabel} on Hardcover (HTTP ${res.status}).`;
                console.warn('Hardcover status update failed', { status: res.status, detail, payload });
                throw new Error(message);
            }
            console.debug('[Hardcover] status update ok', { payload });
            if (refs.hardcoverToggleStatus) {
                refs.hardcoverToggleStatus.textContent = shouldWant
                    ? 'Synced with Hardcover.'
                    : 'Removed from Hardcover want-to-read.';
            }
        } catch (err) {
            console.error('Hardcover sync failed', err);
            if (refs.hardcoverToggleStatus) {
                const msg = err?.message || 'Error syncing with Hardcover.';
                refs.hardcoverToggleStatus.textContent = msg;
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
            applyWantedCountStatus();
        }
        if (pending.length) {
            await Promise.allSettled(pending);
        }
    }

    function handleHardcoverWantedBooks(books) {
        if (!state.hardcoverSyncEnabled) return Promise.resolve();
        return mergeWantedBooks(books, { skipHardcoverRemote: true }).then(() => {
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

    function syncHardcoverRemovals(remoteList) {
        const remoteKeys = new Set(
            (remoteList || [])
                .filter(isHardcoverBook)
                .map(book => bookKey(book))
        );

        const toRemove = state.wanted.filter(
            book => isHardcoverBook(book) && !remoteKeys.has(bookKey(book))
        );

        if (!toRemove.length) return Promise.resolve();

        // Drop from local state/UI
        state.wanted = state.wanted.filter(
            book => !(isHardcoverBook(book) && !remoteKeys.has(bookKey(book)))
        );
        renderWanted();
        applyWantedCountStatus();

        // Remove from server-side wanted table
        const deletions = toRemove.map(book => {
            const key = bookKey(book);
            return deleteWantedRemote(key).catch(err => {
                console.error('Failed to delete wanted book from local store', { key, err });
            });
        });

        return Promise.allSettled(deletions);
    }

    async function mirrorHardcoverWanted(options) {
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

        try {
            const books = await fetcher({ force: opts.force });
            const remoteList = Array.isArray(books) ? books : [];

            await handleHardcoverWantedBooks(remoteList);
            // Remove local Hardcover items the remote list no longer wants before pushing anything back.
            await syncHardcoverRemovals(remoteList);

            if (!state.hardcoverSyncEnabled) return;

            const remoteKeys = new Set(
                remoteList.filter(isHardcoverBook).map(book => bookKey(book))
            );
            const missingLocal = state.wanted
                .filter(isHardcoverBook)
                .filter(book => !remoteKeys.has(bookKey(book)));

            if (missingLocal.length) {
                await Promise.allSettled(missingLocal.map(book => updateHardcoverWantState(book, true)));
            }
        } catch (err) {
            console.error('Failed to sync Hardcover wanted list', err);
            if (opts.retries > 0) {
                setTimeout(() => mirrorHardcoverWanted({
                    force: opts.force,
                    retries: opts.retries - 1
                }), 900);
            } else if (refs.wantedStatus) {
                refs.wantedStatus.textContent = 'Unable to sync Hardcover wanted list.';
            }
        }
    }

    function startHardcoverSyncTimer(runImmediately = false) {
        stopHardcoverSyncTimer();
        if (!state.hardcoverSyncEnabled) return;
        const tick = () => mirrorHardcoverWanted({ force: true, retries: 2 });
        if (runImmediately) tick();
        hardcoverSyncTimer = setInterval(tick, HARDCOVER_SYNC_INTERVAL_MS);
    }

    function stopHardcoverSyncTimer() {
        if (hardcoverSyncTimer) {
            clearInterval(hardcoverSyncTimer);
            hardcoverSyncTimer = null;
        }
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
            await reconcileWantedAgainstLibrary();
        } catch (err) {
            console.error('Failed to load wanted books', err);
            state.wantedLoading = false;
            refs.wantedStatus.textContent = 'Unable to load wanted books.';
        }
    }

    function addToWanted(book, options) {
        const opts = options || {};
        const key = bookKey(book);
        if (!state.wanted.some(b => bookKey(b) === key)) {
            state.wanted.push(book);
            renderWanted();
            refs.wantedStatus.textContent = 'Book added to Wanted.';
            saveWantedRemote(key, book)
                .then(() => {
                    if (typeof opts.onSaved === 'function') {
                        opts.onSaved();
                    }
                })
                .catch(err => {
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
            if (typeof opts.onAlready === 'function') {
                opts.onAlready();
            }
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

    function removeFromWanted(bookOrKey, options) {
        const opts = options || {};
        const key = typeof bookOrKey === 'string' ? bookOrKey : bookKey(bookOrKey);
        const idx = state.wanted.findIndex(b => bookKey(b) === key);
        if (idx === -1) return;
        const removedBook = state.wanted[idx];
        state.wanted.splice(idx, 1);
        renderWanted();
        applyWantedCountStatus();

        deleteWantedRemote(key).catch(err => {
            console.error('Failed to delete wanted book', err);
        });

        if (state.hardcoverSyncEnabled && removedBook && isHardcoverBook(removedBook)) {
            updateHardcoverWantState(removedBook, false);
        }

        if (typeof opts.onRemoved === 'function') {
            opts.onRemoved(removedBook);
        }
    }

    let libraryLoadPromise = null;

    function setLibraryFromCalibre(rawBooks, syncText) {
        const normalized = Array.isArray(rawBooks)
            ? rawBooks.map(normalizeCalibreBook)
            : [];
        state.library = normalized;
        state.libraryLoaded = true;
        state.libraryLoading = false;
        state.librarySyncText = syncText || '';
        renderLibrary();
        reconcileWantedAgainstLibrary().catch(err => console.error('Wanted/Calibre reconcile failed', err));
        return normalized.length;
    }

    async function loadLibraryFromCalibre(forceReload = false) {
        if (state.libraryLoading && libraryLoadPromise) {
            return libraryLoadPromise;
        }
        if (state.libraryLoaded && !forceReload) {
            return Promise.resolve(state.library);
        }

        state.libraryLoading = true;
        renderLibrary();

        libraryLoadPromise = (async () => {
            try {
                const res = await fetch('/calibre/books?take=0');
                if (!res.ok) {
                    const detail = await res.text().catch(() => '');
                    throw new Error(detail || `Unable to load Calibre library (HTTP ${res.status}).`);
                }

                const data = await res.json();
                const books = Array.isArray(data?.books) ? data.books : [];
                const syncText = data?.lastSync
                    ? `Last sync: ${new Date(data.lastSync).toLocaleString()}`
                    : 'Calibre not synced yet.';

                const count = setLibraryFromCalibre(books, syncText);
                if (count === 0 && refs.libraryStatus) {
                    refs.libraryStatus.textContent = data?.lastSync
                        ? 'No books in Calibre mirror. Click “Sync Calibre” to refresh.'
                        : 'Calibre not synced yet. Click “Sync Calibre” to import your library.';
                }

                return state.library;
            } catch (err) {
                console.error('Failed to load Calibre library', err);
                state.libraryLoaded = false;
                if (refs.libraryStatus) {
                    refs.libraryStatus.textContent = err?.message || 'Unable to load Calibre library.';
                }
                return [];
            } finally {
                state.libraryLoading = false;
                renderLibrary();
            }
        })();

        return libraryLoadPromise;
    }

    function reloadLibraryFromCalibre() {
        state.libraryLoaded = false;
        return loadLibraryFromCalibre(true);
    }

    function librarySearchKey(book) {
        if (!book) return '';
        if (librarySearchCache.has(book)) {
            return librarySearchCache.get(book);
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
        if (Array.isArray(book.cached_contributors)) {
            book.cached_contributors.forEach(c => push(c?.name));
        }
        if (Array.isArray(book.contributions)) {
            book.contributions.forEach(c => push(c?.author?.name));
        }
        const isbn = buildIsbnText(book);
        if (isbn && isbn !== 'N/A') {
            push(isbn);
        }

        const text = parts.join(' ');
        librarySearchCache.set(book, text);
        return text;
    }

    function libraryMatchesQuery(book, normalizedQuery) {
        if (!normalizedQuery) return true;
        return librarySearchKey(book).includes(normalizedQuery);
    }

    function setLibrarySearchQuery(raw) {
        state.librarySearchQuery = (raw || '').trim();
        renderLibrary();
    }

    function applyLibraryCountStatus(visibleCount, rawQueryOverride) {
        if (!refs.libraryStatus) return;
        const total = state.library.length;
        const syncSuffix = state.librarySyncText ? ` ${state.librarySyncText}` : '';
        const resolvedQuery = rawQueryOverride != null ? rawQueryOverride : state.librarySearchQuery;
        const rawQuery = (resolvedQuery || '').trim();
        if (rawQuery) {
            const normalized = rawQuery.toLowerCase();
            const visible = typeof visibleCount === 'number'
                ? visibleCount
                : state.library.filter(book => libraryMatchesQuery(book, normalized)).length;
            refs.libraryStatus.textContent = visible === 0
                ? `No matches for "${rawQuery}".${syncSuffix ? ` ${syncSuffix}` : ''}`
                : `Showing ${visible} of ${total} Calibre book(s).${syncSuffix}`;
            return;
        }
        const base = total
            ? `Calibre library: showing ${total} book(s).${syncSuffix}`
            : 'No books on your bookshelf yet.';
        refs.libraryStatus.textContent = base;
    }

    function renderLibrary() {
        const { libraryStatus, libraryResults } = refs;
        libraryResults.innerHTML = '';

        if (state.libraryLoading && !state.libraryLoaded) {
            libraryStatus.textContent = 'Loading Calibre library…';
            return;
        }

        if (!state.library.length) {
            if (state.libraryLoaded) {
                const syncText = state.librarySyncText || '';
                const unsynced = syncText.toLowerCase().includes('not synced');
                if (unsynced) {
                    libraryStatus.textContent = 'Calibre not synced yet. Click “Sync Calibre” to import your library.';
                } else {
                    const suffix = syncText ? ` ${syncText}` : '';
                    libraryStatus.textContent = `No books found in Calibre mirror.${suffix} Click “Sync Calibre” to refresh.`;
                }
            } else {
                libraryStatus.textContent = 'No books on your bookshelf yet.';
            }
            return;
        }

        const rawQuery = state.librarySearchQuery || '';
        const normalizedQuery = rawQuery.trim().toLowerCase();
        const visible = normalizedQuery
            ? state.library.filter(book => libraryMatchesQuery(book, normalizedQuery))
            : state.library;

        if (!visible.length) {
            const suffix = state.librarySyncText ? ` ${state.librarySyncText}` : '';
            libraryStatus.textContent = rawQuery
                ? `No matches for "${rawQuery.trim()}".${suffix ? ` ${suffix}` : ''}`
                : `No books found in Calibre mirror.${suffix} Click “Sync Calibre” to refresh.`;
            return;
        }

        applyLibraryCountStatus(visible.length, rawQuery);

        visible.forEach(book => {
            libraryResults.appendChild(createBookCard(book, {
                showWanted: false,
                showAddToLibrary: false,
                enableViewLink: false
            }));
        });
    }

    function wantedSearchKey(book) {
        if (!book) return '';
        if (wantedSearchCache.has(book)) {
            return wantedSearchCache.get(book);
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
        push(book.base_genres);
        push(book.genre);
        if (typeof book.cached_tags === 'string') {
            push(book.cached_tags);
        } else if (Array.isArray(book.cached_tags)) {
            book.cached_tags.forEach(push);
        }
        if (Array.isArray(book.cached_contributors)) {
            book.cached_contributors.forEach(c => push(c?.name));
        }
        if (Array.isArray(book.contributions)) {
            book.contributions.forEach(c => push(c?.author?.name));
        }
        const isbn = buildIsbnText(book);
        if (isbn && isbn !== 'N/A') {
            push(isbn);
        }

        const text = parts.join(' ');
        wantedSearchCache.set(book, text);
        return text;
    }

    function wantedMatchesQuery(book, normalizedQuery) {
        if (!normalizedQuery) return true;
        return wantedSearchKey(book).includes(normalizedQuery);
    }

    function setWantedSearchQuery(raw) {
        state.wantedSearchQuery = (raw || '').trim();
        renderWanted();
    }

    function applyWantedCountStatus(visibleCount, rawQueryOverride) {
        if (!refs.wantedStatus) return;
        const total = state.wanted.length;
        const resolvedQuery = rawQueryOverride != null ? rawQueryOverride : state.wantedSearchQuery;
        const rawQuery = (resolvedQuery || '').trim();
        if (rawQuery) {
            const normalized = rawQuery.toLowerCase();
            const visible = typeof visibleCount === 'number'
                ? visibleCount
                : state.wanted.filter(book => wantedMatchesQuery(book, normalized)).length;
            refs.wantedStatus.textContent = visible === 0
                ? `No matches for "${rawQuery}".`
                : `Showing ${visible} of ${total} wanted book(s).`;
            return;
        }
        refs.wantedStatus.textContent = total
            ? `You have ${total} wanted book(s).`
            : 'No wanted books yet.';
    }

    function renderWanted() {
        const { wantedStatus, wantedResults } = refs;
        wantedResults.innerHTML = '';
        if (state.wantedLoading && !state.wantedLoaded) {
            wantedStatus.textContent = 'Loading wanted books...';
            return;
        }

        const total = state.wanted.length;
        if (!total) {
            wantedStatus.textContent = state.wantedLoaded
                ? 'No wanted books yet.'
                : 'No wanted books yet.';
            return;
        }

        const rawQuery = state.wantedSearchQuery || '';
        const normalizedQuery = rawQuery.trim().toLowerCase();
        const visible = normalizedQuery
            ? state.wanted.filter(book => wantedMatchesQuery(book, normalizedQuery))
            : state.wanted;

        if (!visible.length) {
            wantedStatus.textContent = rawQuery
                ? `No matches for "${rawQuery.trim()}".`
                : 'No wanted books yet.';
            return;
        }

        applyWantedCountStatus(visible.length, rawQuery);

        visible.forEach(book => {
            wantedResults.appendChild(createBookCard(book, {
                showWanted: false,
                showAddToLibrary: false,
                useWantedLayout: true,
                showRemoveWanted: true,
                onRemoveFromWanted: () => removeFromWanted(book)
            }));
        });
    }

    async function reconcileWantedAgainstLibrary() {
        if (!state.wanted.length || !state.library.length) return;

        const libraryIsbns = new Set();
        state.library.forEach(book => {
            collectIsbnCandidates(book).forEach(isbn => libraryIsbns.add(isbn));
        });
        if (!libraryIsbns.size) return;

        const toRemoveKeys = new Set();
        state.wanted.forEach(book => {
            const candidates = collectIsbnCandidates(book);
            if (candidates.length && candidates.some(isbn => libraryIsbns.has(isbn))) {
                toRemoveKeys.add(bookKey(book));
            }
        });

        if (!toRemoveKeys.size) return;

        const removedBooks = state.wanted.filter(book => toRemoveKeys.has(bookKey(book)));
        state.wanted = state.wanted.filter(book => !toRemoveKeys.has(bookKey(book)));
        renderWanted();
        if (refs.wantedStatus) {
            refs.wantedStatus.textContent = `Removed ${toRemoveKeys.size} wanted book(s) found in Calibre.`;
        }

        const tasks = [];
        removedBooks.forEach(book => {
            const key = bookKey(book);
            tasks.push(deleteWantedRemote(key).catch(err => {
                console.error('Failed to delete wanted book after Calibre sync', { key, err });
            }));
        });

        removedBooks
            .filter(isHardcoverBook)
            .forEach(book => tasks.push(updateHardcoverWantState(book, false)));

        if (tasks.length) {
            await Promise.allSettled(tasks);
        }
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
            loadLibraryFromCalibre();
        } else if (sectionName === 'wanted') {
            refs.topbarTitle.textContent = 'Wanted';
        } else if (sectionName === 'suggested') {
            refs.topbarTitle.textContent = 'Suggested';
            window.bookwormSuggested && window.bookwormSuggested.ensureLoaded();
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

    if (refs.librarySearchInput) {
        refs.librarySearchInput.addEventListener('input', (event) => {
            setLibrarySearchQuery(event.target.value);
        });
    }
    if (refs.librarySearchClear && refs.librarySearchInput) {
        refs.librarySearchClear.addEventListener('click', () => {
            refs.librarySearchInput.value = '';
            setLibrarySearchQuery('');
            refs.librarySearchInput.focus();
        });
    }

    if (refs.wantedSearchInput) {
        refs.wantedSearchInput.addEventListener('input', (event) => {
            setWantedSearchQuery(event.target.value);
        });
    }
    if (refs.wantedSearchClear && refs.wantedSearchInput) {
        refs.wantedSearchClear.addEventListener('click', () => {
            refs.wantedSearchInput.value = '';
            setWantedSearchQuery('');
            refs.wantedSearchInput.focus();
        });
    }

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
        startHardcoverSyncTimer(false);
    }

    window.bookwormApp = {
        state,
        createBookCard,
        normalizeCalibreBook,
        setLibraryFromCalibre,
        loadLibraryFromCalibre,
        reloadLibraryFromCalibre,
        addToLibrary,
        addToWanted,
        removeFromWanted,
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
