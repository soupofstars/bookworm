(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('suggested-status');
    const stepsEl = document.getElementById('suggested-steps');
    const resultsEl = document.getElementById('suggested-results');
    const reloadBtn = document.getElementById('btn-suggested-reload');
    const searchInput = document.getElementById('suggested-search-input');
    const searchClearBtn = document.getElementById('suggested-search-clear');
    if (!statusEl || !resultsEl) return;
    if (resultsEl.classList.contains('results-grid')) {
        resultsEl.classList.remove('results-grid');
        resultsEl.classList.add('suggested-groups');
    }

    let loaded = false;
    let loading = false;
    let liveSteps = [];
    let source = null;
    let totalCalibreBooks = 0;
    let processedCount = 0;
    let lastBuckets = null;
    let allNormalized = [];
    const bucketPages = { high: 0, mid: 0, low: 0 };
    const BUCKET_META = [
        { key: 'high', label: 'Best match', shortLabel: 'Best match' },
        { key: 'mid', label: 'Related', shortLabel: 'Related' },
        { key: 'low', label: 'Weak match', shortLabel: 'Weak match' }
    ];
    const BUCKET_META_BY_KEY = BUCKET_META.reduce((acc, entry) => {
        acc[entry.key] = entry;
        return acc;
    }, {});

    const STREAM_TAKE = 0; // 0 = all Calibre books
    const STREAM_DELAY_MS = 2000; // throttle to ~30 requests per minute
    const PAGE_SIZE = 5;
    const signed = (n) => (n > 0 ? `+${n}` : `${n}`);

    const normalizeHardcoverBook = (() => {
        const api = window.bookwormHardcover;
        if (api && typeof api.normalizeBook === 'function') {
            return api.normalizeBook;
        }
        return book => Object.assign({ source: 'hardcover' }, book || {});
    })();

    function matchScoreBucket(score) {
        if (typeof score !== 'number') return null;
        if (score >= 8) return 'high';
        if (score >= 6) return 'mid';
        if (score >= 2 && score <= 5) return 'low';
        return null;
    }

    function describeMatchScore(score) {
        const bucket = matchScoreBucket(score);
        return bucket ? (BUCKET_META_BY_KEY[bucket]?.shortLabel || null) : null;
    }

    function updateProgress(processed, total) {
        if (!statusEl) return;
        if (!total || total <= 0) {
            statusEl.textContent = `Scanning Calibre books on Hardcover… (${processed} processed)`;
            return;
        }
        const remaining = Math.max(total - processed, 0);
        const etaMinutes = Math.ceil((remaining * STREAM_DELAY_MS) / 60000);
        statusEl.textContent = `Scanning Calibre books on Hardcover… (${processed}/${total}) · approx ${etaMinutes} min remaining`;
    }

    function renderSteps(steps) {
        if (!stepsEl) return;
        stepsEl.classList.add('hidden');
        stepsEl.textContent = '';
    }

    async function readError(res) {
        try {
            const text = await res.text();
            if (!text) return null;
            try {
                const json = JSON.parse(text);
                if (json && typeof json === 'object') {
                    if (json.error) return json.error;
                    if (json.title) return json.title;
                }
            } catch {
                // not JSON, fall through
            }
            return text;
        } catch {
            return null;
        }
    }

    async function loadRecommendations(forceReload = false) {
        if (loading) return;
        if (loaded && !forceReload) return;
        loading = true;
        if (reloadBtn) reloadBtn.disabled = true;
        statusEl.textContent = 'Finding people-list picks on Hardcover…';
        liveSteps = [];
        totalCalibreBooks = 0;
        processedCount = 0;
        renderSteps([]);
        resultsEl.innerHTML = '';

        const streamUrl = `/hardcover/calibre-recommendations/stream?take=${STREAM_TAKE}&lists=20&perList=24&delayMs=${STREAM_DELAY_MS}`;
        const useStream = typeof EventSource !== 'undefined';

        const handleSummary = (data) => {
            const inspected = typeof data?.inspectedCalibreBooks === 'number' ? data.inspectedCalibreBooks : null;
            const matched = typeof data?.matchedCalibreBooks === 'number' ? data.matchedCalibreBooks : null;
            if (typeof data?.totalCalibreBooks === 'number') {
                totalCalibreBooks = data.totalCalibreBooks;
            }
            if (typeof inspected === 'number') {
                processedCount = inspected;
            }
            const steps = Array.isArray(data?.steps) ? data.steps : liveSteps;
            renderSteps(steps);
            const recs = Array.isArray(data?.recommendations) ? data.recommendations : [];
            if (!recs.length) {
                const stats = inspected != null && matched != null
                    ? `Checked ${matched} of ${inspected} Calibre books on Hardcover; no list neighbors found. `
                    : '';
                statusEl.textContent = `${stats}No people-list recommendations yet. Sync Calibre and ensure your Hardcover key is set.`;
                loaded = true;
                loading = false;
                if (reloadBtn) reloadBtn.disabled = false;
                return;
            }

            let rendered = 0;
            rendered = renderRecommendations(recs);

            const prefix = inspected != null && matched != null
                ? `Checked ${matched} of ${inspected} Calibre books. `
                : '';
            statusEl.textContent = rendered === 0
                ? `${prefix}No matching recommendations yet.`
                : `${prefix}People-list picks: ${rendered} recommendation(s).`;
            loaded = true;
            loading = false;
            if (reloadBtn) reloadBtn.disabled = false;
            // reload from saved table to reflect persisted data
            void loadSavedSuggested(true);
        };

        const fallbackFetch = async () => {
            try {
                const res = await fetch(`/hardcover/calibre-recommendations?take=${STREAM_TAKE}&lists=20&perList=24&delayMs=${STREAM_DELAY_MS}`);
                if (!res.ok) {
                    const detail = await readError(res);
                    const statusText = res.statusText || `${res.status}`;
                    statusEl.textContent = detail
                        ? detail
                        : `Unable to load Hardcover people lists (HTTP ${statusText}).`;
                    return;
                }
                const data = await res.json();
                handleSummary(data);
            } catch (err) {
                console.error('Failed to load Hardcover recommendations', err);
                statusEl.textContent = 'Network error loading Hardcover people lists.';
            } finally {
                loading = false;
                if (reloadBtn) reloadBtn.disabled = false;
            }
        };

        if (!useStream) {
            await fallbackFetch();
            return;
        }

        if (source) {
            source.close();
            source = null;
        }

        try {
            source = new EventSource(streamUrl);
            source.addEventListener('open', () => {
                statusEl.textContent = 'Scanning your Calibre books on Hardcover…';
                updateProgress(processedCount, totalCalibreBooks || STREAM_TAKE);
            });
            source.addEventListener('step', (ev) => {
                try {
                    const step = JSON.parse(ev.data);
                    if (typeof step.totalCalibreBooks === 'number') {
                        totalCalibreBooks = step.totalCalibreBooks;
                    }
                    processedCount += 1;
                    liveSteps.push(step);
                    if (liveSteps.length > 40) {
                        liveSteps = liveSteps.slice(liveSteps.length - 40);
                    }
                    renderSteps(liveSteps);
                    updateProgress(processedCount, totalCalibreBooks || STREAM_TAKE);
                } catch (e) {
                    console.warn('Failed to parse step event', e);
                }
            });
            source.addEventListener('summary', (ev) => {
                try {
                    const data = JSON.parse(ev.data);
                    if (typeof data?.totalCalibreBooks === 'number') {
                        totalCalibreBooks = data.totalCalibreBooks;
                    }
                    if (typeof data?.inspectedCalibreBooks === 'number') {
                        processedCount = data.inspectedCalibreBooks;
                    }
                    handleSummary(data);
                } catch (e) {
                    console.warn('Failed to parse summary event', e);
                } finally {
                    if (source) {
                        source.close();
                        source = null;
                    }
                }
            });
            source.addEventListener('error', async (ev) => {
                console.error('Stream error from server', ev);
                if (source) {
                    source.close();
                    source = null;
                }
                statusEl.textContent = 'Stream error loading Hardcover people lists. Retrying via fallback…';
                await fallbackFetch();
            });
        } catch (err) {
            console.error('EventSource failed', err);
            if (source) {
                source.close();
                source = null;
            }
            await fallbackFetch();
        }
    }

    function normalizeRec(rec) {
        if (!rec) return null;
        const matchScore = typeof rec.matchScore === 'number'
            ? rec.matchScore
            : (typeof rec.MatchScore === 'number' ? rec.MatchScore : null);
        if (matchScore === null || matchScore === 1) return null; // hide score 1
        const matchBucket = matchScoreBucket(matchScore);
        const matchLabel = describeMatchScore(matchScore);
        const suggestedId = rec.suggestedId
            ?? rec.SuggestedId
            ?? rec.id
            ?? rec.Id
            ?? null;
        const debug = rec.debug || rec.Debug;
        const titleBonusWords = Array.isArray(rec.titleBonusWords)
            ? rec.titleBonusWords
            : Array.isArray(rec.TitleBonusWords)
                ? rec.TitleBonusWords
                : [];
        const book = rec.book || rec.Book || {};
        const parts = [];
        if (book.title) parts.push(book.title);
        if (Array.isArray(book.cached_contributors)) {
            book.cached_contributors.forEach(c => {
                if (c && typeof c.name === 'string') parts.push(c.name);
            });
        }
        if (Array.isArray(book.contributions)) {
            book.contributions.forEach(c => {
                if (c && c.author && typeof c.author.name === 'string') parts.push(c.author.name);
            });
        }
        const baseGenres = Array.isArray(rec.base_genres)
            ? rec.base_genres
            : Array.isArray(rec.baseGenres)
                ? rec.baseGenres
                : [];
        if (baseGenres.length) parts.push(baseGenres.join(' '));
        const searchKey = parts.join(' ').toLowerCase();
        return { ...rec, matchScore, suggestedId, matchBucket, matchLabel, debug, titleBonusWords, searchKey };
    }

    function bucketize(recs) {
        const grouped = { high: [], mid: [], low: [] };
        recs.forEach(rec => {
            const bucket = rec.matchBucket || matchScoreBucket(rec.matchScore);
            if (!bucket || !grouped[bucket]) return;
            grouped[bucket].push(rec);
        });
        return grouped;
    }

    function applySearchAndRender() {
        const query = (searchInput?.value || '').trim().toLowerCase();
        const filtered = query
            ? allNormalized.filter(r => (r.searchKey || '').includes(query))
            : allNormalized;

        lastBuckets = bucketize(filtered);
        return renderBuckets();
    }

    function handleSearchChange() {
        const rendered = applySearchAndRender();
        if (!loaded) return;
        const rawQuery = (searchInput?.value || '').trim();
        if (rawQuery.length > 0) {
            statusEl.textContent = rendered === 0
                ? `No matches for “${rawQuery}”.`
                : `Filtered to ${rendered} suggestion(s).`;
        } else {
            statusEl.textContent = rendered === 0
                ? 'No suggestions yet.'
                : `Showing ${rendered} suggestion(s).`;
        }
    }

    function changePage(bucketKey, nextPage, pageCount) {
        const next = Math.max(0, Math.min(nextPage, pageCount - 1));
        bucketPages[bucketKey] = next;
        renderBuckets();
    }

    function createCard(rec) {
        const rawBook = rec.book || rec.Book;
        if (!rawBook) return null;
        const normalizedBook = normalizeHardcoverBook(rawBook);
        const card = app.createBookCard(normalizedBook, {
            showWanted: true,
            showAddToLibrary: false,
            useWantedLayout: true,
            matchScore: rec.matchScore,
            matchScoreLabel: null,
            onAddToWanted: () => handleSuggestedAddToWanted(rec)
        });

        const reasons = Array.isArray(rec.reasons)
            ? rec.reasons
            : Array.isArray(rec.Reasons)
                ? rec.Reasons
                : [];
        // Omit list source details on the card.

        const baseGenres = Array.isArray(rec.base_genres)
            ? rec.base_genres
            : Array.isArray(rec.baseGenres)
                ? rec.baseGenres
                : [];
        // Genres and title bonus are intentionally omitted from cards.

        if (rec.alreadyInCalibre) {
            const ownedEl = document.createElement('div');
            ownedEl.className = 'status status-success';
            ownedEl.textContent = 'Already in Calibre library';
            card.appendChild(ownedEl);
        }

        return card;
    }

    function removeSuggestedLocally(suggestedId) {
        if (!suggestedId) return;
        const before = allNormalized.length;
        allNormalized = allNormalized.filter(r => (r.suggestedId || r.id || r.Id) !== suggestedId);
        if (before !== allNormalized.length) {
            handleSearchChange();
        }
    }

    async function hideSuggestedRemote(suggestedId) {
        if (!suggestedId) return;
        try {
            await fetch('/suggested/hide', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ids: [suggestedId] })
            });
        } catch (err) {
            console.warn('Failed to hide suggested entry', err);
        }
    }

    function handleSuggestedAddToWanted(rec) {
        const book = rec.book || rec.Book;
        if (!book) return;
        const suggestedId = rec.suggestedId || rec.id || rec.Id;
        app.addToWanted(book, {
            onSaved: () => {
                if (suggestedId) {
                    removeSuggestedLocally(suggestedId);
                    void hideSuggestedRemote(suggestedId);
                }
            },
            onAlready: () => {
                if (suggestedId) {
                    removeSuggestedLocally(suggestedId);
                    void hideSuggestedRemote(suggestedId);
                }
            }
        });
    }

    function renderBuckets() {
        resultsEl.innerHTML = '';
        if (!lastBuckets) return 0;

        let total = 0;
        BUCKET_META.forEach(bucket => {
            const items = lastBuckets[bucket.key] || [];
            if (!items.length) return;
            total += items.length;

            const pageCount = Math.max(1, Math.ceil(items.length / PAGE_SIZE));
            const currentPage = Math.min(bucketPages[bucket.key] || 0, pageCount - 1);
            bucketPages[bucket.key] = currentPage;

            const start = currentPage * PAGE_SIZE;
            const pageItems = items.slice(start, start + PAGE_SIZE);

            const groupEl = document.createElement('div');
            groupEl.className = 'suggested-group';

            const header = document.createElement('div');
            header.className = 'suggested-group-header';

            const titleWrap = document.createElement('div');
            const titleEl = document.createElement('div');
            titleEl.className = 'suggested-group-title';
            titleEl.textContent = bucket.label;
            const countEl = document.createElement('div');
            countEl.className = 'suggested-group-count';
            countEl.textContent = `${items.length} item(s)`;
            titleWrap.appendChild(titleEl);
            titleWrap.appendChild(countEl);
            header.appendChild(titleWrap);

            if (pageCount > 1) {
                const pager = document.createElement('div');
                pager.className = 'suggested-pager';
                const prev = document.createElement('button');
                prev.className = 'btn btn-ghost';
                prev.textContent = 'Prev';
                prev.disabled = currentPage === 0;
                prev.addEventListener('click', () => changePage(bucket.key, currentPage - 1, pageCount));
                const info = document.createElement('span');
                info.className = 'suggested-pager-info';
                info.textContent = `Page ${currentPage + 1} / ${pageCount}`;
                const next = document.createElement('button');
                next.className = 'btn btn-ghost';
                next.textContent = 'Next';
                next.disabled = currentPage >= pageCount - 1;
                next.addEventListener('click', () => changePage(bucket.key, currentPage + 1, pageCount));
                pager.appendChild(prev);
                pager.appendChild(info);
                pager.appendChild(next);
                header.appendChild(pager);
            }

            groupEl.appendChild(header);

            const grid = document.createElement('div');
            grid.className = 'results-grid';
            pageItems.forEach(rec => {
                const card = createCard(rec);
                if (card) grid.appendChild(card);
            });

            groupEl.appendChild(grid);
            resultsEl.appendChild(groupEl);
        });

        return total;
    }

    function renderRecommendations(recs) {
        const normalized = (recs || []).map(normalizeRec).filter(Boolean);
        bucketPages.high = 0;
        bucketPages.mid = 0;
        bucketPages.low = 0;
        allNormalized = normalized;
        return applySearchAndRender();
    }

    async function loadSavedSuggested(isPostScan = false) {
        try {
            const res = await fetch('/suggested/ranked');
            if (!res.ok) {
                if (!isPostScan) statusEl.textContent = 'Unable to load saved suggestions.';
                return false;
            }
            const data = await res.json();
            const items = Array.isArray(data?.items) ? data.items : [];
            if (!items.length) {
                if (!isPostScan) statusEl.textContent = 'No saved suggestions yet.';
                return false;
            }

            const rendered = renderRecommendations(items.map(item => ({
                id: item.id || item.Id || null,
                suggestedId: item.id || item.Id || null,
                book: item.book,
                occurrences: (item.reasons && item.reasons.length) ? item.reasons.length : 1,
                reasons: item.reasons || [],
                base_genres: item.baseGenres || [],
                matchScore: item.matchScore,
                debug: item.debug || item.Debug,
                titleBonusWords: item.titleBonusWords || item.TitleBonusWords || []
            })));

            statusEl.textContent = rendered === 0
                ? 'No suggestions yet.'
                : `Showing ${rendered} saved suggestion(s).`;
            loaded = true;
            loading = false;
            if (reloadBtn) reloadBtn.disabled = false;
            return true;
        } catch (err) {
            console.error('Failed to load saved suggestions', err);
            if (!isPostScan) statusEl.textContent = 'Unable to load saved suggestions.';
            return false;
        }
    }

    async function shouldScanRecommendations() {
        try {
            const res = await fetch('/hardcover/list-cache/status');
            if (!res.ok) return true;
            const data = await res.json();
            const total = typeof data?.total === 'number' ? data.total : 0;
            const withLists = typeof data?.withLists === 'number' ? data.withLists : 0;
            if (total === 0 || withLists === 0) {
                return true;
            }
            return false;
        } catch {
            return true;
        }
    }

    if (searchInput) {
        searchInput.addEventListener('input', handleSearchChange);
    }
    if (searchClearBtn && searchInput) {
        searchClearBtn.addEventListener('click', () => {
            searchInput.value = '';
            handleSearchChange();
            searchInput.focus();
        });
    }

    async function ensureSuggestedLoaded(forceReload = false) {
        if (loading) return;
        if (loaded && !forceReload) return;

        // First, try to show saved suggestions.
        const hasSaved = await loadSavedSuggested(false);
        if (hasSaved && !forceReload) {
            return;
        }

        // Decide whether to scan.
        const needsScan = await shouldScanRecommendations();
        if (!needsScan) {
            statusEl.textContent = 'Saved suggestions unavailable and cache already populated. Not reloading.';
            if (reloadBtn) reloadBtn.disabled = false;
            return;
        }

        // Proceed with scan.
        await loadRecommendations(forceReload);
    }

    if (reloadBtn) {
        reloadBtn.addEventListener('click', () => ensureSuggestedLoaded(true));
    }

    window.bookwormSuggested = {
        ensureLoaded() {
            ensureSuggestedLoaded(false);
        },
        reload() {
            ensureSuggestedLoaded(true);
        }
    };
})();
