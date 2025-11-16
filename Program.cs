using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// --- Services -------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// HttpClient for Hardcover
builder.Services.AddHttpClient("hardcover", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var endpoint = config["Hardcover:Endpoint"]
                   ?? "https://api.hardcover.app/v1/graphql";
    client.BaseAddress = new Uri(endpoint);

    // API key can come from appsettings or env var Hardcover__ApiKey
    var apiKey = config["Hardcover:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        // Docs say: Authorization: YOUR_API_KEY (no Bearer)
        client.DefaultRequestHeaders.Add("Authorization", apiKey);
    }
});

// --- App ------------------------------------------------------------------

var app = builder.Build();

// Swagger in dev
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Exact-title Hardcover search (works for e.g. "The Hobbit")
app.MapGet("/hardcover/search", async (string title, IHttpClientFactory factory) =>
{
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "title is required" });

    var client = factory.CreateClient("hardcover");

    var graphqlRequest = new
    {
        query = @"
            query BooksByTitle($title: String!) {
              books(
                where: { title: { _eq: $title } }
                limit: 10
                order_by: { users_count: desc }
              ) {
                id
                title
                slug
                rating
                ratings_count
                users_count
                cached_contributors
              }
            }",
        variables = new { title }
    };

    var response = await client.PostAsJsonAsync("", graphqlRequest);

    if (!response.IsSuccessStatusCode)
    {
        var errorText = await response.Content.ReadAsStringAsync();
        return Results.Problem($"Hardcover API error: {response.StatusCode} - {errorText}");
    }

    var resultJson = await response.Content.ReadFromJsonAsync<object>();
    return Results.Ok(resultJson);
})
.WithName("SearchBooks");

// Hardcover "Want to read" list (status_id = 1)
app.MapGet("/hardcover/want-to-read", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("hardcover");

    var graphqlRequest = new
    {
        query = @"
            query MeWantToRead {
              me {
                user_books(
                  where: { status_id: { _eq: 1 } }
                  order_by: { date_added: desc }
                ) {
                  status_id
                  book {
                    id
                    title
                    slug
                    rating
                    ratings_count
                    users_count
                    cached_contributors
                  }
                }
              }
            }"
    };

    var response = await client.PostAsJsonAsync("", graphqlRequest);

    if (!response.IsSuccessStatusCode)
    {
        var errorText = await response.Content.ReadAsStringAsync();
        return Results.Problem($"Hardcover API error: {response.StatusCode} - {errorText}");
    }

    var resultJson = await response.Content.ReadFromJsonAsync<object>();
    return Results.Ok(resultJson);
})
.WithName("GetHardcoverWantToRead");

// --- Readarr-style UI -----------------------------------------------------

app.MapGet("/", () =>
{
    const string html = """
    <!doctype html>
    <html lang="en">
    <head>
        <meta charset="utf-8" />
        <title>Bookworm</title>
        <style>
            :root {
                --bg: #141820;
                --bg-elevated: #1c222d;
                --accent: #3b82f6;
                --accent-soft: rgba(59,130,246,0.15);
                --text: #f9fafb;
                --text-muted: #9ca3af;
                --border: #2d3748;
            }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                font-family: -apple-system, BlinkMacSystemFont, system-ui, sans-serif;
                background: var(--bg);
                color: var(--text);
                display: flex;
                height: 100vh;
            }
            .sidebar {
                width: 220px;
                background: #0f131a;
                border-right: 1px solid var(--border);
                padding: 1.25rem;
                display: flex;
                flex-direction: column;
            }
            .sidebar-title {
                font-weight: 600;
                font-size: 1.3rem;
                margin-bottom: 1.5rem;
                display: flex;
                align-items: center;
                gap: 0.4rem;
            }
            .nav-section-title {
                font-size: 0.75rem;
                text-transform: uppercase;
                letter-spacing: 0.08em;
                color: var(--text-muted);
                margin: 0.75rem 0 0.4rem;
            }
            .nav-link {
                padding: 0.4rem 0.6rem;
                border-radius: 6px;
                font-size: 0.9rem;
                color: var(--text-muted);
                cursor: pointer;
                display: flex;
                align-items: center;
                gap: 0.35rem;
            }
            .nav-link.active,
            .nav-link:hover {
                background: #111827;
                color: var(--text);
            }
            .nav-dot {
                width: 6px;
                height: 6px;
                border-radius: 999px;
                background: var(--accent-soft);
            }
            .nav-link.active .nav-dot {
                background: var(--accent);
            }

            .main {
                flex: 1;
                display: flex;
                flex-direction: column;
            }
            .topbar {
                padding: 0.9rem 1.5rem;
                border-bottom: 1px solid var(--border);
                display: flex;
                align-items: center;
                justify-content: space-between;
                background: #111827;
            }
            .topbar-title {
                font-size: 0.95rem;
                font-weight: 500;
            }
            .topbar-right {
                font-size: 0.8rem;
                color: var(--text-muted);
            }

            .content {
                padding: 1.4rem 1.6rem;
                overflow-y: auto;
            }
            .content-header {
                display: flex;
                justify-content: space-between;
                align-items: flex-end;
                gap: 1rem;
                margin-bottom: 1rem;
            }
            .content-header h1 {
                margin: 0;
                font-size: 1.4rem;
            }
            .content-header p {
                margin: 0.2rem 0 0;
                color: var(--text-muted);
                font-size: 0.9rem;
            }

            .search-bar {
                display: flex;
                gap: 0.5rem;
                margin-bottom: 1rem;
            }
            .search-bar input[type="text"] {
                flex: 1;
                padding: 0.5rem 0.6rem;
                border-radius: 6px;
                border: 1px solid var(--border);
                background: #0b1120;
                color: var(--text);
                font-size: 0.9rem;
            }
            .search-bar button {
                padding: 0.5rem 0.9rem;
                border-radius: 6px;
                border: none;
                background: var(--accent);
                color: white;
                font-size: 0.9rem;
                cursor: pointer;
            }
            .search-bar button:hover {
                background: #2563eb;
            }

            .status {
                font-size: 0.85rem;
                color: var(--text-muted);
                margin-bottom: 0.75rem;
            }

            .results-grid {
                display: grid;
                grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
                gap: 0.9rem;
            }
            .book-card {
                background: var(--bg-elevated);
                border-radius: 10px;
                padding: 0.8rem;
                border: 1px solid var(--border);
                display: flex;
                gap: 0.75rem;
            }
            .book-cover {
                width: 70px;
                height: 100px;
                border-radius: 6px;
                object-fit: cover;
                background: #111827;
                flex-shrink: 0;
            }
            .book-title {
                font-size: 0.95rem;
                font-weight: 600;
                margin-bottom: 0.2rem;
            }
            .book-author {
                font-size: 0.8rem;
                color: var(--text-muted);
                margin-bottom: 0.3rem;
            }
            .book-meta {
                font-size: 0.8rem;
                color: var(--text-muted);
                margin-bottom: 0.4rem;
            }
            .pill {
                display: inline-block;
                padding: 0.15rem 0.45rem;
                border-radius: 999px;
                background: var(--accent-soft);
                color: var(--accent);
                font-size: 0.75rem;
                margin-right: 0.25rem;
            }
            .btn {
                display: inline-block;
                padding: 0.25rem 0.6rem;
                border-radius: 999px;
                border: none;
                font-size: 0.75rem;
                cursor: pointer;
            }
            .btn-wanted {
                background: var(--accent-soft);
                color: var(--accent);
            }
            .btn-primary {
                background: var(--accent);
                color: white;
            }
            .btn-ghost {
                background: transparent;
                color: var(--text-muted);
            }

            .hidden { display: none; }
        </style>
    </head>
    <body>
        <aside class="sidebar">
            <div class="sidebar-title">📚 Bookworm</div>

            <div class="nav-section">
                <div class="nav-section-title">Discover</div>
                <div class="nav-link active" data-section="discover">
                    <span class="nav-dot"></span> Discover
                </div>
            </div>

            <div class="nav-section">
                <div class="nav-section-title">Library</div>
                <div class="nav-link" data-section="library">
                    <span class="nav-dot"></span> Library
                </div>
                <div class="nav-link" data-section="wanted">
                    <span class="nav-dot"></span> Wanted
                </div>
            </div>

            <div class="nav-section">
                <div class="nav-section-title">Hardcover.app</div>
                <div class="nav-link" data-section="hardcover-wanted">
                    <span class="nav-dot"></span> Want to read
                </div>
            </div>

            <div class="nav-section">
                <div class="nav-section-title">System</div>
                <div class="nav-link"><span class="nav-dot"></span> Settings</div>
            </div>
        </aside>

        <main class="main">
            <header class="topbar">
                <div id="topbar-title" class="topbar-title">Discover</div>
                <div class="topbar-right">Backend: Bookworm.Api · Source: Hardcover</div>
            </header>

            <section class="content">
                <!-- Discover -->
                <div id="section-discover">
                    <div class="content-header">
                        <div>
                            <h1>Discover books</h1>
                            <p>Search by exact title (e.g. “The Hobbit”). Use Wanted for books you don’t own yet.</p>
                        </div>
                    </div>

                    <form id="search-form" class="search-bar">
                        <input id="query" type="text" placeholder="e.g. The Hobbit" />
                        <button type="submit">Search</button>
                    </form>

                    <div id="status" class="status">Ready.</div>
                    <div id="results" class="results-grid"></div>
                </div>

                <!-- Library -->
                <div id="section-library" class="hidden">
                    <div class="content-header">
                        <div>
                            <h1>Your library</h1>
                            <p>Books you already own / have in your collection.</p>
                        </div>
                    </div>

                    <div id="library-status" class="status">No books in your library yet.</div>
                    <div id="library-results" class="results-grid"></div>
                </div>

                <!-- Local Wanted -->
                <div id="section-wanted" class="hidden">
                    <div class="content-header">
                        <div>
                            <h1>Wanted</h1>
                            <p>Local wanted list – books you’d like to get. Promote them to your library when you own them.</p>
                        </div>
                    </div>

                    <div id="wanted-status" class="status">No wanted books yet.</div>
                    <div id="wanted-results" class="results-grid"></div>
                </div>

                <!-- Hardcover: Want to read -->
                <div id="section-hardcover-wanted" class="hidden">
                    <div class="content-header">
                        <div>
                            <h1>Hardcover.app</h1>
                            <p>Books you’ve marked as “Want to read” on Hardcover.</p>
                        </div>
                    </div>

                    <div class="nav-section-title">Want to read</div>
                    <div id="hardcover-wanted-status" class="status">Loading from Hardcover…</div>
                    <div id="hardcover-wanted-results" class="results-grid"></div>
                </div>
            </section>
        </main>

        <script>
            // Simple in-memory state
            const library = [];
            const wanted = [];

            function bookKey(book) {
                return book.slug || book.id || book.title;
            }

            // NAV LOGIC
            const navLinks = document.querySelectorAll('.nav-link[data-section]');
            const sections = {
                discover: document.getElementById('section-discover'),
                library: document.getElementById('section-library'),
                wanted: document.getElementById('section-wanted'),
                'hardcover-wanted': document.getElementById('section-hardcover-wanted')
            };
            const topbarTitle = document.getElementById('topbar-title');

            let hardcoverWantedLoaded = false;

            navLinks.forEach(link => {
                link.addEventListener('click', () => {
                    const sectionName = link.getAttribute('data-section');

                    navLinks.forEach(l => l.classList.remove('active'));
                    link.classList.add('active');

                    Object.entries(sections).forEach(([name, el]) => {
                        el.classList.toggle('hidden', name !== sectionName);
                    });

                    if (sectionName === 'discover') topbarTitle.textContent = 'Discover';
                    if (sectionName === 'library') topbarTitle.textContent = 'Library';
                    if (sectionName === 'wanted') topbarTitle.textContent = 'Wanted';
                    if (sectionName === 'hardcover-wanted') {
                        topbarTitle.textContent = 'Hardcover.app · Want to read';
                        if (!hardcoverWantedLoaded) {
                            loadHardcoverWanted();
                        }
                    }
                });
            });

            // DISCOVER SEARCH
            const form = document.getElementById('search-form');
            const queryInput = document.getElementById('query');
            const statusEl = document.getElementById('status');
            const resultsEl = document.getElementById('results');

            form.addEventListener('submit', async (e) => {
                e.preventDefault();
                const q = queryInput.value.trim();
                if (!q) return;

                statusEl.textContent = 'Searching…';
                resultsEl.innerHTML = '';

                try {
                    const res = await fetch('/hardcover/search?title=' + encodeURIComponent(q));
                    if (!res.ok) {
                        statusEl.textContent = 'Error: ' + res.status;
                        return;
                    }

                    const data = await res.json();
                    const books = (data && data.data && data.data.books) || [];

                    if (!books.length) {
                        statusEl.textContent = 'No results found.';
                        return;
                    }

                    statusEl.textContent = `Found ${books.length} result(s).`;
                    resultsEl.innerHTML = '';

                    for (const b of books) {
                        const card = createBookCard(b, { showWanted: true });
                        resultsEl.appendChild(card);
                    }
                } catch (err) {
                    console.error(err);
                    statusEl.textContent = 'Error talking to Bookworm API.';
                }
            });

            // RENDER HELPERS
            const libraryStatusEl = document.getElementById('library-status');
            const libraryResultsEl = document.getElementById('library-results');
            const wantedStatusEl = document.getElementById('wanted-status');
            const wantedResultsEl = document.getElementById('wanted-results');
            const hardcoverWantedStatusEl = document.getElementById('hardcover-wanted-status');
            const hardcoverWantedResultsEl = document.getElementById('hardcover-wanted-results');

            function createBookCard(book, options) {
                const opts = Object.assign({ showWanted: false, showAddToLibrary: false }, options || {});
                const div = document.createElement('div');
                div.className = 'book-card';

                const title = book.title || 'Untitled';
                const contributors = book.cached_contributors || [];
                const firstContributor = contributors[0] || null;
                const authorName = firstContributor && firstContributor.name || 'Unknown author';
                const coverUrl = firstContributor && firstContributor.image && firstContributor.image.url || null;

                const rating = (book.rating ?? 'N/A');
                const usersCount = (book.users_count ?? 0);

                let actionsHtml = '';

                if (opts.showWanted) {
                    actionsHtml += `<button class="btn btn-wanted btn-wanted-action">Wanted</button>`;
                }
                if (opts.showAddToLibrary) {
                    actionsHtml += `<button class="btn btn-primary btn-addlib-action">Add to library</button>`;
                }

                const hardcoverUrl = book.slug
                    ? `https://hardcover.app/books/${book.slug}`
                    : null;
                const linkHtml = hardcoverUrl
                    ? `<a href="${hardcoverUrl}" target="_blank" class="btn btn-ghost" style="text-decoration:none;margin-left:0.35rem;">View</a>`
                    : '';

                div.innerHTML = `
                    ${coverUrl ? `<img class="book-cover" loading="lazy" src="${coverUrl}" alt="Cover">` : `<div class="book-cover"></div>`}
                    <div>
                        <div class="book-title">${title}</div>
                        <div class="book-author">${authorName}</div>
                        <div class="book-meta">
                            <span class="pill">Users: ${usersCount}</span>
                            <span class="pill">Rating: ${rating}</span>
                        </div>
                        ${actionsHtml} ${linkHtml}
                    </div>
                `;

                const wantedBtn = div.querySelector('.btn-wanted-action');
                if (wantedBtn) {
                    wantedBtn.addEventListener('click', () => {
                        addToWanted(book);
                    });
                }

                const addLibBtn = div.querySelector('.btn-addlib-action');
                if (addLibBtn) {
                    addLibBtn.addEventListener('click', () => {
                        addToLibrary(book);
                    });
                }

                return div;
            }

            function addToWanted(book) {
                const key = bookKey(book);
                if (!wanted.some(b => bookKey(b) === key)) {
                    wanted.push(book);
                    renderWanted();
                    wantedStatusEl.textContent = 'Book added to Wanted.';
                } else {
                    wantedStatusEl.textContent = 'Already in Wanted.';
                }
            }

            function addToLibrary(book) {
                const key = bookKey(book);

                if (!library.some(b => bookKey(b) === key)) {
                    library.push(book);
                    renderLibrary();
                    libraryStatusEl.textContent = 'Book added to your library.';
                } else {
                    libraryStatusEl.textContent = 'Already in your library.';
                }

                // Remove from local wanted when we "own" it
                const idx = wanted.findIndex(b => bookKey(b) === key);
                if (idx !== -1) {
                    wanted.splice(idx, 1);
                    renderWanted();
                }
            }

            function renderLibrary() {
                libraryResultsEl.innerHTML = '';
                if (!library.length) {
                    libraryStatusEl.textContent = 'No books in your library yet.';
                    return;
                }
                libraryStatusEl.textContent = `You have ${library.length} book(s) in your library.`;
                for (const b of library) {
                    const card = createBookCard(b, { showWanted: false, showAddToLibrary: false });
                    libraryResultsEl.appendChild(card);
                }
            }

            function renderWanted() {
                wantedResultsEl.innerHTML = '';
                if (!wanted.length) {
                    wantedStatusEl.textContent = 'No wanted books yet.';
                    return;
                }
                wantedStatusEl.textContent = `You have ${wanted.length} wanted book(s).`;
                for (const b of wanted) {
                    const card = createBookCard(b, { showWanted: false, showAddToLibrary: true });
                    wantedResultsEl.appendChild(card);
                }
            }

            async function loadHardcoverWanted() {
                hardcoverWantedStatusEl.textContent = 'Loading from Hardcover…';
                hardcoverWantedResultsEl.innerHTML = '';

                try {
                    const res = await fetch('/hardcover/want-to-read');
                    if (!res.ok) {
                        hardcoverWantedStatusEl.textContent = 'Error loading from Hardcover: ' + res.status;
                        return;
                    }

                    const data = await res.json();
                    const meArr = data && data.data && data.data.me;
                    const firstUser = Array.isArray(meArr) && meArr.length ? meArr[0] : null;
                    const userBooks = firstUser && firstUser.user_books ? firstUser.user_books : [];

                    if (!userBooks.length) {
                        hardcoverWantedStatusEl.textContent = 'No “want to read” books on Hardcover yet.';
                        hardcoverWantedLoaded = true;
                        return;
                    }

                    hardcoverWantedStatusEl.textContent = `You have ${userBooks.length} “want to read” book(s) on Hardcover.`;
                    hardcoverWantedResultsEl.innerHTML = '';

                    for (const ub of userBooks) {
                        const book = ub.book;
                        if (!book) continue;
                        const card = createBookCard(book, { showWanted: false, showAddToLibrary: true });
                        hardcoverWantedResultsEl.appendChild(card);
                    }

                    hardcoverWantedLoaded = true;
                } catch (err) {
                    console.error(err);
                    hardcoverWantedStatusEl.textContent = 'Error talking to Bookworm API.';
                }
            }
        </script>
    </body>
    </html>
    """;

    return Results.Content(html, "text/html");
});

app.Run();
