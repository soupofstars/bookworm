(function () {
    const app = window.bookwormApp;
    if (!app) return;

    const statusEl = document.getElementById('hardcover-wanted-status');
    const resultsEl = document.getElementById('hardcover-wanted-results');
    let loaded = false;
    let loading = false;

    async function loadHardcoverWanted() {
        if (loading) return;
        loading = true;
        statusEl.textContent = 'Loading from Hardcover…';
        resultsEl.innerHTML = '';

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
                loading = false;
                return;
            }

            const data = await res.json();
            const meArr = data?.data?.me;
            const firstUser = Array.isArray(meArr) && meArr.length ? meArr[0] : null;
            const userBooks = firstUser?.user_books ?? [];

            if (!userBooks.length) {
                statusEl.textContent = 'No “want to read” books on Hardcover yet.';
                loaded = true;
                loading = false;
                return;
            }

            statusEl.textContent = `You have ${userBooks.length} “want to read” book(s) on Hardcover.`;
            resultsEl.innerHTML = '';

            userBooks.forEach(entry => {
                const book = entry.book;
                if (!book) return;
                resultsEl.appendChild(app.createBookCard(book, { showAddToLibrary: true }));
            });

            loaded = true;
        } catch (err) {
            console.error(err);
            statusEl.textContent = 'Error talking to Bookworm API.';
        } finally {
            loading = false;
        }
    }

    window.bookwormHardcover = {
        ensureLoaded() {
            if (!loaded) {
                loadHardcoverWanted();
            }
        }
    };
})();
