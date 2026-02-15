(function () {
    const state = {
        library: [],
        libraryLoading: false,
        libraryLoaded: false,
        librarySyncText: '',
        librarySort: 'title-asc',
        libraryPage: 1,
        libraryPageSize: 30,
        librarySearchQuery: '',
        lastCalibreSnapshot: null,
        lastCalibreBookCount: null,
        wanted: [],
        wantedLoading: false,
        wantedLoaded: false,
        wantedSearchQuery: '',
        wantedSort: 'title-asc',
        hardcoverSyncEnabled: false
    };
    const HARDCOVER_SYNC_KEY = 'bookworm:hardcover-sync';
    const HARDCOVER_SYNC_INTERVAL_MS = 5 * 60 * 1000; // periodic Hardcover sync when enabled
    const CALIBRE_REFRESH_INTERVAL_MS = 10 * 60 * 1000; // periodic bookshelf reload to reflect background sync
    const hardcoverIdCache = new Map();
    const wantedSearchCache = new WeakMap();
    const librarySearchCache = new WeakMap();
    const hardcoverImageCache = new Map();

    const sections = {
        'discover-books': document.getElementById('section-discover-books'),
        'discover-author': document.getElementById('section-discover-author'),
        'discover-isbn': document.getElementById('section-discover-isbn'),
        library: document.getElementById('section-library'),
        wanted: document.getElementById('section-wanted'),
        suggested: document.getElementById('section-suggested'),
        'suggested-ignored': document.getElementById('section-suggested-ignored'),
        'hardcover-wanted': document.getElementById('section-hardcover-wanted'),
        'hardcover-mismatch': document.getElementById('section-hardcover-mismatch'),
        'hardcover-owned': document.getElementById('section-hardcover-owned'),
        calibre: document.getElementById('section-calibre'),
        logs: document.getElementById('section-logs'),
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
        librarySortSelect: document.getElementById('library-sort'),
        libraryPageSizeSelect: document.getElementById('library-page-size'),
        librarySearchInput: document.getElementById('library-search-input'),
        librarySearchClear: document.getElementById('library-search-clear'),
        libraryPagination: document.getElementById('library-pagination'),
        libraryPaginationInfo: document.getElementById('library-pagination-info'),
        libraryPaginationPrev: document.getElementById('library-pagination-prev'),
        libraryPaginationNext: document.getElementById('library-pagination-next'),
        libraryPaginationBottom: document.getElementById('library-pagination-bottom'),
        libraryPaginationBottomInfo: document.getElementById('library-pagination-bottom-info'),
        libraryPaginationBottomPrev: document.getElementById('library-pagination-bottom-prev'),
        libraryPaginationBottomNext: document.getElementById('library-pagination-bottom-next'),
        wantedStatus: document.getElementById('wanted-status'),
        wantedResults: document.getElementById('wanted-results'),
        wantedSearchInput: document.getElementById('wanted-search-input'),
        wantedSearchClear: document.getElementById('wanted-search-clear'),
        wantedSortSelect: document.getElementById('wanted-sort'),
        hardcoverToggle: document.getElementById('toggle-hardcover-sync'),
        hardcoverToggleStatus: document.getElementById('settings-hardcover-status'),
        hardcoverListInput: document.getElementById('input-hardcover-list-id'),
        hardcoverListSave: document.getElementById('btn-save-hardcover-list'),
        hardcoverListStatus: document.getElementById('status-hardcover-list'),
        calibrePathInput: document.getElementById('input-calibre-path'),
        calibrePathSave: document.getElementById('btn-save-calibre-path'),
        calibrePathStatus: document.getElementById('status-calibre-path'),
        calibreSettingsCard: document.getElementById('settings-calibre-card'),
        serviceStatus: document.getElementById('service-status')
    };

    let hardcoverSyncTimer = null;
    let calibreRefreshTimer = null;
    let hardcoverListId = null;
    let serviceStatusTimer = null;

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
        copy.hardcover_id = raw.hardcoverId || raw.hardcover_id || null;
        copy.hardcoverId = copy.hardcover_id;
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

    async function refreshHardcoverListStatus() {
        if (!refs.hardcoverListStatus) return;
        try {
            const res = await fetch('/settings/hardcover/list');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            hardcoverListId = data?.listId || null;
            if (refs.hardcoverListInput) {
                refs.hardcoverListInput.value = hardcoverListId || '';
            }
            refs.hardcoverListStatus.textContent = hardcoverListId
                ? `Hardcover list id set to ${hardcoverListId}.`
                : 'No Hardcover list configured.';
        } catch (err) {
            console.error('Failed to load Hardcover list id', err);
            refs.hardcoverListStatus.textContent = 'Unable to load Hardcover list id.';
        }
    }

    async function saveHardcoverListSetting(event) {
        if (event) event.preventDefault();
        if (!refs.hardcoverListInput || !refs.hardcoverListStatus) return;
        const value = refs.hardcoverListInput.value || '';
        try {
            const res = await fetch('/settings/hardcover/list', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ listId: value.trim() || null })
            });
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || `HTTP ${res.status}`);
            }
            const data = await res.json();
            hardcoverListId = data?.listId || null;
            refs.hardcoverListStatus.textContent = hardcoverListId
                ? `Hardcover list id set to ${hardcoverListId}.`
                : 'No Hardcover list configured.';
        } catch (err) {
            console.error('Failed to save Hardcover list id', err);
            refs.hardcoverListStatus.textContent = 'Unable to save Hardcover list id.';
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

    async function updateServiceStatus() {
        const el = refs.serviceStatus;
        if (!el) return;
        const setMessage = (text, isError) => {
            el.textContent = text;
            el.classList.toggle('hidden', !text);
            el.classList.toggle('status-error', !!isError);
        };
        try {
            const res = await fetch(`/health/openlibrary?ts=${Date.now()}`, { cache: 'no-store' });
            if (!res.ok) {
                setMessage('OpenLibrary status unavailable.', true);
                return;
            }
            const data = await res.json();
            if (data?.reachable) {
                setMessage('', false);
            } else {
                const reason = data?.error || `HTTP ${data?.status || 'unavailable'}`;
                setMessage(`OpenLibrary looks down or blocked (reason: ${reason}). Search may be unavailable.`, true);
            }
        } catch (err) {
            console.warn('Status check failed', err);
            setMessage('Cannot reach OpenLibrary; search may be down.', true);
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
        const sourceRaw = (book && book.source) ? String(book.source).toLowerCase() : '';
        const isHardcover = sourceRaw.includes('hardcover');
        const isOpenLibrary = sourceRaw.includes('openlibrary');
        if ((isHardcover || isOpenLibrary) && detailsUrl) {
            const safeUrl = escapeHtml(detailsUrl);
            const safeLabel = escapeHtml(label);
            return `<a class="book-source-link" href="${safeUrl}" target="_blank" rel="noreferrer">${safeLabel}</a>`;
        }
        return `<span class="book-source">${escapeHtml(label)}</span>`;
    }

    function renderHardcoverStatus(book) {
        const hardcoverId = book?.hardcoverId ?? book?.hardcover_id;
        if (hardcoverId) {
            return `<span class="hc-status hc-status-neutral">Synced</span>`;
        }
        return `<span class="hc-status hc-status-neutral">Not synced</span>`;
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
                onRemoveFromWanted: null,
                showDeletePill: false,
                deleteLabel: 'Delete',
                onDelete: null,
                sourceInRightPill: false,
                showIsbnInline: false,
                showHardcoverStatus: false,
                suggestedHide: null,
                enableSearchLinks: false
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
        if (!coverUrl) {
            const isbnCandidate = (book.isbn13 || book.isbn_13 || book.isbn || book.isbn10 || book.isbn_10);
            const normalizedIsbn = Array.isArray(isbnCandidate)
                ? (isbnCandidate.find(Boolean) || '')
                : (isbnCandidate || '');
            const cleanIsbn = normalizedIsbn.replace(/[^0-9Xx]/g, '');
            if (cleanIsbn.length >= 10) {
                coverUrl = `https://covers.openlibrary.org/b/isbn/${cleanIsbn}-M.jpg`;
            }
        }
        const isbnFallback = extractIsbnCandidate(book);
        const googleCover = isbnFallback
            ? `https://books.google.com/books/content?vid=ISBN${isbnFallback}&printsec=frontcover&img=1&zoom=1`
            : null;
        if (!coverUrl && googleCover) {
            coverUrl = googleCover;
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
        // Drop view button when source pill renders a link (e.g., Open Library/Hardcover).
        if (detailsUrl && (book.source === 'openlibrary' || (book.source && String(book.source).toLowerCase().includes('hardcover')))) {
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
            if (opts.useWantedLayout && typeof opts.suggestedHide === 'function') {
                return [
                    { label: 'Editions:', value: editions },
                    { label: '', value: `<button class="btn btn-ghost btn-mark-unwanted">Mark as unwanted</button>` }
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
        const isbnValueForPill = isbnDisplay && isbnDisplay !== 'N/A'
            ? (opts.enableSearchLinks ? isbnValue : escapeHtml(isbnDisplay))
            : 'N/A';
        const nonCalibreIsbnLine = (!isCalibre && !opts.useWantedLayout && opts.showIsbnInline && isbnDisplay && isbnDisplay !== 'N/A')
            ? `
                <div class="book-info-label book-isbn-label">ISBN</div>
                <div class="book-isbn-inline isbn-inline">${escapeHtml(isbnDisplay)}</div>
              `
            : '';

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
                const series = book.series ? escapeHtml(book.series) : 'Standalone';
                const pills = [
                    { label: 'ISBN:', value: isbnValueForPill }
                ];
                if (opts.showHardcoverStatus) {
                    pills.push({ label: 'Hardcover:', value: renderHardcoverStatus(book) });
                } else {
                    pills.push({ label: 'Series:', value: series });
                }
                return pills;
            }
            if (opts.sourceInRightPill) {
                return [{ label: 'Source:', value: formatSourceValue(book, detailsUrl) }];
            }
            const pills = [{ label: 'ISBN:', value: isbnValue }];
            return pills;
        })();

        if (opts.showDeletePill) {
            const safeKey = escapeHtml(key);
            const deleteLabel = escapeHtml(opts.deleteLabel || 'Delete');
            rightPills.push({
                label: '',
                value: `<button class="btn btn-ghost btn-delete-book" data-key="${safeKey}">${deleteLabel}</button>`
            });
        }

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

        const authorsMarkup = isCalibre && !opts.enableSearchLinks
            ? renderAuthorPlain(authorNames)
            : renderAuthorLinks(authorNames);
        const calibreIsbnLine = (isCalibre && opts.showIsbnInline && isbnDisplay && isbnDisplay !== 'N/A')
            ? `
                <div class="book-info-label book-isbn-label">ISBN</div>
                <div class="book-isbn-inline isbn-inline">${opts.enableSearchLinks ? isbnValue : escapeHtml(isbnDisplay)}</div>
              `
            : '';

        const authorsBlock = `
            <div class="book-info-group">
                <div class="book-info-label">Authors</div>
                ${authorsMarkup}
                ${calibreIsbnLine}
                ${nonCalibreIsbnLine}
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
            if (googleCover && googleCover !== coverUrl && coverEl.tagName === 'IMG') {
                coverEl.addEventListener('error', () => {
                    coverEl.setAttribute('src', googleCover);
                }, { once: true });
            }
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

        const deleteBtn = div.querySelector('.btn-delete-book');
        if (deleteBtn) {
            deleteBtn.addEventListener('click', () => {
                if (typeof opts.onDelete === 'function') {
                    const maybePromise = opts.onDelete(book, { button: deleteBtn, card: div });
                    if (maybePromise && typeof maybePromise.then === 'function') {
                        deleteBtn.disabled = true;
                        maybePromise.finally(() => { deleteBtn.disabled = false; });
                    }
                } else if (typeof opts.onRemoveFromWanted === 'function') {
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

        const unwantedBtn = div.querySelector('.btn-mark-unwanted');
        if (unwantedBtn && typeof opts.suggestedHide === 'function') {
            unwantedBtn.addEventListener('click', (event) => {
                event.preventDefault();
                opts.suggestedHide(book);
            });
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

    const HARDCOVER_QUEUE_INTERVAL_MS = 30 * 1000; // 2 per minute
    const hardcoverActionQueue = [];
    let hardcoverQueueTimer = null;

    async function sendHardcoverWantState(book, shouldWant) {
        if (!state.hardcoverSyncEnabled) return;
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
        if (refs.hardcoverToggleStatus) {
            refs.hardcoverToggleStatus.textContent = shouldWant
                ? 'Synced with Hardcover.'
                : 'Removed from Hardcover want-to-read.';
        }
    }

    function processHardcoverQueue() {
        if (hardcoverActionQueue.length === 0) {
            hardcoverQueueTimer = null;
            return;
        }
        const { book, shouldWant, resolve, reject } = hardcoverActionQueue.shift();
        sendHardcoverWantState(book, shouldWant)
            .then(resolve)
            .catch(reject)
            .finally(() => {
                hardcoverQueueTimer = setTimeout(processHardcoverQueue, HARDCOVER_QUEUE_INTERVAL_MS);
            });
    }

    function enqueueHardcoverWantState(book, shouldWant) {
        if (!state.hardcoverSyncEnabled) {
            return Promise.resolve();
        }
        return new Promise((resolve, reject) => {
            hardcoverActionQueue.push({ book, shouldWant, resolve, reject });
            if (!hardcoverQueueTimer) {
                processHardcoverQueue();
            }
        });
    }

    function updateHardcoverWantState(book, shouldWant) {
        return enqueueHardcoverWantState(book, shouldWant);
    }

    async function syncHardcoverAfterWantedAdd(book) {
        if (!state.hardcoverSyncEnabled) return;
        await updateHardcoverWantState(book, true);
        mirrorHardcoverWanted({ force: true, retries: 2 });
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

    function startCalibreRefreshTimer() {
        stopCalibreRefreshTimer();
        const tick = () => {
            pollCalibreSyncState();
        };
        calibreRefreshTimer = setInterval(tick, CALIBRE_REFRESH_INTERVAL_MS);
    }

    function stopCalibreRefreshTimer() {
        if (calibreRefreshTimer) {
            clearInterval(calibreRefreshTimer);
            calibreRefreshTimer = null;
        }
    }

    function startServiceStatusTimer() {
        if (!refs.serviceStatus) return;
        if (serviceStatusTimer) {
            clearInterval(serviceStatusTimer);
        }
        const tick = () => updateServiceStatus();
        tick();
        serviceStatusTimer = setInterval(tick, 5 * 60 * 1000);
    }

    async function pollCalibreSyncState() {
        try {
            const res = await fetch('/calibre/sync/state', { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();
            const snapshot = data?.lastSync || data?.lastSnapshot || null;
            const normalizedSnapshot = snapshot ? new Date(snapshot).toISOString() : null;
            const count = typeof data?.bookCount === 'number' ? data.bookCount : null;

            const snapshotChanged = normalizedSnapshot !== (state.lastCalibreSnapshot || null);
            const countChanged = count != null && state.lastCalibreBookCount != null && count !== state.lastCalibreBookCount;

            if (snapshotChanged || countChanged) {
                state.lastCalibreSnapshot = normalizedSnapshot;
                state.lastCalibreBookCount = count != null ? count : state.lastCalibreBookCount;
                reloadLibraryFromCalibre(true);
                if (window.bookwormCalibre && typeof window.bookwormCalibre.reload === 'function') {
                    window.bookwormCalibre.reload();
                }
            }
        } catch (err) {
            // soft-fail; will try again on next tick
            console.debug('Calibre sync state poll failed', err);
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
                    syncHardcoverAfterWantedAdd(book);
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
            syncHardcoverAfterWantedAdd(book);
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
        state.libraryPage = 1;
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
                const res = await fetch(`/calibre/books?take=0&ts=${Date.now()}`, { cache: 'no-store' });
                if (!res.ok) {
                    const detail = await res.text().catch(() => '');
                    throw new Error(detail || `Unable to load Calibre library (HTTP ${res.status}).`);
                }

                const data = await res.json();
                const books = Array.isArray(data?.books) ? data.books : [];
                const syncText = data?.lastSync
                    ? `Last sync: ${new Date(data.lastSync).toLocaleString()}`
                    : 'Calibre not synced yet.';
                state.lastCalibreSnapshot = data?.lastSync
                    ? new Date(data.lastSync).toISOString()
                    : null;
                state.lastCalibreBookCount = typeof data?.count === 'number'
                    ? data.count
                    : books.length;

                const count = setLibraryFromCalibre(books, syncText);
                if (count === 0 && refs.libraryStatus) {
                    refs.libraryStatus.textContent = data?.lastSync
                        ? 'No books in Calibre mirror yet. Waiting for the next automatic sync.'
                        : 'Calibre not synced yet. Automatic sync will populate your library once the metadata path is set.';
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

    function primaryAuthor(book) {
        const authors = Array.isArray(book.author_names) && book.author_names.length
            ? book.author_names
            : Array.isArray(book.authors) && book.authors.length
                ? book.authors
                : [];
        return authors.length ? authors[0] : '';
    }

    function sortLibraryList(list, sortKey) {
        const arr = Array.isArray(list) ? list.slice() : [];
        const byString = (getter, direction) => (a, b) => {
            const av = getter(a).toLowerCase();
            const bv = getter(b).toLowerCase();
            if (av === bv) return 0;
            return direction === 'asc' ? (av < bv ? -1 : 1) : (av > bv ? -1 : 1);
        };
        const byNumber = (getter, direction) => (a, b) => {
            const av = getter(a);
            const bv = getter(b);
            if (av === bv) return 0;
            return direction === 'asc' ? (av - bv) : (bv - av);
        };

        switch (sortKey) {
            case 'title-desc':
                return arr.sort(byString(b => b.title || '', 'desc'));
            case 'author-asc':
                return arr.sort(byString(primaryAuthor, 'asc'));
            case 'author-desc':
                return arr.sort(byString(primaryAuthor, 'desc'));
            case 'title-asc':
            default:
                return arr.sort(byString(b => b.title || '', 'asc'));
        }
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
                : `Showing ${visible} of ${total} book(s).${syncSuffix}`;
            return;
        }
        const base = total
            ? `Showing ${total} book(s).${syncSuffix}`
            : 'No books on your bookshelf yet.';
        refs.libraryStatus.textContent = base;
    }

    function updateLibraryPaginationUI(totalVisible, totalPages, showingCount) {
        const sets = [
            { container: refs.libraryPagination, info: refs.libraryPaginationInfo, prev: refs.libraryPaginationPrev, next: refs.libraryPaginationNext },
            { container: refs.libraryPaginationBottom, info: refs.libraryPaginationBottomInfo, prev: refs.libraryPaginationBottomPrev, next: refs.libraryPaginationBottomNext }
        ];

        const shouldShow = state.libraryPageSize !== 0 && totalVisible > state.libraryPageSize;
        sets.forEach(set => {
            if (!set.container) return;
            if (!shouldShow) {
                set.container.classList.add('hidden');
                return;
            }

            set.container.classList.remove('hidden');
            if (set.info) {
                set.info.textContent = `Page ${state.libraryPage} of ${totalPages}`;
            }
            if (set.prev) set.prev.disabled = state.libraryPage <= 1;
            if (set.next) set.next.disabled = state.libraryPage >= totalPages;
        });
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
                    libraryStatus.textContent = 'Calibre not synced yet. Automatic sync will populate your library once the metadata path is set.';
                } else {
                    const suffix = syncText ? ` ${syncText}` : '';
                    libraryStatus.textContent = `No books found in Calibre mirror.${suffix} Waiting for the next automatic sync.`;
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
        const sorted = sortLibraryList(visible, state.librarySort);

        if (!sorted.length) {
            const suffix = state.librarySyncText ? ` ${state.librarySyncText}` : '';
            libraryStatus.textContent = rawQuery
                ? `No matches for "${rawQuery.trim()}".${suffix ? ` ${suffix}` : ''}`
                : `No books found in Calibre mirror.${suffix} Waiting for the next automatic sync.`;
            updateLibraryPaginationUI(0, 1, 0);
            return;
        }

        const totalVisible = sorted.length;
        const pageSize = state.libraryPageSize === 0 ? totalVisible : state.libraryPageSize;
        const totalPages = Math.max(1, Math.ceil(totalVisible / pageSize));
        if (state.libraryPage > totalPages) state.libraryPage = totalPages;
        if (state.libraryPage < 1) state.libraryPage = 1;
        const start = state.libraryPageSize === 0 ? 0 : (state.libraryPage - 1) * pageSize;
        const end = state.libraryPageSize === 0 ? sorted.length : start + pageSize;
        const pageItems = sorted.slice(start, end);

        const syncSuffix = state.librarySyncText ? ` ${state.librarySyncText}` : '';
        const baseStatus = rawQuery
            ? `${totalVisible} match(es).${syncSuffix}`
            : `${totalVisible} book(s).${syncSuffix}`;
        libraryStatus.textContent = baseStatus.trim();
        updateLibraryPaginationUI(totalVisible, totalPages, pageItems.length);

        pageItems.forEach(book => {
            libraryResults.appendChild(createBookCard(book, {
                showWanted: false,
                showAddToLibrary: false,
                enableViewLink: false,
                showHardcoverStatus: true
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

        const sorted = sortLibraryList(visible, state.wantedSort);
        applyWantedCountStatus(sorted.length, rawQuery);

        sorted.forEach(book => {
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
        } else if (sectionName === 'suggested-ignored') {
            refs.topbarTitle.textContent = 'Ignored suggestions';
            window.bookwormSuggestedIgnored && window.bookwormSuggestedIgnored.ensureLoaded();
        } else if (sectionName === 'hardcover-wanted') {
            refs.topbarTitle.textContent = 'Hardcover.app · Want to read';
            window.bookwormHardcover && window.bookwormHardcover.ensureLoaded();
        } else if (sectionName === 'hardcover-owned') {
            refs.topbarTitle.textContent = 'Hardcover.app · Owned list';
            window.bookwormHardcoverOwned && window.bookwormHardcoverOwned.ensureLoaded();
        } else if (sectionName === 'hardcover-mismatch') {
            refs.topbarTitle.textContent = 'Not synced with Hardcover';
        } else if (sectionName === 'calibre') {
            refs.topbarTitle.textContent = 'Calibre';
            window.bookwormCalibre && window.bookwormCalibre.ensureLoaded();
        } else if (sectionName === 'logs') {
            refs.topbarTitle.textContent = 'Activity log';
            window.bookwormLogs && window.bookwormLogs.ensureLoaded();
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
            state.libraryPage = 1;
            renderLibrary();
        });
    }
    if (refs.librarySearchClear && refs.librarySearchInput) {
        refs.librarySearchClear.addEventListener('click', () => {
            refs.librarySearchInput.value = '';
            setLibrarySearchQuery('');
            state.libraryPage = 1;
            refs.librarySearchInput.focus();
            renderLibrary();
        });
    }
    if (refs.librarySortSelect) {
        refs.librarySortSelect.value = state.librarySort;
        refs.librarySortSelect.addEventListener('change', (event) => {
            const value = event.target.value || 'title-asc';
            state.librarySort = value;
            state.libraryPage = 1;
            renderLibrary();
        });
    }
    if (refs.libraryPageSizeSelect) {
        refs.libraryPageSizeSelect.value = String(state.libraryPageSize);
        refs.libraryPageSizeSelect.addEventListener('change', (event) => {
            const value = parseInt(event.target.value, 10);
            state.libraryPageSize = Number.isNaN(value) ? 30 : value;
            state.libraryPage = 1;
            renderLibrary();
        });
    }
    [refs.libraryPaginationPrev, refs.libraryPaginationBottomPrev].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            if (state.libraryPage > 1) {
                state.libraryPage -= 1;
                renderLibrary();
            }
        });
    });
    [refs.libraryPaginationNext, refs.libraryPaginationBottomNext].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            state.libraryPage += 1;
            renderLibrary();
        });
    });

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
    if (refs.wantedSortSelect) {
        refs.wantedSortSelect.value = state.wantedSort;
        refs.wantedSortSelect.addEventListener('change', (event) => {
            state.wantedSort = event.target.value || 'title-asc';
            renderWanted();
        });
    }

    loadHardcoverSyncSetting();
    if (refs.hardcoverToggle) {
        refs.hardcoverToggle.addEventListener('change', (event) => {
            setHardcoverSyncEnabled(event.target.checked);
        });
    }
    if (refs.hardcoverListSave) {
        refs.hardcoverListSave.addEventListener('click', saveHardcoverListSetting);
    }
    if (refs.hardcoverListInput) {
        refs.hardcoverListInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                saveHardcoverListSetting(event);
            }
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
    refreshHardcoverListStatus();
    startServiceStatusTimer();

    showSection('library');
    loadWantedFromServer();
    if (state.hardcoverSyncEnabled) {
        mirrorHardcoverWanted({ force: false, retries: 8 });
        startHardcoverSyncTimer(false);
    }
    startCalibreRefreshTimer();

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
