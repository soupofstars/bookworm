(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('suggested-ignored-status');
    const resultsEl = document.getElementById('suggested-ignored-results');
    if (!statusEl || !resultsEl) return;

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
        if (!ignoredItems.length) {
            statusEl.textContent = loaded
                ? 'No ignored suggestions.'
                : 'Loading ignored suggestions…';
            return;
        }

        statusEl.textContent = `Showing ${ignoredItems.length} ignored suggestion(s).`;
        ignoredItems.forEach(item => {
            const book = item?.book || item?.Book;
            if (!book || typeof app.createBookCard !== 'function') return;
            const card = app.createBookCard(book, {
                useWantedLayout: true,
                showWanted: true,
                onAddToWanted: () => handleAddToWanted(item)
            });
            resultsEl.appendChild(card);
        });
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
