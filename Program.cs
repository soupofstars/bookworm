using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bookworm.Data;
using Bookworm.Sync;
using Microsoft.Data.Sqlite;

static string ResolveStorageConnectionString(IConfiguration config, string contentRoot)
{
    var raw = config["Storage:Database"];
    if (string.IsNullOrWhiteSpace(raw))
    {
        raw = "App_Data/bookworm.db";
    }

    // Accept either a full SQLite connection string or a bare file path.
    if (!raw.Contains('='))
    {
        raw = $"Data Source={raw}";
    }

    var connBuilder = new SqliteConnectionStringBuilder(raw);
    var dataSource = connBuilder.DataSource;
    if (!Path.IsPathRooted(dataSource))
    {
        dataSource = Path.GetFullPath(Path.Combine(contentRoot, dataSource));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
    connBuilder.DataSource = dataSource;
    return connBuilder.ToString();
}

var builder = WebApplication.CreateBuilder(args);
var storageConnection = ResolveStorageConnectionString(builder.Configuration, builder.Environment.ContentRootPath);
var openLibraryTimeout = TimeSpan.FromSeconds(8);

// --- Services -------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(new WantedRepository(storageConnection));
builder.Services.AddSingleton(new CalibreMirrorRepository(storageConnection));
builder.Services.AddSingleton(new UserSettingsStore(builder.Configuration, storageConnection));
builder.Services.AddSingleton<CalibreRepository>();
builder.Services.AddSingleton<CalibreCoverService>();
builder.Services.AddSingleton<CalibreSyncService>();
builder.Services.AddSingleton<HardcoverListService>();
builder.Services.AddSingleton(new HardcoverListCacheRepository(storageConnection));
builder.Services.AddSingleton(new SuggestedRepository(storageConnection));
builder.Services.AddSingleton<SuggestedRankingService>();
builder.Services.AddSingleton(new SearchHistoryRepository(storageConnection));
builder.Services.AddSingleton(new ActivityLogRepository(storageConnection));
builder.Services.AddSingleton<ActivityLogService>();
builder.Services.AddSingleton(new BookshelfRepository(storageConnection));
builder.Services.AddSingleton(new HardcoverWantCacheRepository(storageConnection));
builder.Services.AddHostedService<CalibreHaveSyncSchedule>();
builder.Services.AddHostedService<HardcoverWantSyncSchedule>();
builder.Services.AddSingleton(new HardcoverBookshelfMapRepository(storageConnection));
builder.Services.AddSingleton<HardcoverBookshelfResolver>();
builder.Services.AddHostedService<HardcoverBookshelfSyncSchedule>();
builder.Services.AddSingleton<CalibreSyncStateService>();
builder.Services.AddHostedService<SuggestedDedupSchedule>();

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
        var headerValue = apiKey.Trim();
        if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            headerValue = $"Bearer {headerValue}";
        }

        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(headerValue);
    }
});

// HttpClient for OpenLibrary (for fuzzy book search)
builder.Services.AddHttpClient("openlibrary", client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BookwormApp/1.0 (+https://github.com/soupofstars/bookworm)");
    client.Timeout = openLibraryTimeout;
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
app.MapGet("/health/openlibrary", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("openlibrary");
    var sw = Stopwatch.StartNew();
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await client.GetAsync("/search.json?title=ping&limit=1", cts.Token);
        sw.Stop();
        return Results.Ok(new
        {
            reachable = response.IsSuccessStatusCode,
            status = (int)response.StatusCode,
            latencyMs = sw.ElapsedMilliseconds,
            source = "openlibrary"
        });
    }
    catch (TaskCanceledException)
    {
        sw.Stop();
        return Results.Ok(new
        {
            reachable = false,
            status = 408,
            latencyMs = sw.ElapsedMilliseconds,
            source = "openlibrary",
            error = "timeout"
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Results.Ok(new
        {
            reachable = false,
            latencyMs = sw.ElapsedMilliseconds,
            source = "openlibrary",
            error = ex.Message
        });
    }
});

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

static bool ExistsInUserBooks(JsonDocument? doc)
{
    if (doc is null ||
        !doc.RootElement.TryGetProperty("data", out var data) ||
        !data.TryGetProperty("me", out var meEl))
    {
        return false;
    }

    JsonElement user = meEl;
    if (meEl.ValueKind == JsonValueKind.Array)
    {
        if (meEl.GetArrayLength() == 0) return false;
        user = meEl[0];
    }
    else if (meEl.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    foreach (var propertyName in new[] { "user_book", "user_books" })
    {
        if (user.TryGetProperty(propertyName, out var booksEl) &&
            booksEl.ValueKind == JsonValueKind.Array &&
            booksEl.GetArrayLength() > 0)
        {
            return true;
        }
    }

    return false;
}

static bool HasGraphQlErrors(JsonDocument? doc)
{
    if (doc is null) return true;
    if (!doc.RootElement.TryGetProperty("errors", out var errors)) return false;
    return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
}

static string? ExtractGraphQlErrors(JsonDocument? doc)
{
    if (doc is null) return null;
    if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var messages = new List<string>();
    foreach (var err in errors.EnumerateArray())
    {
        if (err.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            messages.Add(message.GetString()!);
        }
    }

    return messages.Count > 0 ? string.Join("; ", messages) : errors.ToString();
}

static bool IsUniqueConflict(string? errorMessage)
{
    if (string.IsNullOrWhiteSpace(errorMessage)) return false;
    var lower = errorMessage.ToLowerInvariant();
    return lower.Contains("constraint") || lower.Contains("duplicate") || lower.Contains("unique");
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

static string? ExtractKey(JsonElement book)
{
    if (book.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
    {
        return id.GetString();
    }

    if (book.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
    {
        return slug.GetString();
    }

    if (book.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
    {
        return title.GetString();
    }

    return null;
}

static string? ReadJsonString(JsonElement element, string property)
{
    if (!element.TryGetProperty(property, out var child)) return null;
    return child.ValueKind switch
    {
        JsonValueKind.String => child.GetString(),
        JsonValueKind.Number => child.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };
}

static JsonElement? ParseJsonElement(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

static T[] DeserializeJsonArray<T>(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return Array.Empty<T>();
    try
    {
        return JsonSerializer.Deserialize<T[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? Array.Empty<T>();
    }
    catch
    {
        return Array.Empty<T>();
    }
}

static IReadOnlyList<string> ExtractGenreTags(JsonElement book)
{
    if (!book.TryGetProperty("cached_tags", out var tags))
    {
        return Array.Empty<string>();
    }

    return ExtractGenresFromCachedTags(tags);
}

static IReadOnlyList<string> ExtractGenresFromCachedTags(JsonElement tagsElement)
{
    var ordered = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void AddName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        if (seen.Add(trimmed))
        {
            ordered.Add(trimmed);
        }
    }

    void AddFromArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddName(item.GetString());
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                AddName(TryResolveGenreName(item));
            }
        }
    }

    if (tagsElement.ValueKind == JsonValueKind.String)
    {
        var raw = tagsElement.GetString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                return ExtractGenresFromCachedTags(parsed.RootElement);
            }
            catch
            {
                AddName(raw);
            }
        }
        return ordered;
    }

    if (tagsElement.ValueKind == JsonValueKind.Array)
    {
        AddFromArray(tagsElement);
        return ordered;
    }

    if (tagsElement.ValueKind == JsonValueKind.Object)
    {
        var keys = new[] { "Genre", "Genres", "genre", "genres" };
        foreach (var key in keys)
        {
            if (tagsElement.TryGetProperty(key, out var genreNode))
            {
                if (genreNode.ValueKind == JsonValueKind.String)
                {
                    AddName(genreNode.GetString());
                    return ordered;
                }

                AddFromArray(genreNode);
                if (ordered.Count > 0)
                {
                    return ordered;
                }
            }
        }

        foreach (var prop in tagsElement.EnumerateObject())
        {
            AddFromArray(prop.Value);
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                AddName(prop.Value.GetString());
            }
        }
    }

    return ordered;
}

static IReadOnlyList<string> ExtractAuthorNames(JsonElement book)
{
    if (book.ValueKind != JsonValueKind.Object) return Array.Empty<string>();

    var names = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Add(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        if (seen.Add(trimmed))
        {
            names.Add(trimmed);
        }
    }

    void AddFromArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                Add(item.GetString());
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                Add(ReadJsonString(item, "name"));
                Add(ReadJsonString(item, "author"));
            }
        }
    }

    foreach (var property in new[] { "author_names", "authors" })
    {
        if (book.TryGetProperty(property, out var authorsEl))
        {
            if (authorsEl.ValueKind == JsonValueKind.String)
            {
                Add(authorsEl.GetString());
            }
            else
            {
                AddFromArray(authorsEl);
            }
        }
    }

    if (book.TryGetProperty("cached_contributors", out var contribs))
    {
        AddFromArray(contribs);
    }

    if (names.Count == 0 && book.TryGetProperty("contributions", out var contributions) && contributions.ValueKind == JsonValueKind.Array)
    {
        foreach (var entry in contributions.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("author", out var authorObj) &&
                authorObj.ValueKind == JsonValueKind.Object)
            {
                Add(ReadJsonString(authorObj, "name"));
            }
        }
    }

    return names.Count == 0 ? Array.Empty<string>() : names;
}

static string? ExtractBookTitle(JsonElement book)
{
    if (book.ValueKind != JsonValueKind.Object) return null;
    foreach (var property in new[] { "title", "name" })
    {
        var value = ReadJsonString(book, property);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static string? NormalizeIsbnValue(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
    if (cleaned.Length < 10) return null;
    return cleaned.ToUpperInvariant();
}

static IReadOnlyList<string> ExtractIsbnCandidates(JsonElement book)
{
    if (book.ValueKind != JsonValueKind.Object) return Array.Empty<string>();

    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Add(string? raw)
    {
        var norm = NormalizeIsbnValue(raw);
        if (!string.IsNullOrWhiteSpace(norm))
        {
            set.Add(norm);
        }
    }

    void AddElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                Add(el.GetString());
                break;
            case JsonValueKind.Number:
                Add(el.GetRawText());
                break;
        }
    }

    void AddProperty(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return;
        AddElement(el);
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                AddElement(item);
            }
        }
    }

    AddProperty(book, "isbn13");
    AddProperty(book, "isbn_13");
    AddProperty(book, "isbn10");
    AddProperty(book, "isbn_10");
    AddProperty(book, "isbn");

    if (book.TryGetProperty("default_physical_edition", out var phys) && phys.ValueKind == JsonValueKind.Object)
    {
        AddProperty(phys, "isbn_13");
        AddProperty(phys, "isbn_10");
    }

    if (book.TryGetProperty("default_ebook_edition", out var ebook) && ebook.ValueKind == JsonValueKind.Object)
    {
        AddProperty(ebook, "isbn_13");
        AddProperty(ebook, "isbn_10");
    }

    return set.Count == 0 ? Array.Empty<string>() : set.ToArray();
}

static string? TryResolveGenreName(JsonElement element)
{
    if (element.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    string? GetNested(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var nested)
            ? nested.ValueKind switch
            {
                JsonValueKind.String => nested.GetString(),
                JsonValueKind.Number => nested.GetRawText(),
                _ => null
            }
            : null;
    }

    var direct = GetNested(element, "name") ?? GetNested(element, "label");
    if (!string.IsNullOrWhiteSpace(direct))
    {
        return direct;
    }

    var tag = GetNested(element, "tag") ?? GetNested(element, "tagSlug");
    if (!string.IsNullOrWhiteSpace(tag))
    {
        return tag;
    }

    if (element.TryGetProperty("genre", out var genreObj))
    {
        var name = GetNested(genreObj, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
    }

    if (element.TryGetProperty("base_genre", out var baseGenreObj))
    {
        var name = GetNested(baseGenreObj, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
    }

    return null;
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

// Calibre plugin-friendly view of wanted list (title + ISBNs)
app.MapGet("/api/calibre/wanted", async (WantedRepository repo) =>
{
    var items = await repo.GetAllAsync();
    var payload = items.Select(entry =>
    {
        var book = entry.Book;
        var key = !string.IsNullOrWhiteSpace(entry.Key) ? entry.Key : (ExtractKey(book) ?? ExtractBookTitle(book));
        var title = ExtractBookTitle(book) ?? entry.Key ?? ExtractKey(book) ?? "Untitled";

        return new
        {
            key,
            title,
            authors = ExtractAuthorNames(book),
            isbns = ExtractIsbnCandidates(book),
            source = ReadJsonString(book, "source"),
            slug = ReadJsonString(book, "slug")
        };
    }).ToList();

    return Results.Ok(new
    {
        count = payload.Count,
        items = payload
    });
})
.WithName("GetWantedForCalibrePlugin");

// Fuzzy book search via OpenLibrary
app.MapGet("/search", async (string query, string? mode, int? page, bool? skipHistory, IHttpClientFactory factory, SearchHistoryRepository historyRepo) =>
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
    var pageNumber = Math.Max(1, page.GetValueOrDefault(1));
    const int pageSize = 20;
    List<OpenLibraryDoc> docs = new();
    int? numFound = null;

    foreach (var param in searchParams)
    {
        var url = $"/search.json?{param}={Uri.EscapeDataString(query)}&limit={pageSize}&page={pageNumber}&fields={fields}";
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url);
        }
        catch (TaskCanceledException)
        {
            return Results.Problem($"OpenLibrary search timed out after {openLibraryTimeout.TotalSeconds:F0} seconds.", statusCode: (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"OpenLibrary search failed: {ex.Message}", statusCode: (int)HttpStatusCode.BadGateway);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            return Results.Problem($"OpenLibrary error ({param}): {response.StatusCode} - {errorText}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenLibrarySearchResponse>()
                      ?? new OpenLibrarySearchResponse(Array.Empty<OpenLibraryDoc>());
        numFound = payload.NumFound;

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

    var totalCount = numFound ?? books.Count;
    if (skipHistory != true)
    {
        switch (normalizedMode)
        {
            case "author":
                await historyRepo.LogAuthorAsync(query, totalCount);
                break;
            case "isbn":
                await historyRepo.LogIsbnAsync(query, totalCount);
                break;
            default:
                await historyRepo.LogBookAsync(query, totalCount);
                break;
        }
    }
    return Results.Ok(new { query, count = books.Count, total = totalCount, page = pageNumber, pageSize, books });
})
.WithName("OpenLibrarySearch");

// Author search via OpenLibrary
app.MapGet("/search/authors", async (string query, int? page, int? pageSize, IHttpClientFactory factory, SearchHistoryRepository historyRepo) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query is required" });

    var pageNumber = page.GetValueOrDefault(1);
    if (pageNumber < 1) pageNumber = 1;
    var limit = pageSize.GetValueOrDefault(20);
    limit = Math.Clamp(limit, 1, 50);
    var offset = (pageNumber - 1) * limit;

    var client = factory.CreateClient("openlibrary");
    var authorUrl = $"/search/authors.json?q={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}";
    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(authorUrl);
    }
    catch (TaskCanceledException)
    {
        return Results.Problem($"OpenLibrary author search timed out after {openLibraryTimeout.TotalSeconds:F0} seconds.", statusCode: (int)HttpStatusCode.GatewayTimeout);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"OpenLibrary author search failed: {ex.Message}", statusCode: (int)HttpStatusCode.BadGateway);
    }
    if (!response.IsSuccessStatusCode)
    {
        var errorText = await response.Content.ReadAsStringAsync();
        return Results.Problem($"OpenLibrary author search error: {response.StatusCode} - {errorText}");
    }

    var payload = await response.Content.ReadFromJsonAsync<OpenLibraryAuthorSearchResponse>()
                  ?? new OpenLibraryAuthorSearchResponse(Array.Empty<OpenLibraryAuthorDoc>());

    var authors = payload.Docs
        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
        .Select(a => new
        {
            key = a.Key,
            name = a.Name,
            top_work = a.TopWork,
            work_count = a.WorkCount,
            birth_date = a.BirthDate,
            death_date = a.DeathDate,
            top_subjects = a.TopSubjects?.Take(5) ?? Enumerable.Empty<string>(),
            source = "openlibrary-author"
        })
        .ToList();

    await historyRepo.LogAuthorAsync(query, payload.NumFound ?? authors.Count);

    return Results.Ok(new
    {
        query,
        count = authors.Count,
        total = payload.NumFound ?? authors.Count,
        page = pageNumber,
        pageSize = limit,
        authors
    });
})
.WithName("OpenLibraryAuthorSearch");

// Search history (book searches)
app.MapGet("/search/history", async (int? take, SearchHistoryRepository repo) =>
{
    var limit = take.GetValueOrDefault(8);
    var items = await repo.GetRecentAsync(limit);
    return Results.Ok(new
    {
        items = items.Select(i => new
        {
            query = i.Query,
            count = i.ResultCount,
            lastSearched = i.LastSearched
        })
    });
})
.WithName("SearchHistory");

app.MapDelete("/search/history", async (string query, SearchHistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest(new { error = "query is required" });
    }

    await repo.DeleteAsync(query);
    return Results.NoContent();
})
.WithName("DeleteSearchHistory");

// Author search history
app.MapGet("/search/history/authors", async (int? take, SearchHistoryRepository repo) =>
{
    var limit = take.GetValueOrDefault(8);
    var items = await repo.GetRecentAuthorsAsync(limit);
    return Results.Ok(new
    {
        items = items.Select(i => new
        {
            query = i.Query,
            count = i.ResultCount,
            lastSearched = i.LastSearched
        })
    });
})
.WithName("AuthorSearchHistory");

app.MapDelete("/search/history/authors", async (string query, SearchHistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest(new { error = "query is required" });
    }

    await repo.DeleteAuthorAsync(query);
    return Results.NoContent();
})
.WithName("DeleteAuthorSearchHistory");

// ISBN search history
app.MapGet("/search/history/isbn", async (int? take, SearchHistoryRepository repo) =>
{
    var limit = take.GetValueOrDefault(8);
    var items = await repo.GetRecentIsbnAsync(limit);
    return Results.Ok(new
    {
        items = items.Select(i => new
        {
            query = i.Query,
            count = i.ResultCount,
            lastSearched = i.LastSearched
        })
    });
})
.WithName("IsbnSearchHistory");

app.MapDelete("/search/history/isbn", async (string query, SearchHistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest(new { error = "query is required" });
    }

    await repo.DeleteIsbnAsync(query);
    return Results.NoContent();
})
.WithName("DeleteIsbnSearchHistory");

// Hardcover search (exact title, then exact ISBN if provided; avoids like/ilike)
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

    string NormalizeIsbn(string raw)
    {
        var cleaned = new string(raw.Where(char.IsDigit).ToArray());
        return cleaned.Length >= 10 ? cleaned : string.Empty;
    }

    var isbn = NormalizeIsbn(title);
    const int limit = 20;

    // Exact title search
    var titlePayload = new
    {
        query = @"
            query BooksByExactTitle($title: String!, $limit: Int!) {
              books(
                where: { title: { _eq: $title } }
                limit: $limit
                order_by: { users_count: desc }
              ) {
                id
                title
                slug
                rating
                ratings_count
                users_count
                editions_count
                default_physical_edition { isbn_13 isbn_10 }
                default_ebook_edition { isbn_13 isbn_10 }
                cached_contributors
                contributions(where: { contributable_type: { _eq: ""Book"" } }) {
                  contributable_type
                  author { id name slug }
                }
                image { url }
              }
            }",
        variables = new { title = title.Trim(), limit }
    };

    async Task<(bool ok, List<JsonElement> books, HttpResponseMessage? response)> RunAsync(object payload)
    {
        var response = await client.PostAsJsonAsync("", payload);
        if (!response.IsSuccessStatusCode)
        {
            return (false, new List<JsonElement>(), response);
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        if (HasGraphQlErrors(doc))
        {
            return (false, new List<JsonElement>(), response);
        }

        var list = new List<JsonElement>();
        if (doc is not null &&
            doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("books", out var books) &&
            books.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in books.EnumerateArray())
            {
                list.Add(item.Clone());
            }
        }

        return (true, list, response);
    }

    var (titleOk, titleBooks, titleResp) = await RunAsync(titlePayload);
    if (titleOk && titleBooks.Count > 0)
    {
        return Results.Ok(new { data = new { books = titleBooks } });
    }

    // ISBN search if we have a normalized isbn
    if (!string.IsNullOrWhiteSpace(isbn))
    {
        var isbnPayload = new
        {
            query = @"
                query BooksByIsbn($isbn: String!, $limit: Int!) {
                  books(
                    where: {
                      _or: [
                        { isbn13: { _eq: $isbn } }
                        { isbn_13: { _eq: $isbn } }
                        { isbn10: { _eq: $isbn } }
                        { isbn_10: { _eq: $isbn } }
                        { default_physical_edition: { isbn_13: { _eq: $isbn } } }
                        { default_physical_edition: { isbn_10: { _eq: $isbn } } }
                        { default_ebook_edition: { isbn_13: { _eq: $isbn } } }
                        { default_ebook_edition: { isbn_10: { _eq: $isbn } } }
                      ]
                    }
                    limit: $limit
                    order_by: { users_count: desc }
                  ) {
                    id
                    title
                    slug
                    rating
                    ratings_count
                    users_count
                    editions_count
                    default_physical_edition { isbn_13 isbn_10 }
                    default_ebook_edition { isbn_13 isbn_10 }
                    cached_contributors
                    contributions(where: { contributable_type: { _eq: ""Book"" } }) {
                      contributable_type
                      author { id name slug }
                    }
                    image { url }
                  }
                }",
            variables = new { isbn, limit }
        };

        var (isbnOk, isbnBooks, isbnResp) = await RunAsync(isbnPayload);
        if (isbnOk && isbnBooks.Count > 0)
        {
            return Results.Ok(new { data = new { books = isbnBooks } });
        }

        if (!isbnOk && isbnResp is not null && !isbnResp.IsSuccessStatusCode)
        {
            return await ForwardHardcoverError(isbnResp, "search");
        }
    }

    if (!titleOk && titleResp is not null && !titleResp.IsSuccessStatusCode)
    {
        return await ForwardHardcoverError(titleResp, "search");
    }

    return Results.Ok(new { data = new { books = Array.Empty<JsonElement>() } });
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

    var queries = new[]
    {
        @"
            query MeWantToRead {
              me {
                user_book(
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
            }",
        @"
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

    HttpResponseMessage? lastResponse = null;
    foreach (var query in queries)
    {
        var response = await client.PostAsJsonAsync("", new { query });
        lastResponse = response;

        if (!response.IsSuccessStatusCode)
        {
            continue;
        }

        var resultJson = await response.Content.ReadFromJsonAsync<JsonDocument>();
        if (!HasGraphQlErrors(resultJson))
        {
            return Results.Ok(resultJson);
        }
    }

    return await ForwardHardcoverError(lastResponse!, "want-to-read");
})
.WithName("GetHardcoverWantToRead");

// Hardcover want-to-read cache
app.MapGet("/hardcover/want-to-read/cache", async (HardcoverWantCacheRepository repo) =>
{
    var entries = await repo.GetAllAsync();
    var books = new List<JsonElement>(entries.Count);
    foreach (var entry in entries)
    {
        if (string.IsNullOrWhiteSpace(entry.BookJson)) continue;
        try
        {
            using var doc = JsonDocument.Parse(entry.BookJson);
            books.Add(doc.RootElement.Clone());
        }
        catch
        {
            // ignore malformed JSON
        }
    }

    return Results.Ok(new { count = books.Count, books });
})
.WithName("GetHardcoverWantCache");

app.MapGet("/hardcover/want-to-read/state", async (HardcoverWantCacheRepository repo, IConfiguration config, CancellationToken cancellationToken) =>
{
    var stats = await repo.GetStatsAsync(cancellationToken);
    var configured = IsHardcoverConfigured(config);
    return Results.Ok(new
    {
        configured,
        count = stats.Count,
        lastUpdated = stats.LastUpdatedUtc
    });
})
.WithName("GetHardcoverWantState");

app.MapPost("/hardcover/want-to-read/cache", async (HardcoverWantCacheRequest request, HardcoverWantCacheRepository repo) =>
{
    var books = request?.Books ?? new List<JsonElement>();
    var result = await repo.ReplaceAllAsync(books);
    return Results.Ok(new { cached = result.Cached, removed = result.Removed });
})
.WithName("SetHardcoverWantCache");

app.MapGet("/hardcover/owned", async (
    int? take,
    IHttpClientFactory factory,
    IConfiguration config,
    UserSettingsStore settings) =>
{
    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new
        {
            error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
        });
    }

    var listIdRaw = settings.HardcoverListId;
    if (string.IsNullOrWhiteSpace(listIdRaw) || !int.TryParse(listIdRaw, out var listId))
    {
        return Results.BadRequest(new { error = "Hardcover list id not configured." });
    }

    // Hardcover allows large lists; default to 500 to avoid truncation (user reports 118 items).
    var limit = Math.Clamp(take ?? 500, 1, 500);
    var client = factory.CreateClient("hardcover");

    const string bookFields = @"
        id
        title
        slug
        rating
        ratings_count
        users_count
        cached_tags
        cached_contributors
        contributions(where: { contributable_type: { _eq: ""Book"" } }) {
          contributable_type
          author { id name slug }
        }
        image { url }
    ";

    var queries = new[]
    {
        new
        {
            Name = "lists",
            Query = @"
                query OwnedListArray($listId: Int!, $limit: Int!) {
                  lists(where: { id: { _eq: $listId } }) {
                    id
                    name
                    slug
                    user { name username }
                    list_books(
                      limit: $limit
                      order_by: { created_at: desc }
                    ) {
                      book { " + bookFields + @" }
                      position
                      date_added
                    }
                  }
                }",
            PropertyName = "lists",
            ParseAsListItems = false,
            PropertyIsArray = true
        },
        new
        {
            Name = "list_items_public",
            Query = @"
                query OwnedListPublic($listId: Int!, $limit: Int!) {
                  list_items_public(
                    where: { list_id: { _eq: $listId } }
                    limit: $limit
                    order_by: { created_at: desc }
                  ) {
                    book { " + bookFields + @" }
                    list { id name slug user { name username } }
                  }
                }",
            PropertyName = "list_items_public",
            ParseAsListItems = true,
            PropertyIsArray = true
        },
        new
        {
            Name = "list_items",
            Query = @"
                query OwnedListPrivate($listId: Int!, $limit: Int!) {
                  list_items(
                    where: { list_id: { _eq: $listId } }
                    limit: $limit
                    order_by: { created_at: desc }
                  ) {
                    book { " + bookFields + @" }
                    list { id name slug user { name username } }
                  }
                }",
            PropertyName = "list_items",
            ParseAsListItems = true,
            PropertyIsArray = true
        },
        new
        {
            Name = "lists_by_pk",
            Query = @"
                query OwnedListFallback($listId: Int!, $limit: Int!) {
                  lists_by_pk(id: $listId) {
                    id
                    name
                    slug
                    user { name username }
                    list_books(
                      limit: $limit
                      order_by: { created_at: desc }
                    ) {
                      book { " + bookFields + @" }
                    }
                  }
                }",
            PropertyName = "lists_by_pk",
            ParseAsListItems = false,
            PropertyIsArray = false
        }
    };

    (List<JsonElement> Books, object? ListInfo)? TryParseListItems(JsonDocument? doc, string propertyName)
    {
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(propertyName, out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        object? listInfo = null;
        var books = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            if (listInfo is null &&
                item.TryGetProperty("list", out var listEl) &&
                listEl.ValueKind == JsonValueKind.Object)
            {
                listInfo = new
                {
                    id = ReadJsonString(listEl, "id"),
                    name = ReadJsonString(listEl, "name"),
                    slug = ReadJsonString(listEl, "slug"),
                    owner = listEl.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object
                        ? (ReadJsonString(userEl, "name") ?? ReadJsonString(userEl, "username"))
                        : null
                };
            }

            if (item.TryGetProperty("book", out var bookEl) && bookEl.ValueKind == JsonValueKind.Object)
            {
                books.Add(bookEl.Clone());
            }
        }

        return new(books, listInfo);
    }

    (List<JsonElement> Books, object? ListInfo)? TryParseListBooks(JsonDocument? doc, string propertyName, bool propertyIsArray)
    {
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(propertyName, out var listNode))
        {
            return null;
        }

        JsonElement listEl;
        if (propertyIsArray)
        {
            if (listNode.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            listEl = listNode.EnumerateArray().FirstOrDefault();
            if (listEl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }
        else
        {
            if (listNode.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            listEl = listNode;
        }

        object? listInfo = new
        {
            id = ReadJsonString(listEl, "id"),
            name = ReadJsonString(listEl, "name"),
            slug = ReadJsonString(listEl, "slug"),
            owner = listEl.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object
                ? (ReadJsonString(userEl, "name") ?? ReadJsonString(userEl, "username"))
                : null
        };

        var books = new List<JsonElement>();
        if (listEl.TryGetProperty("list_books", out var listBooks) && listBooks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in listBooks.EnumerateArray())
            {
                if (item.TryGetProperty("book", out var bookEl) && bookEl.ValueKind == JsonValueKind.Object)
                {
                    books.Add(bookEl.Clone());
                }
            }
        }

        return new(books, listInfo);
    }

    HttpResponseMessage? lastResponse = null;
    JsonDocument? lastDoc = null;
    foreach (var entry in queries)
    {
        var payload = new
        {
            query = entry.Query,
            variables = new { listId, limit }
        };

        var response = await client.PostAsJsonAsync("", payload);
        lastResponse = response;

        if (response.StatusCode == (HttpStatusCode)429)
        {
            return await ForwardHardcoverError(response, "owned-list");
        }

        if (!response.IsSuccessStatusCode)
        {
            continue;
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        lastDoc = doc;
        if (HasGraphQlErrors(doc))
        {
            var errors = ExtractGraphQlErrors(doc);
            if (!string.IsNullOrWhiteSpace(errors) && errors.Contains("throttle", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    title: "Hardcover rate limited",
                    detail: errors,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            continue;
        }

        (List<JsonElement> Books, object? ListInfo)? parsed = entry.ParseAsListItems
            ? TryParseListItems(doc, entry.PropertyName)
            : TryParseListBooks(doc, entry.PropertyName, entry.PropertyIsArray);

        if (parsed is not null)
        {
            var listInfo = parsed.Value.ListInfo ?? new
            {
                id = listIdRaw,
                name = (string?)null,
                slug = (string?)null,
                owner = (string?)null
            };

            return Results.Ok(new
            {
                list = listInfo,
                count = parsed.Value.Books.Count,
                books = parsed.Value.Books
            });
        }
    }

    if (lastDoc is not null)
    {
        var errors = ExtractGraphQlErrors(lastDoc);
        var status = StatusCodes.Status400BadRequest;
        if (!string.IsNullOrWhiteSpace(errors) && errors.Contains("throttle", StringComparison.OrdinalIgnoreCase))
        {
            status = StatusCodes.Status429TooManyRequests;
        }
        return Results.Problem(
            title: "Hardcover owned list query failed",
            detail: string.IsNullOrWhiteSpace(errors) ? "Hardcover GraphQL returned errors." : errors,
            statusCode: status);
    }

    return lastResponse is null
        ? Results.Problem("Unable to query Hardcover owned list.")
        : await ForwardHardcoverError(lastResponse, "owned-list");
})
.WithName("GetHardcoverOwnedList");

app.MapGet("/hardcover/calibre-recommendations", async (
    int? take,
    int? lists,
    int? perList,
    double? minRating,
    int? delayMs,
    IConfiguration config,
    CalibreMirrorRepository mirrorRepo,
    HardcoverListService listService,
    SuggestedRepository suggestedRepo,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!IsHardcoverConfigured(config))
        {
            return Results.BadRequest(new
            {
                error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
            });
        }

        var takeBooks = take.GetValueOrDefault(0);
        var listsPerBook = Math.Clamp(lists ?? 12, 1, 50);
        var itemsPerList = Math.Clamp(perList ?? 20, 1, 60);
        var delay = Math.Clamp(delayMs ?? 450, 0, 2000);

        var requestedTake = takeBooks <= 0 ? 0 : takeBooks;
        var books = await mirrorRepo.GetBooksAsync(requestedTake, cancellationToken);
        if (books.Count == 0)
        {
            return Results.BadRequest(new { error = "No Calibre books available. Sync Calibre first." });
        }

        var result = await listService.GetRecommendationsAsync(
            books,
            takeBooks,
            listsPerBook,
            itemsPerList,
            minRating,
            delay,
            cancellationToken);

        var recommendations = result.Recommendations
            .Select(r => new
            {
                book = r.Book,
                occurrences = r.Occurrences,
                reasons = r.Reasons,
                base_genres = ExtractGenreTags(r.Book)
            })
            .ToList();

        var suggestedEntries = recommendations
            .Select(r => new SuggestedEntry(
                Book: r.book,
                BaseGenres: r.base_genres,
                Reasons: r.reasons,
                SourceKey: ExtractKey(r.book)))
            .ToList();
        await suggestedRepo.UpsertMissingAsync(suggestedEntries, cancellationToken);

        return Results.Ok(new
        {
            result.InspectedCalibreBooks,
            result.MatchedCalibreBooks,
            UniqueRecommendations = recommendations.Count,
            Recommendations = recommendations,
            result.Matches,
            result.Steps,
            totalCalibreBooks = books.Count
        });
    }
    catch (Exception ex)
    {
        if (ex is HttpRequestException httpEx &&
            (httpEx.Message.Contains("list_items_public", StringComparison.OrdinalIgnoreCase)
             || httpEx.Message.Contains("list_items", StringComparison.OrdinalIgnoreCase)))
        {
            return Results.BadRequest(new
            {
                error = "Hardcover API schema does not expose list_items/list_items_public. People-list recommendations are unavailable via this API."
            });
        }

        return Results.Problem(
            title: "Failed to load Hardcover people lists",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name
            });
    }
})
.WithName("HardcoverCalibreRecommendations");

app.MapGet("/hardcover/calibre-recommendations/stream", async (
    int? take,
    int? lists,
    int? perList,
    double? minRating,
    int? delayMs,
    IConfiguration config,
    CalibreMirrorRepository mirrorRepo,
    HardcoverListService listService,
    HttpContext context,
    HardcoverListCacheRepository cacheRepo,
    SuggestedRepository suggestedRepo,
    CancellationToken cancellationToken) =>
{
    var response = context.Response;
    response.Headers.CacheControl = "no-cache";
    response.Headers.Connection = "keep-alive";
    response.Headers["X-Accel-Buffering"] = "no";
    response.ContentType = "text/event-stream";

    var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
    var token = linked.Token;

    async Task WriteEventAsync(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"event: {eventName}\n", token);
        await response.WriteAsync($"data: {json}\n\n", token);
        await response.Body.FlushAsync(token);
    }

    try
    {
        if (!IsHardcoverConfigured(config))
        {
            await WriteEventAsync("error", new { error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey." });
            return Results.Empty;
        }

        var takeBooks = take.GetValueOrDefault(0);
        var listsPerBook = Math.Clamp(lists ?? 25, 1, 100);
        var itemsPerList = Math.Clamp(perList ?? 30, 1, 100);
        var delay = Math.Clamp(delayMs ?? 2000, 2000, 120000);

        var requestedTake = takeBooks <= 0 ? 0 : takeBooks;
        var books = await mirrorRepo.GetBooksAsync(requestedTake, token);
        if (books.Count == 0)
        {
            await WriteEventAsync("error", new { error = "No Calibre books available. Sync Calibre first." });
            return Results.Empty;
        }

        var aggregator = new Dictionary<string, RecAccumulatorSummary>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<object>();
        var inspected = 0;
        var matched = 0;

        var totalCalibreBooks = books.Count;

        foreach (var book in books)
        {
            token.ThrowIfCancellationRequested();
            inspected++;

            var existing = await cacheRepo.GetAsync(book.Id, token);
            HardcoverBookListResult result;
            try
            {
                result = await listService.GetListsForBookAsync(book, listsPerBook, itemsPerList, minRating, token);
            }
            catch (Exception ex)
            {
                await WriteEventAsync("step", new
                {
                    calibreId = book.Id,
                    title = book.Title,
                    isbn = book.Isbn,
                    matchedHardcover = false,
                    hardcoverBookId = (string?)null,
                    hardcoverTitle = (string?)null,
                    listsChecked = 0,
                    recommendationsAdded = 0,
                    error = ex.Message
                });
                continue;
            }

            var listCount = result.Lists.Count;
            var recCount = result.Recommendations.Count;
            var matchedHardcover = !string.IsNullOrWhiteSpace(result.HardcoverId);
            var preserveExisting = !matchedHardcover
                                   && existing is not null
                                   && string.Equals(existing.Status, "ok", StringComparison.OrdinalIgnoreCase)
                                   && existing.ListCount > 0
                                   && !string.IsNullOrWhiteSpace(existing.HardcoverId);

            IReadOnlyList<HardcoverListHit> listsForUse = preserveExisting
                ? DeserializeJsonArray<HardcoverListHit>(existing!.ListsJson)
                : result.Lists;
            IReadOnlyList<HardcoverListRecommendation> recsForUse = preserveExisting
                ? DeserializeJsonArray<HardcoverListRecommendation>(existing!.RecommendationsJson)
                : result.Recommendations;
            var effectiveHardcoverId = preserveExisting ? existing!.HardcoverId : result.HardcoverId;
            var effectiveHardcoverTitle = preserveExisting ? existing!.HardcoverTitle : result.HardcoverTitle;
            var effectiveListCount = preserveExisting ? existing!.ListCount : listCount;
            var effectiveRecCount = preserveExisting ? existing!.RecommendationCount : recCount;
            var effectiveMatched = matchedHardcover || preserveExisting || (existing?.HardcoverId is not null);

            if (matchedHardcover || preserveExisting || existing?.HardcoverId is not null)
            {
                matched++;
            }

            // aggregate recommendations
            foreach (var rec in recsForUse)
            {
                var key = ExtractKey(rec.Book);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!aggregator.TryGetValue(key, out var acc))
                {
                    acc = new RecAccumulatorSummary(rec.Book);
                    aggregator[key] = acc;
                }

                acc.Count += rec.Occurrences;
                acc.Reasons.AddRange(rec.Reasons);
            }

            // cache this book's result
            if (!preserveExisting)
            {
                await cacheRepo.UpsertAsync(new HardcoverListCacheEntry(
                    CalibreId: book.Id,
                    CalibreTitle: book.Title,
                    HardcoverId: result.HardcoverId,
                    HardcoverTitle: result.HardcoverTitle,
                    ListCount: listCount,
                    RecommendationCount: recCount,
                    LastCheckedUtc: DateTime.UtcNow,
                    Status: matchedHardcover ? "ok" : "not_matched",
                    BaseGenresJson: cacheRepo.Serialize(result.BaseGenres ?? Array.Empty<string>()),
                    ListsJson: cacheRepo.Serialize(result.Lists),
                    RecommendationsJson: cacheRepo.Serialize(result.Recommendations)
                ), token);
            }

            var stepPayload = new
            {
                calibreId = book.Id,
                title = book.Title,
                isbn = book.Isbn,
                matchedHardcover = effectiveMatched,
                hardcoverBookId = effectiveHardcoverId,
                hardcoverTitle = effectiveHardcoverTitle,
                listsChecked = effectiveListCount,
                recommendationsAdded = effectiveRecCount,
                totalCalibreBooks
            };
            steps.Add(stepPayload);
            await WriteEventAsync("step", stepPayload);

            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        var recommendations = aggregator.Values
            .OrderByDescending(x => x.Count)
            .Select(x => new HardcoverListRecommendation(
                x.Book,
                x.Count,
                x.Reasons.ToArray()))
            .ToList();

        var recommendationPayload = recommendations
            .Select(r => new
            {
                book = r.Book,
                occurrences = r.Occurrences,
                reasons = r.Reasons,
                base_genres = ExtractGenreTags(r.Book)
            })
            .ToList();

        var suggestedEntries = recommendationPayload
            .Select(r => new SuggestedEntry(
                Book: r.book,
                BaseGenres: r.base_genres,
                Reasons: r.reasons,
                SourceKey: ExtractKey(r.book)))
            .ToList();
        await suggestedRepo.UpsertMissingAsync(suggestedEntries, token);

        await WriteEventAsync("summary", new
        {
            inspectedCalibreBooks = inspected,
            matchedCalibreBooks = matched,
            uniqueRecommendations = recommendationPayload.Count,
            totalCalibreBooks,
            recommendations = recommendationPayload,
            steps
        });
    }
    catch (Exception ex)
    {
        await WriteEventAsync("error", new { error = ex.Message, exception = ex.GetType().Name });
    }

    return Results.Empty;
})
.WithName("HardcoverCalibreRecommendationsStream");

app.MapGet("/calibre/books", async (
    int? take,
    BookshelfRepository bookshelfRepo,
    CalibreMirrorRepository mirrorRepo,
    CalibreCoverService coverService,
    CancellationToken cancellationToken) =>
{
    var takeBooks = Math.Clamp(take ?? 0, 0, int.MaxValue); // 0 = all
    IReadOnlyList<CalibreMirrorBook> books = await bookshelfRepo.GetBooksAsync(takeBooks, cancellationToken);
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
        sourcePath = state.CalibrePath,
        storagePath = mirrorRepo.DatabasePath
    });
})
.WithName("GetCalibreBooks");

app.MapGet("/calibre/sync/state", async (CalibreSyncStateService stateService, CancellationToken cancellationToken) =>
{
    var state = await stateService.GetStateAsync(cancellationToken);
    return Results.Ok(new
    {
        lastSync = state.LastSnapshot,
        sourcePath = state.SourcePath,
        storagePath = state.StoragePath,
        bookCount = state.BookCount,
        bookshelfUpdatedAt = state.BookshelfLastUpdated
    });
})
.WithName("GetCalibreSyncState");

app.MapPost("/calibre/sync", async (CalibreSyncService syncService, ActivityLogService activityLog, CancellationToken cancellationToken) =>
{
    var result = await syncService.SyncAsync(cancellationToken);
    if (!result.Success)
    {
        await activityLog.WarnAsync("Calibre sync", "Manual Calibre sync failed.", new { error = result.Error });
        return Results.BadRequest(new { error = result.Error });
    }

    var newIds = result.NewBookIds ?? Array.Empty<int>();
    var removedIds = result.RemovedBookIds ?? Array.Empty<int>();

    await activityLog.SuccessAsync("Calibre sync", "Manual Calibre sync completed.", new
    {
        total = result.Count,
        newCount = newIds.Count,
        removedCount = removedIds.Count
    });

    return Results.Ok(new
    {
        synced = result.Count,
        snapshot = result.Snapshot,
        newBookIds = newIds,
        removedBookIds = removedIds,
        newCount = newIds.Count,
        removedCount = removedIds.Count
    });
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

app.MapGet("/settings/hardcover/list", (UserSettingsStore store) =>
{
    return Results.Ok(new { listId = store.HardcoverListId });
});

app.MapPost("/settings/hardcover/list", async (HardcoverListRequest request, UserSettingsStore store) =>
{
    var trimmed = request?.ListId?.Trim();
    await store.SetHardcoverListIdAsync(trimmed);
    return Results.Ok(new { listId = store.HardcoverListId });
});

app.MapGet("/logs", async (int? take, ActivityLogRepository repo, CancellationToken cancellationToken) =>
{
    var entries = await repo.GetRecentAsync(take ?? 200, cancellationToken);
    var payload = entries.Select(entry => new
    {
        entry.Id,
        createdAt = entry.CreatedAt,
        entry.Source,
        entry.Level,
        entry.Message,
        details = ParseJsonElement(entry.DetailsJson)
    });

    return Results.Ok(new { entries = payload });
})
.WithName("GetActivityLog");

app.MapDelete("/logs", async (ActivityLogRepository repo, CancellationToken cancellationToken) =>
{
    await repo.ClearAsync(cancellationToken);
    return Results.Ok(new { status = "cleared" });
})
.WithName("ClearActivityLog");

app.MapPost("/hardcover/bookshelf/resolve", async (HardcoverBookshelfResolver resolver, CancellationToken cancellationToken) =>
{
    var result = await resolver.ResolveAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/hardcover/bookshelf/push-all", async (
    BookshelfRepository bookshelf,
    UserSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    var listIdRaw = settings.HardcoverListId;
    if (string.IsNullOrWhiteSpace(listIdRaw) || !int.TryParse(listIdRaw, out var listId))
    {
        return Results.BadRequest(new { error = "Hardcover list id not configured." });
    }

    var apiKey = config["Hardcover:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { error = "Hardcover API key not configured." });
    }

    var idsMap = await bookshelf.GetHardcoverIdsAsync(cancellationToken);
    var ids = idsMap.Values
        .Where(hid => !string.IsNullOrWhiteSpace(hid) && int.TryParse(hid, out _))
        .Select(hid => int.Parse(hid!))
        .Distinct()
        .ToList();

    if (ids.Count == 0)
    {
        return Results.Ok(new { pushed = 0, message = "No hardcover ids to push." });
    }

    const string mutation = @"
        mutation addBook($bookId: Int!, $listId: Int!) {
          insert_list_book(object: { book_id: $bookId, list_id: $listId }) {
            id
          }
        }";

    var client = httpClientFactory.CreateClient("hardcover");
    var pushed = 0;
    var failed = 0;
    var throttled = 0;
    foreach (var hid in ids)
    {
        var payload = new
        {
            query = mutation,
            variables = new { bookId = hid, listId }
        };

        async Task<bool> SendAsync()
        {
            using var response = await client.PostAsJsonAsync("", payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                throttled++;
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                failed++;
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    failed++;
                    return true;
                }
            }
            catch
            {
                // ignore parse errors
            }

            pushed++;
            return true;
        }

        var sent = await SendAsync();
        if (!sent)
        {
            // wait and retry once if throttled
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            await SendAsync();
        }

        // throttle to ~4 per minute
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
    }

    return Results.Ok(new { pushed, total = ids.Count, listId, failed, throttled });
});

app.MapGet("/suggested", async (SuggestedRepository repo, CancellationToken cancellationToken) =>
{
    var items = await repo.GetAllAsync(cancellationToken);
    return Results.Ok(new { count = items.Count, items });
});

app.MapGet("/suggested/ranked", async (SuggestedRepository repo, SuggestedRankingService ranking, CancellationToken cancellationToken) =>
{
    var items = await repo.GetAllAsync(cancellationToken);
    var ranked = await ranking.RankAsync(items, cancellationToken);

    var toDelete = ranked
        .Where(r => r.MatchedByIsbn && r.CalibreId is not null)
        .Select(r => r.SuggestedId)
        .ToList();
    if (toDelete.Count > 0)
    {
        await repo.DeleteByIdsAsync(toDelete, cancellationToken);
        ranked = ranked.Where(r => !toDelete.Contains(r.SuggestedId)).ToList();
    }

    return Results.Ok(new
    {
        count = ranked.Count,
            items = ranked.Select(r => new
            {
                id = r.SuggestedId,
                book = r.Book,
                baseGenres = r.BaseGenres,
                reasons = r.Reasons,
                matchScore = r.MatchScore,
                authorMatches = r.AuthorMatches,
                genreMatches = r.GenreMatches,
                tagMatches = r.TagMatches,
                titleBonusWords = r.TitleBonusWords,
                sourceKey = r.SourceKey,
                alreadyInCalibre = r.AlreadyInCalibre,
                calibreId = r.CalibreId,
                debug = r.DebugBreakdown
            })
    });
});

app.MapGet("/suggested/ignored", async (SuggestedRepository repo, CancellationToken cancellationToken) =>
{
    var items = await repo.GetByHiddenAsync(2, cancellationToken);
    return Results.Ok(new { count = items.Count, items });
});

app.MapPost("/suggested/hide", async (SuggestedHideRequest request, SuggestedRepository repo, CancellationToken cancellationToken) =>
{
    var ids = request?.Ids?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<int>();
    if (ids.Length == 0)
    {
        return Results.BadRequest(new { error = "No valid ids provided." });
    }

    var hiddenValue = request?.Hidden ?? 1;
    if (hiddenValue <= 0)
    {
        hiddenValue = 1;
    }

    await repo.HideByIdsAsync(ids, hiddenValue, cancellationToken);
    return Results.Ok(new { hidden = ids.Length, hiddenValue });
});

app.MapGet("/hardcover/list-cache/status", async (HardcoverListCacheRepository repo, CancellationToken cancellationToken) =>
{
    var entries = await repo.GetAllAsync(cancellationToken);
    var total = entries.Count;
    var withLists = entries.Count(e => e.ListCount > 0);
    var pending = entries.Count(e =>
        string.Equals(e.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
        (string.IsNullOrWhiteSpace(e.Status) && e.ListCount == 0 && e.RecommendationCount == 0));
    return Results.Ok(new { total, withLists, pending });
});

app.MapPost("/hardcover/book/resolve", async (HardcoverResolveRequest request, IHttpClientFactory factory, IConfiguration config) =>
{
    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new { error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey." });
    }

    static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    var isbnRaw = NormalizeIsbn(request?.Isbn);
    var bookId = request?.BookId;
    if (string.IsNullOrWhiteSpace(isbnRaw) && bookId is null)
    {
        return Results.BadRequest(new { error = "isbn or bookId is required" });
    }

    var client = factory.CreateClient("hardcover");
    const string isbnQuery = @"
        query BookByIsbn($isbn: String!, $isbn10: String!) {
          books(
            where: {
              _or: [
                { isbn13: { _eq: $isbn } }
                { isbn_13: { _eq: $isbn } }
                { isbn10: { _eq: $isbn10 } }
                { isbn_10: { _eq: $isbn10 } }
              ]
            }
            limit: 1
          ) {
            id
            slug
            title
            isbn13
            isbn_13
            isbn10
            isbn_10
            default_physical_edition { isbn_13 isbn_10 }
            default_ebook_edition { isbn_13 isbn_10 }
          }
        }";

    const string idQuery = @"
        query BookById($id: Int!) {
          books_by_pk(id: $id) {
            id
            slug
            title
            isbn13
            isbn_13
            isbn10
            isbn_10
            default_physical_edition { isbn_13 isbn_10 }
            default_ebook_edition { isbn_13 isbn_10 }
          }
        }";

    object BuildPayload()
    {
        if (bookId is not null)
        {
            return new { query = idQuery, variables = new { id = bookId.Value } };
        }

        // Best-effort ISBN-10 derivation; if not 10 digits, just reuse cleaned input.
        string AsIsbn10(string cleaned)
        {
            if (cleaned.Length == 10) return cleaned;
            if (cleaned.Length == 13) return cleaned[^10..];
            return cleaned;
        }

        return new { query = isbnQuery, variables = new { isbn = isbnRaw, isbn10 = AsIsbn10(isbnRaw!) } };
    }

    var payload = BuildPayload();

    var response = await client.PostAsJsonAsync("", payload);
    if (!response.IsSuccessStatusCode)
    {
        return await ForwardHardcoverError(response, "resolve-book");
    }

    using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
    if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) ||
        (!data.TryGetProperty("books", out var booksEl) && !data.TryGetProperty("books_by_pk", out booksEl)))
    {
        return Results.NotFound(new { error = "Book not found by ISBN." });
    }

    if (booksEl.ValueKind == JsonValueKind.Null)
    {
        return Results.NotFound(new { error = "Book not found by ISBN." });
    }

    var book = booksEl.ValueKind == JsonValueKind.Array
        ? booksEl[0]
        : booksEl;
    static string? ReadString(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var child)) return null;
        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.GetRawText(),
            _ => null
        };
    }

    var id = ReadString(book, "id");
    var slug = ReadString(book, "slug");
    return Results.Ok(new { id, slug });
});

app.MapPost("/hardcover/book/image", async (HardcoverResolveRequest request, IHttpClientFactory factory, IConfiguration config) =>
{
    if (!IsHardcoverConfigured(config))
    {
        return Results.BadRequest(new { error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey." });
    }

    static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    var isbnRaw = NormalizeIsbn(request?.Isbn);
    var bookId = request?.BookId;
    if (string.IsNullOrWhiteSpace(isbnRaw) && bookId is null)
    {
        return Results.BadRequest(new { error = "isbn or bookId is required" });
    }

    var client = factory.CreateClient("hardcover");
    const string isbnQuery = @"
        query BookImageByIsbn($isbn: String!, $isbn10: String!) {
          books(
            where: {
              _or: [
                { isbn13: { _eq: $isbn } }
                { isbn_13: { _eq: $isbn } }
                { isbn10: { _eq: $isbn10 } }
                { isbn_10: { _eq: $isbn10 } }
              ]
            }
            limit: 1
          ) {
            id
            image { url }
            cached_contributors { name image { url } }
          }
        }";

    const string idQuery = @"
        query BookImageById($id: Int!) {
          books_by_pk(id: $id) {
            id
            image { url }
            cached_contributors { name image { url } }
          }
        }";

    object BuildPayload()
    {
        if (bookId is not null)
        {
            return new { query = idQuery, variables = new { id = bookId.Value } };
        }

        string AsIsbn10(string cleaned)
        {
            if (cleaned.Length == 10) return cleaned;
            if (cleaned.Length == 13) return cleaned[^10..];
            return cleaned;
        }

        return new { query = isbnQuery, variables = new { isbn = isbnRaw, isbn10 = AsIsbn10(isbnRaw!) } };
    }

    var payload = BuildPayload();
    var response = await client.PostAsJsonAsync("", payload);
    if (!response.IsSuccessStatusCode)
    {
        return await ForwardHardcoverError(response, "book-image");
    }

    using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
    if (doc is null || !doc.RootElement.TryGetProperty("data", out var data))
    {
        return Results.NotFound(new { error = "Book not found." });
    }

    JsonElement? bookNode = null;
    if (data.TryGetProperty("books_by_pk", out var byPk) && byPk.ValueKind == JsonValueKind.Object)
    {
        bookNode = byPk;
    }
    else if (data.TryGetProperty("books", out var booksEl) && booksEl.ValueKind == JsonValueKind.Array && booksEl.GetArrayLength() > 0)
    {
        bookNode = booksEl[0];
    }

    if (bookNode is null)
    {
        return Results.NotFound(new { error = "Book not found." });
    }

    static string? ReadImage(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var child)) return null;
        if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            return urlEl.GetString();
        }
        return null;
    }

    var imageUrl = ReadImage(bookNode.Value, "image");
    string? contributorImage = null;
    if (bookNode.Value.TryGetProperty("cached_contributors", out var contribs) && contribs.ValueKind == JsonValueKind.Array)
    {
        foreach (var contrib in contribs.EnumerateArray())
        {
            contributorImage = ReadImage(contrib, "image");
            if (!string.IsNullOrWhiteSpace(contributorImage))
            {
                break;
            }
        }
    }

    if (string.IsNullOrWhiteSpace(imageUrl) && string.IsNullOrWhiteSpace(contributorImage))
    {
        return Results.NotFound(new { error = "No images available." });
    }

    return Results.Ok(new { image = imageUrl, contributorImage });
})
.WithName("HardcoverBookImage");

app.MapPost("/hardcover/want-to-read/check", async (HardcoverWantRequest request, IHttpClientFactory factory, IConfiguration config) =>
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
    var isInt = int.TryParse(request.BookId, out var intId);
    if (!isInt)
    {
        return Results.BadRequest(new { error = "bookId must be an integer Hardcover book id." });
    }

    var queries = new[]
    {
        @"
            query CheckWantToReadInt($bookId: Int!) {
              me {
                user_book(where: { book_id: { _eq: $bookId }, status_id: { _eq: 1 } }) {
                  book_id
                  status_id
                  date_added
                }
              }
            }",
        @"
            query CheckWantToReadIntPlural($bookId: Int!) {
              me {
                user_books(where: { book_id: { _eq: $bookId }, status_id: { _eq: 1 } }) {
                  book_id
                  status_id
                  date_added
                }
              }
            }"
    };

    var variables = new { bookId = (object)intId };

    HttpResponseMessage? lastResponse = null;
    foreach (var query in queries)
    {
        var response = await client.PostAsJsonAsync("", new { query, variables });
        lastResponse = response;

        if (!response.IsSuccessStatusCode)
        {
            continue;
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        if (HasGraphQlErrors(doc))
        {
            continue;
        }

        var exists = ExistsInUserBooks(doc);
        return Results.Ok(new { exists, raw = doc });
    }

    return await ForwardHardcoverError(lastResponse!, "want-to-read:check");
});

app.MapPost("/hardcover/want-to-read/status", async (HardcoverWantRequest request, IHttpClientFactory factory, IConfiguration config, HttpContext httpContext, UserSettingsStore settings) =>
{
    var incomingBookId =
    request.BookId ??
    (request.BookIdAlt?.ToString());
    if (string.IsNullOrWhiteSpace(incomingBookId))
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

    bool isInt = int.TryParse(incomingBookId, out var intId);
    if (!isInt)
    {
        return Results.BadRequest(new { error = "bookId must be an integer Hardcover book id." });
    }

    int ResolveStatusId()
    {
        if (request.StatusId.HasValue) return request.StatusId.Value;
        if (request.WantToRead.HasValue)
        {
            return request.WantToRead.Value ? 1 : 6;
        }

        return 1;
    }

    var desiredStatusId = ResolveStatusId();
    if (desiredStatusId is not (1 or 6))
    {
        return Results.BadRequest(new { error = "statusId must be 1 (want) or 6 (remove)." });
    }

    var client = factory.CreateClient("hardcover");
    var debug = string.Equals(httpContext.Request.Query["debug"], "1", StringComparison.OrdinalIgnoreCase);

    async Task<IResult> HandleAddAsync()
    {
        var attempts = new[]
        {
            new
            {
                Name = "insert_user_book_one",
                Query = @"
                    mutation addBook($bookId: Int!, $statusId: Int!) {
                      insert_user_book_one(object: { book_id: $bookId, status_id: $statusId }) { id }
                    }"
            },
            new
            {
                Name = "insert_user_book",
                Query = @"
                    mutation addBook($bookId: Int!, $statusId: Int!) {
                      insert_user_book(object: { book_id: $bookId, status_id: $statusId }) { id }
                    }"
            }
        };

        var mutationVariables = new { bookId = (object)intId, statusId = desiredStatusId };

        foreach (var attempt in attempts)
        {
            using var response = await client.PostAsJsonAsync("", new
            {
                query = attempt.Query,
                variables = mutationVariables
            });

            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            string? errors = null;
            try
            {
                doc = JsonDocument.Parse(body);
                errors = HasGraphQlErrors(doc) ? ExtractGraphQlErrors(doc) : null;
            }
            catch
            {
                // ignore parse errors; fall back to raw body
            }

            if (response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(errors))
            {
                doc?.Dispose();
                return Results.Ok(new { status = "ok", method = attempt.Name, statusId = desiredStatusId });
            }

            if (IsUniqueConflict(errors))
            {
                doc?.Dispose();
                return Results.Ok(new { status = "ok", method = attempt.Name, note = "already_exists", statusId = desiredStatusId });
            }

            if (!ReferenceEquals(attempts[^1], attempt))
            {
                doc?.Dispose();
                continue;
            }

            doc?.Dispose();
            return Results.Problem(
                title: "Hardcover addBook failed",
                detail: string.IsNullOrWhiteSpace(errors) ? body : errors,
                statusCode: response.IsSuccessStatusCode ? StatusCodes.Status400BadRequest : (int)response.StatusCode,
                extensions: debug
                    ? new Dictionary<string, object?>
                    {
                        ["mutation"] = attempt.Name,
                        ["variables"] = mutationVariables,
                        ["raw"] = body,
                        ["errors"] = errors
                    }
                    : null);
        }

        return Results.Problem(
            title: "Hardcover addBook failed",
            detail: "Unknown error",
            statusCode: StatusCodes.Status400BadRequest);
    }

    async Task<IResult> HandleRemoveAsync()
    {
        // Prefer removing from configured Hardcover list if available.
        async Task<IResult?> TryRemoveFromListAsync()
        {
        var listIdRaw = settings.HardcoverListId;
        int? configuredListId = int.TryParse(listIdRaw, out var parsedListId) ? parsedListId : null;

            const string findQuery = @"
                query FindListBook($listId: Int!, $bookId: Int!) {
                  list_books(
                    where: { list_id: { _eq: $listId }, book_id: { _eq: $bookId } }
                    limit: 5
                    order_by: { created_at: desc }
                  ) {
                    id
                    list_id
                  }
                }";

            const string findAnyQuery = @"
                query FindAnyListBook($bookId: Int!) {
                  list_books(
                    where: { book_id: { _eq: $bookId } }
                    limit: 5
                    order_by: { created_at: desc }
                  ) {
                    id
                    list_id
                  }
                }";

            const string deleteMutation = @"
                mutation RemoveBookFromList($id: Int!) {
                  delete_list_book(id: $id) {
                    id
                    list_id
                    list { name books_count }
                  }
                }";

            async Task<(int? listBookId, int? listId)> QueryForListBookAsync(object payload)
            {
                using var response = await client.PostAsJsonAsync("", payload);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    return (null, null);
                }

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (HasGraphQlErrors(doc)) return (null, null);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("list_books", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                            {
                                var lbId = idEl.GetInt32();
                                int? listId = null;
                                if (item.TryGetProperty("list_id", out var listEl) && listEl.ValueKind == JsonValueKind.Number)
                                {
                                    listId = listEl.GetInt32();
                                }
                                return (lbId, listId);
                            }
                        }
                    }
                }
                catch
                {
                    return (null, null);
                }

                return (null, null);
            }

            (int? listBookId, int? listId) = configuredListId is not null
                ? await QueryForListBookAsync(new
                {
                    query = findQuery,
                    variables = new { listId = configuredListId.Value, bookId = (object)intId }
                })
                : (null, null);

            if (listBookId is null)
            {
                (listBookId, listId) = await QueryForListBookAsync(new
                {
                    query = findAnyQuery,
                    variables = new { bookId = (object)intId }
                });
            }

            if (listBookId is null)
            {
                return Results.Ok(new
                {
                    status = "ok",
                    method = "delete_list_book",
                    note = "not_found",
                    statusId = desiredStatusId
                });
            }

            var deletePayload = new
            {
                query = deleteMutation,
                variables = new { id = listBookId.Value }
            };

            using var deleteResponse = await client.PostAsJsonAsync("", deletePayload);
            var deleteBody = await deleteResponse.Content.ReadAsStringAsync();

            JsonDocument? deleteDoc = null;
            string? deleteErrors = null;
            try
            {
                deleteDoc = JsonDocument.Parse(deleteBody);
                deleteErrors = HasGraphQlErrors(deleteDoc) ? ExtractGraphQlErrors(deleteDoc) : null;
            }
            catch
            {
                // ignore parse
            }

            if (deleteResponse.IsSuccessStatusCode && string.IsNullOrWhiteSpace(deleteErrors))
            {
                deleteDoc?.Dispose();
                return Results.Ok(new
                {
                    status = "ok",
                    method = "delete_list_book",
                    statusId = desiredStatusId,
                    listId,
                    listBookId
                });
            }

            deleteDoc?.Dispose();
            return null;
        }

        var listResult = await TryRemoveFromListAsync();
        if (listResult is not null)
        {
            return listResult;
        }

        var attempts = new[]
        {
            new
            {
                Name = "delete_user_book",
                RequiresStatus = false,
                Query = @"
                    mutation removeBook($bookId: Int!) {
                      delete_user_book(where: { book_id: { _eq: $bookId } }) {
                        affected_rows
                      }
                    }"
            },
            new
            {
                Name = "delete_user_books",
                RequiresStatus = false,
                Query = @"
                    mutation removeBook($bookId: Int!) {
                      delete_user_books(where: { book_id: { _eq: $bookId } }) {
                        affected_rows
                      }
                    }"
            },
            new
            {
                Name = "update_user_book",
                RequiresStatus = true,
                Query = @"
                    mutation removeBook($bookId: Int!, $statusId: Int!) {
                      update_user_book(
                        where: { book_id: { _eq: $bookId } },
                        _set: { status_id: $statusId }
                      ) {
                        affected_rows
                      }
                    }"
            },
            new
            {
                Name = "update_user_books",
                RequiresStatus = true,
                Query = @"
                    mutation removeBook($bookId: Int!, $statusId: Int!) {
                      update_user_books(
                        where: { book_id: { _eq: $bookId } },
                        _set: { status_id: $statusId }
                      ) {
                        affected_rows
                      }
                    }"
            }
        };

        foreach (var attempt in attempts)
        {
            object variables = attempt.RequiresStatus
                ? new { bookId = (object)intId, statusId = desiredStatusId }
                : new { bookId = (object)intId };

            using var response = await client.PostAsJsonAsync("", new
            {
                query = attempt.Query,
                variables
            });

            var body = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            string? errors = null;
            try
            {
                doc = JsonDocument.Parse(body);
                errors = HasGraphQlErrors(doc) ? ExtractGraphQlErrors(doc) : null;
            }
            catch
            {
                // ignore parse errors
            }

            if (response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(errors))
            {
                var affectedRows = 0;
                if (doc is not null &&
                    doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty(attempt.Name, out var node) &&
                    node.TryGetProperty("affected_rows", out var affectedEl) &&
                    affectedEl.ValueKind == JsonValueKind.Number)
                {
                    affectedRows = affectedEl.GetInt32();
                }

                doc?.Dispose();
                return Results.Ok(new
                {
                    status = "ok",
                    method = attempt.Name,
                    statusId = desiredStatusId,
                    removed = affectedRows
                });
            }

            if (!ReferenceEquals(attempts[^1], attempt))
            {
                doc?.Dispose();
                continue;
            }

            var detail = string.IsNullOrWhiteSpace(errors) ? body : errors;
            doc?.Dispose();
            return Results.Problem(
                title: "Hardcover removeBook failed",
                detail: detail,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: debug
                    ? new Dictionary<string, object?>
                    {
                        ["mutation"] = attempt.Query,
                        ["variables"] = variables,
                        ["raw"] = body,
                        ["errors"] = errors
                    }
                    : null);
        }

        return Results.Problem(
            title: "Hardcover removeBook failed",
            detail: "Unknown error",
            statusCode: StatusCodes.Status400BadRequest);
    }

    return desiredStatusId == 6
        ? await HandleRemoveAsync()
        : await HandleAddAsync();
})
.WithName("SetHardcoverWantStatus");

// Simple add-to-want endpoint for front-end fixes (always uses status_id = 1)
app.MapPost("/hardcover/add-book", async (HttpRequest http, IHttpClientFactory factory, IConfiguration config) =>
{
    string? incomingBookId = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(http.Body);
        var root = doc.RootElement;
        if (root.TryGetProperty("bookId", out var idEl))
        {
            incomingBookId = idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString(),
                JsonValueKind.Number => idEl.GetRawText(),
                _ => null
            };
        }
        if (string.IsNullOrWhiteSpace(incomingBookId) &&
            root.TryGetProperty("book_id", out var altEl))
        {
            incomingBookId = altEl.ValueKind switch
            {
                JsonValueKind.String => altEl.GetString(),
                JsonValueKind.Number => altEl.GetRawText(),
                _ => null
            };
        }
    }
    catch
    {
        // fall through with null
    }

    if (string.IsNullOrWhiteSpace(incomingBookId))
    {
        return Results.Ok(new { status = "error", error = "bookId is required" });
    }

    if (!IsHardcoverConfigured(config))
    {
        return Results.Ok(new
        {
            status = "error",
            error = "Hardcover API key not configured. Set Hardcover:ApiKey or env var Hardcover__ApiKey."
        });
    }

    if (!int.TryParse(incomingBookId, out var intId))
    {
        return Results.Ok(new { status = "error", error = "bookId must be an integer Hardcover book id." });
    }

    var client = factory.CreateClient("hardcover");
    var attempts = new[]
    {
        new
        {
            Name = "insert_user_book_one",
            Payload = (object)new
            {
                query = @"
                    mutation addBook($bookId: Int!) {
                      insert_user_book_one(object: { book_id: $bookId, status_id: 1 }) {
                        id
                      }
                    }",
                variables = new { bookId = intId }
            }
        },
        new
        {
            Name = "insert_user_book",
            Payload = (object)new
            {
                query = @"
                    mutation addBook($bookId: Int!) {
                      insert_user_book(object: { book_id: $bookId, status_id: 1 }) {
                        id
                      }
                    }",
                variables = new { bookId = intId }
            }
        }
    };

    foreach (var attempt in attempts)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("", attempt.Payload);
            var body = await response.Content.ReadAsStringAsync();

            string? graphErrors = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (HasGraphQlErrors(doc))
                {
                    graphErrors = ExtractGraphQlErrors(doc);
                }
            }
            catch
            {
                // ignore parse issues
            }

            // Treat unique/duplicate as success
            if (IsUniqueConflict(graphErrors))
            {
                return Results.Ok(new { status = "ok", method = attempt.Name, note = "already_exists" });
            }

            if (!response.IsSuccessStatusCode || !string.IsNullOrWhiteSpace(graphErrors))
            {
                // Try next mutation if available
                if (!ReferenceEquals(attempts[^1], attempt))
                {
                    continue;
                }

                var errorMsg = !string.IsNullOrWhiteSpace(graphErrors) ? graphErrors : body;
                return Results.Ok(new
                {
                    status = "error",
                    error = $"HTTP {(int)response.StatusCode}: {errorMsg}",
                    mutation = attempt.Name
                });
            }

            return Results.Ok(new { status = "ok", method = attempt.Name });
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(attempts[^1], attempt))
            {
                return Results.Ok(new { status = "error", error = ex.Message, mutation = attempt.Name });
            }
        }
    }

    return Results.Ok(new { status = "error", error = "Unknown error" });
})
.WithName("AddHardcoverBook");

app.Run();

record OpenLibrarySearchResponse(
    [property: JsonPropertyName("docs")] IEnumerable<OpenLibraryDoc> Docs,
    [property: JsonPropertyName("numFound")] int? NumFound = null);

record OpenLibraryAuthorSearchResponse(
    [property: JsonPropertyName("docs")] IEnumerable<OpenLibraryAuthorDoc> Docs,
    [property: JsonPropertyName("numFound")] int? NumFound = null);

record HardcoverListRequest(string? ListId);
record OpenLibraryAuthorDoc
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("top_work")]
    public string? TopWork { get; init; }

    [JsonPropertyName("work_count")]
    public int? WorkCount { get; init; }

    [JsonPropertyName("birth_date")]
    public string? BirthDate { get; init; }

    [JsonPropertyName("death_date")]
    public string? DeathDate { get; init; }

    [JsonPropertyName("top_subjects")]
    public IEnumerable<string>? TopSubjects { get; init; }
}

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
    [property: JsonPropertyName("bookId")] string? BookId,
    [property: JsonPropertyName("book_id")] int? BookIdAlt,
    [property: JsonPropertyName("status_id")] int? StatusId,
    [property: JsonPropertyName("wantToRead")] bool? WantToRead);

record HardcoverResolveRequest(
    [property: JsonPropertyName("isbn")] string? Isbn,
    [property: JsonPropertyName("bookId")] int? BookId);

record CalibrePathRequest(
    [property: JsonPropertyName("path")] string? Path);

record HardcoverWantCacheRequest(
    [property: JsonPropertyName("books")] List<JsonElement>? Books);

class RecAccumulatorSummary
{
    public RecAccumulatorSummary(JsonElement book)
    {
        Book = book;
    }

    public JsonElement Book { get; }
    public int Count { get; set; }
    public List<HardcoverListReason> Reasons { get; } = new();
}
