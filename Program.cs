using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bookworm.Data;

var builder = WebApplication.CreateBuilder(args);

// --- Services -------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var wantedDb = builder.Configuration["Storage:Database"] ?? "App_Data/bookworm.db";
builder.Services.AddSingleton(new WantedRepository(wantedDb));
builder.Services.AddSingleton(new CalibreMirrorRepository(wantedDb));
builder.Services.AddSingleton<UserSettingsStore>();
builder.Services.AddSingleton<CalibreRepository>();
builder.Services.AddSingleton<CalibreCoverService>();
builder.Services.AddSingleton<CalibreSyncService>();

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

// HttpClient for OpenLibrary (for fuzzy book search)
builder.Services.AddHttpClient("openlibrary", client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BookwormApp/1.0 (+https://github.com/soupofstars/bookworm)");
});

// --- App ------------------------------------------------------------------

var app = builder.Build();

// Swagger in dev
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve static UI assets (index.html, JS, CSS) from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();
var coverService = app.Services.GetRequiredService<CalibreCoverService>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = coverService.AsFileProvider(),
    RequestPath = coverService.RequestPath
});

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Helpers ----------------------------------------------------------

static bool IsHardcoverConfigured(IConfiguration config)
    => !string.IsNullOrWhiteSpace(config["Hardcover:ApiKey"]);

static async Task<IResult> ForwardHardcoverError(HttpResponseMessage response, string context)
{
    var body = await response.Content.ReadAsStringAsync();
    return Results.Problem(
        title: $"Hardcover API error ({context})",
        detail: string.IsNullOrWhiteSpace(body) ? null : body,
        statusCode: (int)response.StatusCode);
}

static int ExtractAffectedRows(JsonDocument? document, string key)
{
    if (document is null)
    {
        return 0;
    }

    if (!document.RootElement.TryGetProperty("data", out var dataElement))
    {
        return 0;
    }

    if (!dataElement.TryGetProperty(key, out var section))
    {
        return 0;
    }

    if (!section.TryGetProperty("affected_rows", out var affected))
    {
        return 0;
    }

    return affected.GetInt32();
}

static string ExpandUserPath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    var expanded = Environment.ExpandEnvironmentVariables(path);
    if (expanded.StartsWith("~"))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            var remainder = expanded.Length > 1
                ? expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/')
                : string.Empty;
            expanded = string.IsNullOrWhiteSpace(remainder)
                ? home
                : Path.Combine(home, remainder);
        }
    }

    return expanded;
}

// Wanted shelf endpoints (persisted locally via SQLite)
app.MapGet("/wanted", async (WantedRepository repo) =>
{
    var items = await repo.GetAllAsync();
    return Results.Ok(new { items });
});

app.MapPost("/wanted", async (WantedRepository repo, WantedBookRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Key))
    {
        return Results.BadRequest(new { error = "key is required" });
    }

    await repo.UpsertAsync(request);
    return Results.Ok(new { status = "ok" });
});

app.MapDelete("/wanted/{key}", async (WantedRepository repo, string key) =>
{
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "key is required" });
    }

    await repo.DeleteAsync(key);
    return Results.NoContent();
});

// Fuzzy book search via OpenLibrary
app.MapGet("/search", async (string query, string? mode, IHttpClientFactory factory) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query is required" });

    var client = factory.CreateClient("openlibrary");
    const string fields = "key,title,author_name,cover_i,edition_count,ratings_average,ratings_count,subject,edition_key,cover_edition_key,isbn,isbn_13,isbn_10";
    var normalizedMode = (mode ?? "title").Trim().ToLowerInvariant();
    var searchParams = normalizedMode switch
    {
        "author" => new[] { "author", "q" },
        "isbn" => new[] { "isbn", "title", "q" },
        _ => new[] { "title", "q" }
    };
    List<OpenLibraryDoc> docs = new();

    foreach (var param in searchParams)
    {
        var response = await client.GetAsync($"/search.json?{param}={Uri.EscapeDataString(query)}&limit=20&fields={fields}");

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            return Results.Problem($"OpenLibrary error ({param}): {response.StatusCode} - {errorText}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenLibrarySearchResponse>()
                      ?? new OpenLibrarySearchResponse(Array.Empty<OpenLibraryDoc>());

        docs = payload.Docs
            .Where(doc => !string.IsNullOrWhiteSpace(doc.Title))
            .Take(20)
            .ToList();

        if (docs.Count > 0 || param == "q")
        {
            break;
        }
    }

    var books = docs
        .Select(doc =>
        {
            var coverUrl = doc.CoverId.HasValue
                ? $"https://covers.openlibrary.org/b/id/{doc.CoverId}-M.jpg"
                : null;

            return new
            {
                id = doc.Key ?? doc.Title ?? Guid.NewGuid().ToString("N"),
                title = doc.Title ?? "Untitled",
                slug = doc.Key, // For OpenLibrary we can link directly via work key
                rating = doc.RatingsAverage,
                ratings_count = doc.RatingsCount,
                users_count = doc.EditionCount,
                author_names = doc.AuthorName ?? Array.Empty<string>(),
                isbn13 = doc.Isbn13?.FirstOrDefault()
                         ?? doc.Isbn?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.Replace("-", "").Length >= 13),
                isbn10 = doc.Isbn10?.FirstOrDefault()
                         ?? doc.Isbn?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.Replace("-", "").Length == 10),
                cover_i = doc.CoverId,
                edition_key = doc.EditionKey?.FirstOrDefault(),
                cover_edition_key = doc.CoverEditionKey,
                image = coverUrl is not null ? new { url = coverUrl } : null,
                tags = doc.Subject?.Take(3) ?? Enumerable.Empty<string>(),
                source = "openlibrary"
            };
        })
        .ToList();

    return Results.Ok(new { query, count = books.Count, books });
})
.WithName("OpenLibrarySearch");

// Exact-title Hardcover search (kept for compatibility or future needs)
app.MapGet("/hardcover/search", async (string title, IHttpClientFactory factory, IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "title is required" });

    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new
        {
            error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
        });
    }

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
                editions_count
                default_physical_edition {
                  isbn_13
                  isbn_10
                }
                default_ebook_edition {
                  isbn_13
                  isbn_10
                }
                cached_contributors
                contributions(where: { contributable_type: { _eq: ""Book"" } }) {
                  contributable_type
                  author {
                    id
                    name
                    slug
                  }
                }
                image {
                  url
                }
              }
            }",
        variables = new { title }
    };

    var response = await client.PostAsJsonAsync("", graphqlRequest);

    if (!response.IsSuccessStatusCode)
        return await ForwardHardcoverError(response, "search");

    var resultJson = await response.Content.ReadFromJsonAsync<object>();
    return Results.Ok(resultJson);
})
.WithName("HardcoverExactSearch");

// Hardcover "Want to read" list (status_id = 1)
app.MapGet("/hardcover/want-to-read", async (IHttpClientFactory factory, IConfiguration config) =>
{
    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new
        {
            error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
        });
    }

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
                    editions_count
                    default_physical_edition {
                      isbn_13
                      isbn_10
                    }
                    default_ebook_edition {
                      isbn_13
                      isbn_10
                    }
                    cached_contributors
                    contributions(where: { contributable_type: { _eq: ""Book"" } }) {
                      contributable_type
                      author {
                        id
                        name
                        slug
                      }
                    }
                    image {
                      url
                    }
                  }
                }
              }
            }"
    };

    var response = await client.PostAsJsonAsync("", graphqlRequest);

    if (!response.IsSuccessStatusCode)
        return await ForwardHardcoverError(response, "want-to-read");

    var resultJson = await response.Content.ReadFromJsonAsync<object>();
    return Results.Ok(resultJson);
})
.WithName("GetHardcoverWantToRead");

app.MapGet("/calibre/books", async (CalibreMirrorRepository mirrorRepo, CalibreCoverService coverService, CancellationToken cancellationToken) =>
{
    IReadOnlyList<CalibreMirrorBook> books = await mirrorRepo.GetBooksAsync(200, cancellationToken);
    var state = await mirrorRepo.GetSyncStateAsync(cancellationToken);

    string? libraryRoot = null;
    if (!string.IsNullOrWhiteSpace(state.CalibrePath))
    {
        try
        {
            var full = Path.GetFullPath(state.CalibrePath);
            libraryRoot = Path.GetDirectoryName(full);
        }
        catch
        {
            libraryRoot = null;
        }
    }

    if (!string.IsNullOrWhiteSpace(libraryRoot))
    {
        var hydrated = new List<CalibreMirrorBook>(books.Count);
        foreach (var book in books)
        {
            string? coverUrl = book.CoverUrl;
            if (book.HasCover && !coverService.CoverExists(book.Id) && !string.IsNullOrWhiteSpace(book.Path))
            {
                try
                {
                    var ensured = await coverService.EnsureCoverAsync(book.Id, libraryRoot!, book.Path, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ensured))
                    {
                        coverUrl = ensured;
                    }
                }
                catch
                {
                    // ignore and keep existing coverUrl if copy fails
                }
            }

            hydrated.Add(ReferenceEquals(coverUrl, book.CoverUrl) ? book : book with { CoverUrl = coverUrl });
        }

        books = hydrated;
    }

    return Results.Ok(new
    {
        count = books.Count,
        books,
        lastSync = state.LastSnapshot,
        sourcePath = state.CalibrePath
    });
})
.WithName("GetCalibreBooks");

app.MapPost("/calibre/sync", async (CalibreSyncService syncService, CancellationToken cancellationToken) =>
{
    var result = await syncService.SyncAsync(cancellationToken);
    if (!result.Success)
    {
        return Results.BadRequest(new { error = result.Error });
    }

    return Results.Ok(new { synced = result.Count, snapshot = result.Snapshot });
})
.WithName("SyncCalibreLibrary");

app.MapGet("/settings/calibre", (UserSettingsStore store) =>
{
    var path = store.CalibreDatabasePath;
    var exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    return Results.Ok(new { path, exists });
});

app.MapPost("/settings/calibre", async (CalibrePathRequest request, UserSettingsStore store) =>
{
    var trimmed = request.Path?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        await store.SetCalibreDatabasePathAsync(null);
        return Results.Ok(new { path = (string?)null, exists = false });
    }

    string fullPath;
    try
    {
        var expanded = ExpandUserPath(trimmed);
        fullPath = Path.GetFullPath(expanded);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid path: {ex.Message}" });
    }

    if (!File.Exists(fullPath))
    {
        return Results.BadRequest(new { error = $"metadata.db not found at {fullPath}" });
    }

    await store.SetCalibreDatabasePathAsync(fullPath);
    return Results.Ok(new { path = fullPath, exists = true });
});

app.MapPost("/hardcover/want-to-read/status", async (HardcoverWantRequest request, IHttpClientFactory factory, IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(request.BookId))
    {
        return Results.BadRequest(new { error = "bookId is required" });
    }

    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new
        {
            error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
        });
    }

    var client = factory.CreateClient("hardcover");

    if (request.WantToRead)
    {
        const string updateMutation = @"
            mutation SetWantToRead($bookId: uuid!) {
              update_user_books(
                where: { book_id: { _eq: $bookId } }
                _set: { status_id: 1 }
              ) {
                affected_rows
              }
            }";

        var updateResponse = await client.PostAsJsonAsync("", new
        {
            query = updateMutation,
            variables = new { bookId = request.BookId }
        });

        if (!updateResponse.IsSuccessStatusCode)
        {
            return await ForwardHardcoverError(updateResponse, "want-to-read:update");
        }

        using var updateDoc = await updateResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var affected = ExtractAffectedRows(updateDoc, "update_user_books");

        if (affected == 0)
        {
            const string insertMutation = @"
                mutation InsertWantToRead($bookId: uuid!) {
                  insert_user_books_one(object: { book_id: $bookId, status_id: 1 }) {
                    status_id
                  }
                }";

            var insertResponse = await client.PostAsJsonAsync("", new
            {
                query = insertMutation,
                variables = new { bookId = request.BookId }
            });

            if (!insertResponse.IsSuccessStatusCode)
            {
                return await ForwardHardcoverError(insertResponse, "want-to-read:insert");
            }
        }

        return Results.Ok(new { status = "ok" });
    }

    const string deleteMutation = @"
        mutation RemoveWantToRead($bookId: uuid!) {
          delete_user_books(
            where: { book_id: { _eq: $bookId }, status_id: { _eq: 1 } }
          ) {
            affected_rows
          }
        }";

    var deleteResponse = await client.PostAsJsonAsync("", new
    {
        query = deleteMutation,
        variables = new { bookId = request.BookId }
    });

    if (!deleteResponse.IsSuccessStatusCode)
    {
        return await ForwardHardcoverError(deleteResponse, "want-to-read:remove");
    }

    return Results.Ok(new { status = "ok" });
})
.WithName("SetHardcoverWantStatus");

app.Run();

record OpenLibrarySearchResponse(
    [property: JsonPropertyName("docs")] IEnumerable<OpenLibraryDoc> Docs);

record OpenLibraryDoc
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("author_name")]
    public IEnumerable<string>? AuthorName { get; init; }

    [JsonPropertyName("cover_i")]
    public int? CoverId { get; init; }

    [JsonPropertyName("edition_count")]
    public int? EditionCount { get; init; }

    [JsonPropertyName("ratings_average")]
    public double? RatingsAverage { get; init; }

    [JsonPropertyName("ratings_count")]
    public int? RatingsCount { get; init; }

    [JsonPropertyName("subject")]
    public IEnumerable<string>? Subject { get; init; }

    [JsonPropertyName("edition_key")]
    public IEnumerable<string>? EditionKey { get; init; }

    [JsonPropertyName("cover_edition_key")]
    public string? CoverEditionKey { get; init; }

    [JsonPropertyName("isbn")]
    public IEnumerable<string>? Isbn { get; init; }

    [JsonPropertyName("isbn_13")]
    public IEnumerable<string>? Isbn13 { get; init; }

    [JsonPropertyName("isbn_10")]
    public IEnumerable<string>? Isbn10 { get; init; }
}

record HardcoverWantRequest(
    [property: JsonPropertyName("bookId")] string BookId,
    [property: JsonPropertyName("wantToRead")] bool WantToRead);

record CalibrePathRequest(
    [property: JsonPropertyName("path")] string? Path);
