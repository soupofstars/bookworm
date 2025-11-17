using System.Net.Http.Json;
using System.Text.Json.Serialization;

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

// Fuzzy book search via OpenLibrary
app.MapGet("/search", async (string query, IHttpClientFactory factory) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query is required" });

    var client = factory.CreateClient("openlibrary");
    const string fields = "key,title,author_name,cover_i,edition_count,ratings_average,ratings_count,subject,edition_key,cover_edition_key";
    var response = await client.GetAsync($"/search.json?q={Uri.EscapeDataString(query)}&limit=20&fields={fields}");

    if (!response.IsSuccessStatusCode)
    {
        var errorText = await response.Content.ReadAsStringAsync();
        return Results.Problem($"OpenLibrary error: {response.StatusCode} - {errorText}");
    }

    var payload = await response.Content.ReadFromJsonAsync<OpenLibrarySearchResponse>()
                  ?? new OpenLibrarySearchResponse(Array.Empty<OpenLibraryDoc>());

    var books = payload.Docs
        .Where(doc => !string.IsNullOrWhiteSpace(doc.Title))
        .Take(20)
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
                cached_contributors
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
                    cached_contributors
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
}
