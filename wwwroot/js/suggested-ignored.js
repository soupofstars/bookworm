(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('suggested-ignored-status');
    const resultsEl = document.getElementById('suggested-ignored-results');
    const pageSizeSelect = document.getElementById('suggested-ignored-page-size');
    const pagination = document.getElementById('suggested-ignored-pagination');
    const paginationInfo = document.getElementById('suggested-ignored-pagination-info');
    const paginationPrev = document.getElementById('suggested-ignored-pagination-prev');
    const paginationNext = document.getElementById('suggested-ignored-pagination-next');
    const paginationBottom = document.getElementById('suggested-ignored-pagination-bottom');
    const paginationBottomInfo = document.getElementById('suggested-ignored-pagination-bottom-info');
    const paginationBottomPrev = document.getElementById('suggested-ignored-pagination-bottom-prev');
    const paginationBottomNext = document.getElementById('suggested-ignored-pagination-bottom-next');
    if (!statusEl || !resultsEl) return;
    resultsEl.classList.add('results-grid');

    let loading = false;
    let loaded = false;
    let ignoredItems = [];
    let page = 1;
    let pageSize = 30;

    function removeById(id) {
        if (!id) return;
        ignoredItems = ignoredItems.filter(item => (item.id || item.Id) !== id);
        render();
    }

    function handleAddToWanted(item) {
        const book = item?.book || item?.Book;
        const id = item?.id || item?.Id;
        if (!book) return;

        app.addToWanted(book, {
            onSaved: () => removeById(id),
            onAlready: () => removeById(id)
        });
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

    function render() {
        resultsEl.innerHTML = '';
        resultsEl.classList.remove('results-grid');
        resultsEl.classList.add('suggested-groups');
        if (!ignoredItems.length) {
            statusEl.textContent = loaded
                ? 'No ignored suggestions.'
                : 'Loading ignored suggestions…';
            updatePaginationUI(0, 1, 0);
            return;
        }

        const effectivePageSize = pageSize === 0 ? ignoredItems.length : pageSize;
        const totalPages = Math.max(1, Math.ceil(ignoredItems.length / effectivePageSize));
        if (page > totalPages) page = totalPages;
        if (page < 1) page = 1;
        const start = pageSize === 0 ? 0 : (page - 1) * effectivePageSize;
        const end = pageSize === 0 ? ignoredItems.length : start + effectivePageSize;
        const pageItems = ignoredItems.slice(start, end);

        statusEl.textContent = `${ignoredItems.length} ignored suggestion(s).`;
        const group = document.createElement('div');
        group.className = 'suggested-group';
        const grid = document.createElement('div');
        grid.className = 'results-grid';
        pageItems.forEach(item => {
            const book = item?.book || item?.Book;
            if (!book || typeof app.createBookCard !== 'function') return;
            const card = app.createBookCard(book, {
                useWantedLayout: true,
                showWanted: true,
                onAddToWanted: () => handleAddToWanted(item)
            });
            grid.appendChild(card);
        });
        group.appendChild(grid);
        resultsEl.appendChild(group);
        updatePaginationUI(ignoredItems.length, totalPages, pageItems.length);
    }

    async function loadIgnored(force = false) {
        if (loading) return;
        if (loaded && !force) return;
        loading = true;
        statusEl.textContent = 'Loading ignored suggestions…';

        try {
            const res = await fetch('/suggested/ignored');
            if (!res.ok) {
                const detail = await res.text().catch(() => '');
                throw new Error(detail || `HTTP ${res.status}`);
            }
            const data = await res.json();
            ignoredItems = Array.isArray(data?.items) ? data.items : [];
            loaded = true;
            page = 1;
            render();
        } catch (err) {
            console.error('Failed to load ignored suggestions', err);
            statusEl.textContent = 'Unable to load ignored suggestions.';
        } finally {
            loading = false;
        }
    }

    window.bookwormSuggestedIgnored = {
        ensureLoaded: () => loadIgnored(true),
        reload: () => loadIgnored(true)
    };

    if (pageSizeSelect) {
        pageSizeSelect.value = String(pageSize);
        pageSizeSelect.addEventListener('change', (event) => {
            const value = parseInt(event.target.value, 10);
            pageSize = Number.isNaN(value) ? 30 : value;
            page = 1;
            render();
        });
    }
    [paginationPrev, paginationBottomPrev].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            if (page > 1) {
                page -= 1;
                render();
            }
        });
    });
    [paginationNext, paginationBottomNext].forEach(btn => {
        if (!btn) return;
        btn.addEventListener('click', () => {
            page += 1;
            render();
        });
    });
})();
