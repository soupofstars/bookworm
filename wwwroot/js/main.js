(function () {
    const state = {
        library: [],
        wanted: []
    };

    const sections = {
        discover: document.getElementById('section-discover'),
        library: document.getElementById('section-library'),
        wanted: document.getElementById('section-wanted'),
        'hardcover-wanted': document.getElementById('section-hardcover-wanted')
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
        wantedResults: document.getElementById('wanted-results')
    };

    function bookKey(book) {
        return book.slug || book.id || book.title;
    }

    function buildIsbnPills(book) {
        const isbn = book.isbn13 || book.isbn10;
        if (!isbn) return '';
        return `<div class="isbn-pill-row"><span class="pill pill-isbn">ISBN: ${isbn}</span></div>`;
    }

    function createBookCard(book, options) {
        const opts = Object.assign({ showWanted: false, showAddToLibrary: false }, options || {});
        const div = document.createElement('div');
        div.className = 'book-card';

        const title = book.title || 'Untitled';
        let authorText = 'Unknown author';
        if (Array.isArray(book.author_names) && book.author_names.length) {
            authorText = book.author_names.join(', ');
        } else if (Array.isArray(book.authors) && book.authors.length) {
            authorText = book.authors.join(', ');
        } else if (book.author) {
            authorText = book.author;
        } else if (Array.isArray(book.cached_contributors) && book.cached_contributors.length) {
            authorText = book.cached_contributors[0].name || authorText;
        }

        let coverUrl =
            (book.image && book.image.url) ||
            book.coverUrl ||
            (book.cover_i ? `https://covers.openlibrary.org/b/id/${book.cover_i}-M.jpg` : null);
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
        const editions = book.edition_count ?? book.users_count ?? book.ratings_count ?? 0;

        let detailsUrl = null;
        if (book.source === 'openlibrary') {
            let editionKey = null;
            const prefer = value => Array.isArray(value) ? value[0] : value;
            editionKey = prefer(book.cover_edition_key);
            if (!editionKey) editionKey = prefer(book.edition_key);
            if (editionKey) {
                detailsUrl = `https://openlibrary.org/books/${editionKey}`;
            } else if (book.slug) {
                detailsUrl = `https://openlibrary.org${book.slug}`;
            }
        } else if (book.slug || book.id) {
            detailsUrl = `https://hardcover.app/books/${book.slug || book.id}`;
        }
        const viewLink = detailsUrl
            ? `<a href="${detailsUrl}" target="_blank" class="btn btn-view">View</a>`
            : '';

        let actionMarkup = '';
        if (opts.showWanted) {
            actionMarkup += `<button class="btn btn-wanted btn-wanted-action">Wanted</button>`;
        }
        if (opts.showAddToLibrary) {
            actionMarkup += `<button class="btn btn-primary btn-addlib-action">Add to library</button>`;
        }

        div.innerHTML = `
            ${coverUrl ? `<img class="book-cover" loading="lazy" src="${coverUrl}" alt="Cover">` : `<div class="book-cover"></div>`}
            <div>
                <div class="book-title">${title}</div>
                <div class="book-author">${authorText}</div>
                <div class="book-meta">
                    <span class="pill pill-metric pill-editions">Editions: ${editions}</span>
                    <span class="pill pill-metric pill-rating">Rating: ${rating}</span>
                </div>
                ${buildIsbnPills(book)}
                ${actionMarkup ? `<div class="book-actions">${actionMarkup}</div>` : ''}
                ${viewLink ? `<div class="book-actions">${viewLink}</div>` : ''}
            </div>
        `;

        const wantedBtn = div.querySelector('.btn-wanted-action');
        if (wantedBtn) {
            wantedBtn.addEventListener('click', () => addToWanted(book));
        }

        const addLibBtn = div.querySelector('.btn-addlib-action');
        if (addLibBtn) {
            addLibBtn.addEventListener('click', () => addToLibrary(book));
        }

        return div;
    }

    function addToWanted(book) {
        const key = bookKey(book);
        if (!state.wanted.some(b => bookKey(b) === key)) {
            state.wanted.push(book);
            renderWanted();
            refs.wantedStatus.textContent = 'Book added to Wanted.';
        } else {
            refs.wantedStatus.textContent = 'Already in Wanted.';
        }
    }

    function addToLibrary(book) {
        const key = bookKey(book);
        if (!state.library.some(b => bookKey(b) === key)) {
            state.library.push(book);
            renderLibrary();
            refs.libraryStatus.textContent = 'Book added to your library.';
        } else {
            refs.libraryStatus.textContent = 'Already in your library.';
        }

        const idx = state.wanted.findIndex(b => bookKey(b) === key);
        if (idx !== -1) {
            state.wanted.splice(idx, 1);
            renderWanted();
        }
    }

    function renderLibrary() {
        const { libraryStatus, libraryResults } = refs;
        libraryResults.innerHTML = '';
        if (!state.library.length) {
            libraryStatus.textContent = 'No books in your library yet.';
            return;
        }

        libraryStatus.textContent = `You have ${state.library.length} book(s) in your library.`;
        state.library.forEach(book => {
            libraryResults.appendChild(createBookCard(book, { showWanted: false, showAddToLibrary: false }));
        });
    }

    function renderWanted() {
        const { wantedStatus, wantedResults } = refs;
        wantedResults.innerHTML = '';
        if (!state.wanted.length) {
            wantedStatus.textContent = 'No wanted books yet.';
            return;
        }

        wantedStatus.textContent = `You have ${state.wanted.length} wanted book(s).`;
        state.wanted.forEach(book => {
            wantedResults.appendChild(createBookCard(book, { showWanted: false, showAddToLibrary: true }));
        });
    }

    function showSection(sectionName) {
        navLinks.forEach(link => link.classList.remove('active'));
        if (navMap[sectionName]) {
            navMap[sectionName].classList.add('active');
        }

        Object.entries(sections).forEach(([name, el]) => {
            el.classList.toggle('hidden', name !== sectionName);
        });

        if (sectionName === 'discover') {
            refs.topbarTitle.textContent = 'Search';
            window.bookwormSearch && window.bookwormSearch.restore();
        } else if (sectionName === 'library') {
            refs.topbarTitle.textContent = 'Library';
        } else if (sectionName === 'wanted') {
            refs.topbarTitle.textContent = 'Wanted';
        } else if (sectionName === 'hardcover-wanted') {
            refs.topbarTitle.textContent = 'Hardcover.app · Want to read';
            window.bookwormHardcover && window.bookwormHardcover.ensureLoaded();
        }
    }

    navLinks.forEach(link => {
        link.addEventListener('click', () => {
            const sectionName = link.getAttribute('data-section');
            showSection(sectionName);
        });
    });

    showSection('library');

    window.bookwormApp = {
        state,
        createBookCard,
        addToLibrary,
        addToWanted,
        renderLibrary,
        renderWanted
    };
})();
