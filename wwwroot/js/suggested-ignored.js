(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('suggested-ignored-status');
    const resultsEl = document.getElementById('suggested-ignored-results');
    if (!statusEl || !resultsEl) return;
    resultsEl.classList.add('results-grid');

    let loading = false;
    let loaded = false;
    let ignoredItems = [];

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

    function render() {
        resultsEl.innerHTML = '';
        resultsEl.classList.remove('results-grid');
        resultsEl.classList.add('suggested-groups');
        if (!ignoredItems.length) {
            statusEl.textContent = loaded
                ? 'No ignored suggestions.'
                : 'Loading ignored suggestions…';
            return;
        }

        statusEl.textContent = `Showing ${ignoredItems.length} ignored suggestion(s).`;
        const group = document.createElement('div');
        group.className = 'suggested-group';
        const grid = document.createElement('div');
        grid.className = 'results-grid';
        ignoredItems.forEach(item => {
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
})();
