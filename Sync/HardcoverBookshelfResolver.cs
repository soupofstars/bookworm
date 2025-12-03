using System.Net.Http.Json;
using System.Text.Json;
using Bookworm.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public record HardcoverBookshelfSyncResult(
    int Attempted,
    int Resolved,
    int MissingIsbn,
    int TitleFallback,
    int AlreadyMapped,
    int PushedToList);

public class HardcoverBookshelfResolver
{
    private readonly BookshelfRepository _bookshelf;
    private readonly HardcoverBookshelfMapRepository _mapRepo;
    private readonly UserSettingsStore _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<HardcoverBookshelfResolver> _logger;

    public HardcoverBookshelfResolver(
        BookshelfRepository bookshelf,
        HardcoverBookshelfMapRepository mapRepo,
        UserSettingsStore settings,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ActivityLogService activityLog,
        ILogger<HardcoverBookshelfResolver> logger)
    {
        _bookshelf = bookshelf;
        _mapRepo = mapRepo;
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _activityLog = activityLog;
        _logger = logger;
    }

    public async Task<HardcoverBookshelfSyncResult> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!IsHardcoverConfigured())
        {
            _logger.LogInformation("Skipping Hardcover bookshelf resolve: Hardcover API key not configured.");
            await _activityLog.InfoAsync("Hardcover bookshelf", "Skipped bookshelf resolve: Hardcover API key not configured.");
            return new HardcoverBookshelfSyncResult(0, 0, 0, 0, 0, 0);
        }

        var books = await _bookshelf.GetBooksAsync(0, cancellationToken);
        var existing = await _mapRepo.GetAllAsync(cancellationToken);
        var map = existing.ToDictionary(e => e.CalibreId, e => e);

        var attempted = 0;
        var resolved = 0;
        var missingIsbn = 0;
        var titleFallback = 0;
        var alreadyMapped = 0;
        var newlyResolvedIds = new List<int>();

        var client = _httpClientFactory.CreateClient("hardcover");

        foreach (var book in books)
        {
            if (book is null) continue;

            map.TryGetValue(book.Id, out var current);
            var existingId = current?.HardcoverId;
            if (!string.IsNullOrWhiteSpace(existingId) && !IsNumericId(existingId))
            {
                existingId = null;
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {
                alreadyMapped++;
                await _mapRepo.UpsertAsync(book.Id, existingId, DateTime.UtcNow, cancellationToken);
                await _bookshelf.UpdateHardcoverIdAsync(book.Id, existingId, cancellationToken);
                continue;
            }

            attempted++;

            string? hardcoverId = null;
            var isbn = NormalizeIsbn(book.Isbn);
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                hardcoverId = await ResolveByIsbnAsync(client, isbn!, book.Title, cancellationToken);
            }
            else
            {
                missingIsbn++;
            }

            if (hardcoverId is null && !string.IsNullOrWhiteSpace(book.Title))
            {
                titleFallback++;
                hardcoverId = await ResolveByTitleAsync(client, book.Title, cancellationToken);
            }

            await _mapRepo.UpsertAsync(book.Id, hardcoverId, DateTime.UtcNow, cancellationToken);
            if (!string.IsNullOrWhiteSpace(hardcoverId))
            {
                resolved++;
                await _bookshelf.UpdateHardcoverIdAsync(book.Id, hardcoverId, cancellationToken);
                if (int.TryParse(hardcoverId, out var parsedId))
                {
                    newlyResolvedIds.Add(parsedId);
                }
            }
        }

        var pushed = 0;
        if (newlyResolvedIds.Count > 0)
        {
            pushed = await PushToHardcoverListAsync(newlyResolvedIds, cancellationToken);
        }

        _logger.LogInformation("Hardcover bookshelf resolve: attempted {Attempted}, resolved {Resolved}, missingIsbn {MissingIsbn}, titleFallback {TitleFallback}, alreadyMapped {AlreadyMapped}, pushed {Pushed}",
            attempted, resolved, missingIsbn, titleFallback, alreadyMapped, pushed);
        await _activityLog.SuccessAsync("Hardcover bookshelf", "Hardcover bookshelf sync completed.", new
        {
            attempted,
            resolved,
            missingIsbn,
            titleFallback,
            alreadyMapped,
            pushed
        });

        return new HardcoverBookshelfSyncResult(attempted, resolved, missingIsbn, titleFallback, alreadyMapped, pushed);
    }

    private async Task<string?> ResolveByIsbnAsync(HttpClient client, string isbn, string? targetTitle, CancellationToken cancellationToken)
    {
        const string searchQuery = @"
            query SearchByIsbn($query: String!, $perPage: Int!, $page: Int!) {
              search(
                  query: $query,
                  query_type: ""isbns"",
                  per_page: $perPage,
                  page: $page
              ) {
                  results
              }
            }";

        var variables = new { query = isbn, perPage = 5, page = 1 };
        using var response = await client.PostAsJsonAsync("", new { query = searchQuery, variables }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
        if (doc is null || HasGraphQlErrors(doc.RootElement))
        {
            return null;
        }

        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("search", out var searchEl) &&
            searchEl.ValueKind == JsonValueKind.Object &&
            searchEl.TryGetProperty("results", out var resultsEl) &&
            resultsEl.ValueKind == JsonValueKind.Object &&
            resultsEl.TryGetProperty("hits", out var hitsEl) &&
            hitsEl.ValueKind == JsonValueKind.Array &&
            hitsEl.GetArrayLength() > 0)
        {
            foreach (var hit in hitsEl.EnumerateArray())
            {
                if (hit.ValueKind != JsonValueKind.Object) continue;
                if (!hit.TryGetProperty("document", out var docEl) || docEl.ValueKind != JsonValueKind.Object) continue;

                var titleMatches = TitlesMatch(docEl, targetTitle);
                if (!titleMatches) continue;

                if (docEl.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var id))
                    {
                        return id.ToString();
                    }
                    if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var parsed))
                    {
                        return parsed.ToString();
                    }
                }
            }
        }

        return null;
    }

    private async Task<string?> ResolveByTitleAsync(HttpClient client, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        const string titleQuery = @"
            query BookByTitle($title: String!) {
              books(
                where: { title: { _ilike: $title } }
                limit: 5
              ) {
                id
                title
                slug
              }
            }";

        var pattern = $"%{title}%";
        using var response = await client.PostAsJsonAsync("", new { query = titleQuery, variables = new { title = pattern } }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
        if (doc is null || HasGraphQlErrors(doc.RootElement))
        {
            return null;
        }

        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("books", out var booksEl) &&
            booksEl.ValueKind == JsonValueKind.Array &&
            booksEl.GetArrayLength() > 0)
        {
            var targetNormalized = NormalizeTitle(title);
            foreach (var b in booksEl.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (b.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                {
                    var normalized = NormalizeTitle(titleEl.GetString());
                    if (!string.IsNullOrWhiteSpace(targetNormalized) && string.Equals(normalized, targetNormalized, StringComparison.OrdinalIgnoreCase))
                    {
                        if (b.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var id))
                        {
                            return id.ToString();
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool TitlesMatch(JsonElement docEl, string? targetTitle)
    {
        if (string.IsNullOrWhiteSpace(targetTitle)) return true;
        var normalizedTarget = NormalizeTitle(targetTitle);
        if (string.IsNullOrWhiteSpace(normalizedTarget)) return false;

        if (docEl.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
        {
            var normalizedDoc = NormalizeTitle(titleEl.GetString());
            if (!string.IsNullOrWhiteSpace(normalizedDoc) && string.Equals(normalizedDoc, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (docEl.TryGetProperty("alternative_titles", out var altEl) && altEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var alt in altEl.EnumerateArray())
            {
                if (alt.ValueKind != JsonValueKind.String) continue;
                var normalizedAlt = NormalizeTitle(alt.GetString());
                if (!string.IsNullOrWhiteSpace(normalizedAlt) && string.Equals(normalizedAlt, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        return cleaned;
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        Span<char> buffer = stackalloc char[title.Length];
        var idx = 0;
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[idx++] = char.ToLowerInvariant(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                buffer[idx++] = ' ';
            }
        }
        var normalized = new string(buffer.Slice(0, idx)).Trim();
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsNumericId(string? value) => int.TryParse(value, out _);

    private static bool HasGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)) return false;
        return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
    }

    private bool IsHardcoverConfigured()
    {
        var apiKey = _config["Hardcover:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    private async Task<int> PushToHardcoverListAsync(IEnumerable<int> hardcoverIds, CancellationToken cancellationToken)
    {
        var listIdRaw = _settings.HardcoverListId;
        if (string.IsNullOrWhiteSpace(listIdRaw) || !int.TryParse(listIdRaw, out var listId))
        {
            _logger.LogInformation("Skipping push to Hardcover list: list id not configured.");
            await _activityLog.WarnAsync("Hardcover bookshelf", "Skipped push to Hardcover list: list id not configured.");
            return 0;
        }

        var apiKey = _config["Hardcover:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("Skipping push to Hardcover list: API key not configured.");
            await _activityLog.WarnAsync("Hardcover bookshelf", "Skipped push to Hardcover list: Hardcover API key not configured.");
            return 0;
        }

        var client = _httpClientFactory.CreateClient("hardcover");
        const string mutation = @"
            mutation addBook($bookId: Int!, $listId: Int!) {
              insert_list_book(object: { book_id: $bookId, list_id: $listId }) {
                id
              }
            }";

        var pushed = 0;
        var failures = new List<object>();
        foreach (var hid in hardcoverIds.Distinct())
        {
            var payload = new
            {
                query = mutation,
                variables = new { bookId = hid, listId }
            };

            var attempt = 0;
            var delayMs = 1500;
            var maxAttempts = 3;
            try
            {
                while (attempt < maxAttempts)
                {
                    attempt++;
                    using var response = await client.PostAsJsonAsync("", payload, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);

                    if ((int)response.StatusCode == 429 && attempt < maxAttempts)
                    {
                        _logger.LogWarning("Hardcover list push throttled for book {BookId}; attempt {Attempt} of {MaxAttempts}. Retrying after {DelayMs}ms.", hid, attempt, maxAttempts, delayMs);
                        await Task.Delay(delayMs, cancellationToken);
                        delayMs *= 2;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to add book {BookId} to Hardcover list {ListId}: {Status} {Body}", hid, listId, response.StatusCode, body);
                        failures.Add(new { bookId = hid, status = (int)response.StatusCode, body });
                        break;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (HasGraphQlErrors(doc.RootElement))
                        {
                            _logger.LogWarning("Hardcover list add returned errors for book {BookId}: {Errors}", hid, ExtractGraphQlErrors(doc));
                            failures.Add(new { bookId = hid, errors = ExtractGraphQlErrors(doc) });
                            break;
                        }
                    }
                    catch
                    {
                        // ignore parse issues
                    }

                    pushed++;
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception adding book {BookId} to Hardcover list {ListId}", hid, listId);
                failures.Add(new { bookId = hid, error = ex.Message });
            }
        }

        if (failures.Count > 0)
        {
            await _activityLog.WarnAsync("Hardcover bookshelf", "Push to Hardcover list had failures.", new { listId = listIdRaw, failures });
        }
        else if (pushed > 0)
        {
            await _activityLog.SuccessAsync("Hardcover bookshelf", "Pushed newly resolved books to Hardcover list.", new { listId = listIdRaw, pushed, ids = hardcoverIds.Distinct().ToArray() });
        }

        return pushed;
    }

    private static string? ExtractGraphQlErrors(JsonDocument? doc)
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
}
