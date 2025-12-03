using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Bookworm.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public record CalibreSyncResult(
    bool Success,
    int Count,
    DateTime? Snapshot,
    string? Error,
    IReadOnlyList<int> NewBookIds,
    IReadOnlyList<int> RemovedBookIds);

public class CalibreSyncService
{
    private readonly CalibreRepository _source;
    private readonly CalibreMirrorRepository _mirror;
    private readonly BookshelfRepository _bookshelf;
    private readonly UserSettingsStore _settings;
    private readonly CalibreCoverService _covers;
    private readonly HardcoverListCacheRepository _listCache;
    private readonly HardcoverListService _listService;
    private readonly IConfiguration _config;
    private readonly SuggestedRepository _suggestedRepo;
    private readonly WantedRepository _wanted;
    private readonly HardcoverWantCacheRepository _hardcoverWantCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalibreSyncService> _logger;
    private readonly ActivityLogService _activityLog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CalibreSyncService(
        CalibreRepository source,
        CalibreMirrorRepository mirror,
        BookshelfRepository bookshelf,
        UserSettingsStore settings,
        CalibreCoverService covers,
        HardcoverListCacheRepository listCache,
        HardcoverListService listService,
        IConfiguration config,
        SuggestedRepository suggestedRepo,
        WantedRepository wanted,
        HardcoverWantCacheRepository hardcoverWantCache,
        IHttpClientFactory httpClientFactory,
        ILogger<CalibreSyncService> logger,
        ActivityLogService activityLog)
    {
        _source = source;
        _mirror = mirror;
        _bookshelf = bookshelf;
        _settings = settings;
        _covers = covers;
        _listCache = listCache;
        _listService = listService;
        _config = config;
        _suggestedRepo = suggestedRepo;
        _wanted = wanted;
        _hardcoverWantCache = hardcoverWantCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _activityLog = activityLog;
    }

    public async Task<CalibreSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!_source.IsConfigured)
        {
            return new CalibreSyncResult(false, 0, null, "Calibre path not configured.", Array.Empty<int>(), Array.Empty<int>());
        }

        if (!TryGetLibraryRoot(out var libraryRoot, out var metadataPath, out var error))
        {
            return new CalibreSyncResult(false, 0, null, error ?? "Calibre metadata not found.", Array.Empty<int>(), Array.Empty<int>());
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var books = await _source.GetRecentBooksAsync(0, cancellationToken);
            if (!books.Any())
            {
                var emptySnapshot = DateTime.UtcNow;
                var emptyReplaceResult = await _mirror.ReplaceAllAsync(Array.Empty<CalibreMirrorBook>(), metadataPath, emptySnapshot, cancellationToken);
                await _bookshelf.ReplaceAllAsync(Array.Empty<CalibreMirrorBook>(), cancellationToken);
                return new CalibreSyncResult(true, 0, emptySnapshot, null, emptyReplaceResult.NewIds, emptyReplaceResult.RemovedIds);
            }

            var mirrored = new List<CalibreMirrorBook>(books.Count);
            foreach (var book in books)
            {
                string? coverUrl = null;
                if (book.HasCover && !string.IsNullOrWhiteSpace(book.Path))
                {
                    try
                    {
                        coverUrl = await _covers.EnsureCoverAsync(book.Id, libraryRoot!, book.Path, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy cover for Calibre book {BookId}", book.Id);
                    }
                }

                mirrored.Add(new CalibreMirrorBook(
                    book.Id,
                    book.Title,
                    book.AuthorNames,
                    book.Isbn,
                    book.Rating,
                    book.AddedAt,
                    book.PublishedAt,
                    book.Path,
                    book.HasCover,
                    book.Formats,
                    Array.Empty<string>(),
                    book.Publisher,
                    book.Series,
                    book.FileSizeMb,
                    book.Description,
                    coverUrl,
                    null));
            }

            var snapshot = DateTime.UtcNow;
            var replaceResult = await _mirror.ReplaceAllAsync(mirrored, metadataPath, snapshot, cancellationToken);
            await _bookshelf.ReplaceAllAsync(mirrored, cancellationToken);
            try
            {
                await _listCache.SyncWithCalibreAsync(mirrored, cancellationToken);
                await ProcessPendingListCacheAsync(mirrored, cancellationToken);
                await CleanupWantedForNewBooksAsync(mirrored, replaceResult.NewIds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync Hardcover list cache with Calibre mirror.");
            }
            return new CalibreSyncResult(true, mirrored.Count, snapshot, null, replaceResult.NewIds, replaceResult.RemovedIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calibre sync failed.");
            return new CalibreSyncResult(false, 0, null, ex.Message, Array.Empty<int>(), Array.Empty<int>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsHardcoverConfigured()
    {
        var apiKey = _config["Hardcover:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    private bool IsPendingStatus(HardcoverListCacheEntry entry)
    {
        if (entry is null) return false;
        if (string.Equals(entry.Status, "pending", StringComparison.OrdinalIgnoreCase)) return true;
        return string.IsNullOrWhiteSpace(entry.Status) && entry.ListCount == 0 && entry.RecommendationCount == 0;
    }

    private async Task ProcessPendingListCacheAsync(IEnumerable<CalibreMirrorBook> mirrored, CancellationToken cancellationToken)
    {
        if (!IsHardcoverConfigured())
        {
            _logger.LogInformation("Skipping Hardcover list cache processing because Hardcover API key is not configured.");
            return;
        }

        var books = mirrored?.ToList() ?? new List<CalibreMirrorBook>();
        if (books.Count == 0) return;

        var cacheEntries = await _listCache.GetAllAsync(cancellationToken);
        var pendingIds = cacheEntries
            .Where(IsPendingStatus)
            .Select(e => e.CalibreId)
            .ToHashSet();

        if (pendingIds.Count == 0) return;

        var processed = 0;
        var matched = 0;
        var totalRecommendations = 0;
        foreach (var book in books.Where(b => pendingIds.Contains(b.Id)))
        {
            try
            {
                var result = await _listService.GetListsForBookAsync(
                    book,
                    listsPerBook: 12,
                    itemsPerList: 20,
                    minRating: null,
                    cancellationToken: cancellationToken);

                var listCount = result.Lists.Count;
                var recCount = result.Recommendations.Count;
                var matchedHardcover = !string.IsNullOrWhiteSpace(result.HardcoverId);

                await _listCache.UpsertAsync(new HardcoverListCacheEntry(
                    CalibreId: book.Id,
                    CalibreTitle: book.Title,
                    HardcoverId: result.HardcoverId,
                    HardcoverTitle: result.HardcoverTitle,
                    ListCount: listCount,
                    RecommendationCount: recCount,
                    LastCheckedUtc: DateTime.UtcNow,
                    Status: matchedHardcover ? "ok" : "not_matched",
                    BaseGenresJson: _listCache.Serialize(result.BaseGenres ?? Array.Empty<string>()),
                    ListsJson: _listCache.Serialize(result.Lists),
                    RecommendationsJson: _listCache.Serialize(result.Recommendations)
                ), cancellationToken);

                processed++;
                if (matchedHardcover) matched++;
                totalRecommendations += result.Recommendations.Count;

                if (result.Recommendations.Count > 0)
                {
                    var suggestedEntries = result.Recommendations.Select(r => new SuggestedEntry(
                        r.Book,
                        ExtractGenresFromBook(r.Book),
                        r.Reasons,
                        ExtractKeyFromBook(r.Book)
                    ));
                    await _suggestedRepo.UpsertMissingAsync(suggestedEntries, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process Hardcover lists for Calibre book {BookId}", book.Id);
                await _activityLog.WarnAsync("Suggested", "Failed to process Hardcover lists for Calibre book.", new { bookId = book.Id, error = ex.Message });
            }
        }

        if (processed > 0)
        {
            await _activityLog.InfoAsync("Suggested", "Processed Calibre books for Hardcover list checks.", new
            {
                processed,
                matchedHardcover = matched,
                recommendationsAdded = totalRecommendations
            });
        }
    }

    private static string? ExtractKeyFromBook(JsonElement book)
    {
        if (book.TryGetProperty("id", out var id) && TryGetString(id) is { } idStr) return idStr;
        if (book.TryGetProperty("slug", out var slug) && TryGetString(slug) is { } slugStr) return slugStr;
        return book.TryGetProperty("title", out var title) ? TryGetString(title) : null;
    }

    private static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? TryGetString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static IReadOnlyList<string> ExtractGenresFromBook(JsonElement book)
    {
        if (!book.TryGetProperty("cached_tags", out var tagsEl)) return Array.Empty<string>();
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return;
            if (seen.Add(trimmed))
            {
                ordered.Add(trimmed);
            }
        }

        if (tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    Add(nameEl.GetString());
                }
                else if (tag.ValueKind == JsonValueKind.String)
                {
                    Add(tag.GetString());
                }
            }
        }

        return ordered;
    }

    private async Task CleanupWantedForNewBooksAsync(
        IReadOnlyList<CalibreMirrorBook> mirrored,
        IReadOnlyList<int> newIds,
        CancellationToken cancellationToken)
    {
        if (newIds is null || newIds.Count == 0)
        {
            return;
        }

        var newIdSet = new HashSet<int>(newIds);
        var newBooks = mirrored?.Where(b => newIdSet.Contains(b.Id)).ToList() ?? new List<CalibreMirrorBook>();
        if (newBooks.Count == 0)
        {
            return;
        }

        var newIsbns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in newBooks)
        {
            var norm = NormalizeIsbn(book.Isbn);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                newIsbns.Add(norm);
            }
        }

        if (newIsbns.Count == 0)
        {
            return;
        }

        var wanted = await _wanted.GetAllAsync(cancellationToken);
        foreach (var entry in wanted)
        {
            var isbns = ExtractIsbns(entry.Book);
            if (!isbns.Any(newIsbns.Contains))
            {
                continue;
            }

            try
            {
                await RemoveFromHardcoverAsync(entry.Book, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove Hardcover want-to-read for book {Key}", entry.Key);
            }

            try
            {
                await _wanted.DeleteAsync(entry.Key, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove wanted book {Key} after Calibre sync", entry.Key);
            }

            try
            {
                await RemoveFromHardcoverCacheAsync(entry.Book, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove Hardcover cache entry for book {Key}", entry.Key);
            }
        }
    }

    private static IReadOnlyList<string> ExtractIsbns(JsonElement book)
    {
        var list = new List<string>();
        static void Add(JsonElement? el, ICollection<string> target)
        {
            if (el is null) return;
            if (el.Value.ValueKind == JsonValueKind.String)
            {
                var norm = NormalizeIsbn(el.Value.GetString());
                if (!string.IsNullOrWhiteSpace(norm))
                {
                    target.Add(norm);
                }
            }
        }

        if (book.ValueKind != JsonValueKind.Object) return Array.Empty<string>();

        if (book.TryGetProperty("isbn13", out var isbn13)) Add(isbn13, list);
        if (book.TryGetProperty("isbn_13", out var isbn13b)) Add(isbn13b, list);
        if (book.TryGetProperty("isbn10", out var isbn10)) Add(isbn10, list);
        if (book.TryGetProperty("isbn_10", out var isbn10b)) Add(isbn10b, list);

        if (book.TryGetProperty("default_physical_edition", out var phys) && phys.ValueKind == JsonValueKind.Object)
        {
            if (phys.TryGetProperty("isbn_13", out var p13)) Add(p13, list);
            if (phys.TryGetProperty("isbn_10", out var p10)) Add(p10, list);
        }
        if (book.TryGetProperty("default_ebook_edition", out var ebook) && ebook.ValueKind == JsonValueKind.Object)
        {
            if (ebook.TryGetProperty("isbn_13", out var e13)) Add(e13, list);
            if (ebook.TryGetProperty("isbn_10", out var e10)) Add(e10, list);
        }

        return list.Count == 0 ? Array.Empty<string>() : list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task RemoveFromHardcoverAsync(JsonElement book, CancellationToken cancellationToken)
    {
        if (!IsHardcoverConfigured())
        {
            return;
        }

        var bookId = ExtractHardcoverId(book);
        if (bookId is null)
        {
            foreach (var isbn in ExtractIsbns(book))
            {
                bookId = await ResolveHardcoverIdByIsbnAsync(isbn, cancellationToken);
                if (bookId is not null) break;
            }
        }

        if (bookId is null)
        {
            _logger.LogDebug("Skipping Hardcover removal: unable to resolve book id.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("hardcover");
            const string mutation = @"
                mutation addBook($bookId: Int!, $statusId: Int!) {
                  insert_user_book(object: { book_id: $bookId, status_id: $statusId }) {
                    id
                  }
                }";
            var payload = new
            {
                query = mutation,
                variables = new { bookId = bookId.Value, statusId = 6 }
            };
            using var response = await client.PostAsJsonAsync("", payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Hardcover remove want-to-read failed for book {BookId}: {Status} {Body}", bookId, response.StatusCode, body);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (HasGraphQlErrors(doc.RootElement))
                {
                    _logger.LogWarning("Hardcover remove want-to-read returned errors for book {BookId}: {Errors}", bookId, ExtractGraphQlErrors(doc));
                }
            }
            catch
            {
                // ignore parse errors
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Hardcover want-to-read for book id {BookId}", bookId);
        }
    }

    private static int? ExtractHardcoverId(JsonElement book)
    {
        string? GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        int? TryParse(string? raw)
        {
            return int.TryParse(raw, out var val) ? val : null;
        }

        var candidates = new[]
        {
            GetString(book, "id"),
            GetString(book, "book_id"),
            GetString(book, "bookId"),
            GetString(book, "hardcover_id"),
            GetString(book, "hardcoverId")
        };

        foreach (var candidate in candidates)
        {
            var parsed = TryParse(candidate);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private async Task RemoveFromHardcoverCacheAsync(JsonElement book, CancellationToken cancellationToken)
    {
        var bookId = ExtractHardcoverId(book);
        if (bookId is not null)
        {
            await _hardcoverWantCache.DeleteByHardcoverIdAsync(bookId.Value.ToString(), cancellationToken);
            return;
        }

        if (book.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String)
        {
            var slug = slugEl.GetString();
            if (!string.IsNullOrWhiteSpace(slug))
            {
                await _hardcoverWantCache.DeleteBySlugAsync(slug, cancellationToken);
            }
        }
    }

    private async Task<int?> ResolveHardcoverIdByIsbnAsync(string isbn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isbn)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient("hardcover");
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
                  }
                }";

            string AsIsbn10(string cleaned)
            {
                if (cleaned.Length == 10) return cleaned;
                if (cleaned.Length == 13) return cleaned[^10..];
                return cleaned;
            }

            var variables = new { isbn, isbn10 = AsIsbn10(isbn) };
            using var response = await client.PostAsJsonAsync("", new { query = isbnQuery, variables }, cancellationToken);
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
                var first = booksEl[0];
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.Number &&
                    idEl.TryGetInt32(out var id))
                {
                    return id;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve Hardcover id for ISBN {Isbn}", isbn);
        }

        return null;
    }

    private static bool HasGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)) return false;
        return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
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

    private bool TryGetLibraryRoot(out string? libraryRoot, out string? metadataPath, out string? error)
    {
        metadataPath = _settings.CalibreDatabasePath;
        if (string.IsNullOrWhiteSpace(metadataPath))
        {
            libraryRoot = null;
            error = "Calibre metadata path not configured.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(metadataPath);
            if (!File.Exists(fullPath))
            {
                libraryRoot = null;
                error = $"Metadata file not found at {fullPath}.";
                return false;
            }

            libraryRoot = Path.GetDirectoryName(fullPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            libraryRoot = null;
            error = ex.Message;
            return false;
        }
    }
}
