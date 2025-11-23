using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Bookworm.Data;

public sealed record HardcoverListReason(
    string? ListId,
    string? ListName,
    string? ListSlug,
    string? OwnerName,
    int? CalibreId,
    string? CalibreTitle);

public sealed record HardcoverListRecommendation(
    JsonElement Book,
    int Occurrences,
    IReadOnlyList<HardcoverListReason> Reasons);

public sealed record HardcoverNeighbor(string? Key, string? Title, double? Rating, JsonElement BookWithSource);

public sealed record HardcoverListHit(string? ListId, string? ListName, string? ListSlug, string? OwnerName, IReadOnlyList<HardcoverNeighbor> Neighbors);

public sealed record HardcoverBookListResult(
    int CalibreId,
    string CalibreTitle,
    string? HardcoverId,
    string? HardcoverTitle,
    IReadOnlyList<string> BaseGenres,
    IReadOnlyList<HardcoverListHit> Lists,
    IReadOnlyList<HardcoverListRecommendation> Recommendations);

public sealed record HardcoverListResponse(
    int InspectedCalibreBooks,
    int MatchedCalibreBooks,
    int UniqueRecommendations,
    IReadOnlyList<HardcoverListRecommendation> Recommendations,
    IReadOnlyList<CalibreSourceMatch> Matches,
    IReadOnlyList<HardcoverListStep> Steps);

public sealed record CalibreSourceMatch(
    int CalibreId,
    string Title,
    string? Isbn,
    string? HardcoverBookId,
    string? HardcoverTitle);

public sealed record HardcoverListStep(
    int CalibreId,
    string Title,
    string? Isbn,
    bool MatchedHardcover,
    string? HardcoverBookId,
    string? HardcoverTitle,
    int ListsChecked,
    int RecommendationsAdded);

/// <summary>
/// Queries Hardcover people lists for a given set of Calibre titles and aggregates neighboring books.
/// </summary>
public class HardcoverListService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HardcoverListService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private const string BookFields = @"
        id
        title
        slug
        rating
        ratings_count
        users_count
        cached_contributors
        cached_tags
        contributions(where: { contributable_type: { _eq: ""Book"" } }) {
          contributable_type
          author { id name slug }
        }
        image { url }
        default_physical_edition { isbn_13 isbn_10 }
        default_ebook_edition { isbn_13 isbn_10 }
    ";

    public HardcoverListService(IHttpClientFactory httpClientFactory, ILogger<HardcoverListService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HardcoverListResponse> GetRecommendationsAsync(
        IEnumerable<CalibreMirrorBook> calibreBooks,
        int takeCalibreBooks,
        int listsPerBook,
        int itemsPerList,
        double? minRating,
        int delayMs,
        CancellationToken cancellationToken,
        Func<HardcoverListStep, Task>? onStep = null)
    {
        var client = _httpClientFactory.CreateClient("hardcover");
        var booksList = calibreBooks.ToList();
        var inspected = 0;
        var matched = 0;
        var matches = new List<CalibreSourceMatch>();
        var aggregator = new Dictionary<string, RecAccumulator>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<HardcoverListStep>();

        var booksToProcess = takeCalibreBooks <= 0
            ? booksList
            : booksList.Take(Math.Max(1, takeCalibreBooks));

        foreach (var book in booksToProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspected++;

            var isbn = NormalizeIsbn(book.Isbn);
            var foundBook = await FindHardcoverBookAsync(client, isbn, book.Title, cancellationToken);
            if (foundBook is null)
            {
                matches.Add(new CalibreSourceMatch(book.Id, book.Title, isbn, null, null));
                continue;
            }

            matched++;
            matches.Add(new CalibreSourceMatch(book.Id, book.Title, isbn, foundBook.HardcoverId, foundBook.HardcoverTitle));

            var lists = await FetchListsContainingBookAsync(client, foundBook.HardcoverId, listsPerBook, itemsPerList, cancellationToken);
            var recsAdded = 0;
            foreach (var list in lists)
            {
                foreach (var neighbor in list.Neighbors)
                {
                    if (minRating.HasValue && neighbor.Rating.HasValue && neighbor.Rating < minRating)
                    {
                        continue;
                    }

                    var key = neighbor.Key ?? neighbor.Title;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!aggregator.TryGetValue(key, out var acc))
                    {
                        acc = new RecAccumulator(neighbor.BookWithSource.Clone());
                        aggregator[key] = acc;
                    }

                    acc.Increment(new HardcoverListReason(
                        list.ListId,
                        list.ListName,
                        list.ListSlug,
                        list.OwnerName,
                        book.Id,
                        book.Title));
                    recsAdded++;
                }
            }

            steps.Add(new HardcoverListStep(
                book.Id,
                book.Title,
                isbn,
                true,
                foundBook.HardcoverId,
                foundBook.HardcoverTitle,
                lists.Count,
                recsAdded));

            if (onStep is not null)
            {
                await onStep(steps[^1]);
            }

            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        foreach (var miss in matches.Where(m => m.HardcoverBookId is null))
        {
            steps.Add(new HardcoverListStep(
                miss.CalibreId,
                miss.Title,
                miss.Isbn,
                false,
                null,
                null,
                0,
                0));
            if (onStep is not null)
            {
                await onStep(steps[^1]);
            }
        }

        var recommendations = aggregator.Values
            .OrderByDescending(x => x.Count)
            .Select(x => new HardcoverListRecommendation(
                x.Book,
                x.Count,
                x.Reasons.ToArray()))
            .ToList();

        return new HardcoverListResponse(
            inspected,
            matched,
            recommendations.Count,
            recommendations,
            matches,
            steps);
    }

    public async Task<HardcoverBookListResult> GetListsForBookAsync(
        CalibreMirrorBook book,
        int listsPerBook,
        int itemsPerList,
        double? minRating,
        CancellationToken cancellationToken)
    {
        var isbn = NormalizeIsbn(book.Isbn);
        var client = _httpClientFactory.CreateClient("hardcover");
        var foundBook = await FindHardcoverBookAsync(client, isbn, book.Title, cancellationToken);
        if (foundBook is null)
        {
            return new HardcoverBookListResult(
                book.Id,
                book.Title,
                null,
                null,
                Array.Empty<string>(),
                Array.Empty<HardcoverListHit>(),
                Array.Empty<HardcoverListRecommendation>());
        }

        var baseGenres = await FetchBaseGenresAsync(client, foundBook.HardcoverId, cancellationToken);
        var lists = await FetchListsContainingBookAsync(
            client,
            foundBook.HardcoverId,
            listsPerBook,
            itemsPerList,
            cancellationToken);

        var aggregator = new Dictionary<string, RecAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            foreach (var neighbor in list.Neighbors)
            {
                if (minRating.HasValue && neighbor.Rating.HasValue && neighbor.Rating < minRating)
                {
                    continue;
                }

                var key = neighbor.Key ?? neighbor.Title;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!aggregator.TryGetValue(key, out var acc))
                {
                    acc = new RecAccumulator(neighbor.BookWithSource.Clone());
                    aggregator[key] = acc;
                }

                acc.Increment(new HardcoverListReason(
                    list.ListId,
                    list.ListName,
                    list.ListSlug,
                    list.OwnerName,
                    book.Id,
                    book.Title));
            }
        }

        var recommendations = aggregator.Values
            .OrderByDescending(x => x.Count)
            .Select(x => new HardcoverListRecommendation(
                x.Book,
                x.Count,
                x.Reasons.ToArray()))
            .ToList();

        return new HardcoverBookListResult(
            book.Id,
            book.Title,
            foundBook.HardcoverId,
            foundBook.HardcoverTitle,
            baseGenres,
            lists,
            recommendations);
    }

    private async Task<HardcoverBookResult?> FindHardcoverBookAsync(HttpClient client, string? isbn, string title, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(isbn))
        {
            const string byIsbnQuery = @"
                query BookByIsbn($isbn: String!) {
                  books(
                    where: {
                      _or: [
                        { default_physical_edition: { isbn_13: { _eq: $isbn } } },
                        { default_physical_edition: { isbn_10: { _eq: $isbn } } },
                        { default_ebook_edition: { isbn_13: { _eq: $isbn } } },
                        { default_ebook_edition: { isbn_10: { _eq: $isbn } } }
                      ]
                    }
                    limit: 5
                    order_by: { users_count: desc }
                  ) {
                    " + BookFields + @"
                  }
                }";

            var doc = await PostGraphQLAsync(client, byIsbnQuery, new { isbn }, cancellationToken);
            var match = ExtractFirstBook(doc);
            if (match is not null)
            {
                return match;
            }
        }

        const string byTitleQuery = @"
            query BookByTitle($title: String!, $fuzzy: String!) {
              books(
                where: {
                  _or: [
                    { title: { _ilike: $fuzzy } },
                    { title: { _ilike: $title } }
                  ]
                }
                limit: 5
                order_by: { users_count: desc }
              ) {
                " + BookFields + @"
              }
            }";

        var fuzzy = $"%{title}%";
        var byTitleDoc = await PostGraphQLAsync(client, byTitleQuery, new { title, fuzzy }, cancellationToken);
        return ExtractFirstBook(byTitleDoc);
    }

    private async Task<List<HardcoverListHit>> FetchListsContainingBookAsync(
        HttpClient client,
        string hardcoverBookId,
        int listsPerBook,
        int itemsPerList,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(hardcoverBookId, out var numericId))
        {
            return new List<HardcoverListHit>();
        }

        const string listsQuery = @"
            query ListsWithBook($bookId: Int!, $listLimit: Int!, $itemLimit: Int!) {
              list_books(
                where: { book_id: { _eq: $bookId } }
                limit: $listLimit
                order_by: { created_at: desc }
              ) {
                list {
                  id
                  name
                  slug
                  user { name username }
                  list_books(
                    where: { book_id: { _neq: $bookId } }
                    limit: $itemLimit
                  ) {
                    book { " + BookFields + @" }
                  }
                }
              }
            }";

        var doc = await PostGraphQLAsync(client, listsQuery, new
        {
            bookId = numericId,
            listLimit = listsPerBook,
            itemLimit = itemsPerList
        }, cancellationToken);

        if (doc is null)
        {
            return new List<HardcoverListHit>();
        }

        var results = new List<HardcoverListHit>();
        try
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("list_books", out var listItems) ||
                listItems.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in listItems.EnumerateArray())
            {
                if (!item.TryGetProperty("list", out var list))
                {
                    continue;
                }

                var listId = list.TryGetProperty("id", out var idEl) ? GetStringValue(idEl) : null;
                var listName = list.TryGetProperty("name", out var nameEl) ? GetStringValue(nameEl) : null;
                var listSlug = list.TryGetProperty("slug", out var slugEl) ? GetStringValue(slugEl) : null;
                var owner = list.TryGetProperty("user", out var userEl)
                    ? userEl.TryGetProperty("name", out var nameProp) && GetStringValue(nameProp) is { } nameStr
                        ? nameStr
                        : userEl.TryGetProperty("username", out var userName) ? GetStringValue(userName) : null
                    : null;

                var neighbors = new List<HardcoverNeighbor>();
                if (list.TryGetProperty("list_books", out var neighborItems) && neighborItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var neighbor in neighborItems.EnumerateArray())
                    {
                        if (!neighbor.TryGetProperty("book", out var bookEl))
                        {
                            continue;
                        }

                        var bookWithSource = AddSource(bookEl);
                        neighbors.Add(new HardcoverNeighbor(
                            Key: ExtractKey(bookEl),
                            Title: bookEl.TryGetProperty("title", out var t) ? GetStringValue(t) : null,
                            Rating: TryGetDouble(bookEl, "rating"),
                            BookWithSource: bookWithSource));
                    }
                }

                results.Add(new HardcoverListHit(listId, listName, listSlug, owner, neighbors));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse list results from Hardcover.");
        }

        return results;
    }

    private async Task<JsonDocument?> PostGraphQLAsync(HttpClient client, string query, object variables, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.PostAsJsonAsync("", new { query, variables }, _jsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"Hardcover API returned {(int)response.StatusCode} {response.StatusCode}: {body}";
                _logger.LogWarning(message);
                throw new HttpRequestException(message);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                var errText = errors.ToString();
                _logger.LogWarning("Hardcover GraphQL returned errors: {Errors}", errText);
                throw new HttpRequestException($"Hardcover GraphQL errors: {errText}");
            }
            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardcover GraphQL request failed.");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> FetchBaseGenresAsync(HttpClient client, string hardcoverId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(hardcoverId, out var numericId))
        {
            return Array.Empty<string>();
        }

        const string tagsQuery = @"
            query CachedTags($bookId: Int!) {
              books(
                where: { id: { _eq: $bookId } }
                limit: 1
              ) {
                cached_tags
              }
            }";

        var doc = await PostGraphQLAsync(client, tagsQuery, new { bookId = numericId }, cancellationToken);
        if (doc is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("books", out var books) ||
                books.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var first = books.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined || first.ValueKind == JsonValueKind.Null)
            {
                return Array.Empty<string>();
            }

            if (!first.TryGetProperty("cached_tags", out var tagsElement))
            {
                return Array.Empty<string>();
            }

            var names = ExtractGenresFromCachedTags(tagsElement);
            return names.Count == 0 ? Array.Empty<string>() : names;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse cached_tags genres for Hardcover book {HardcoverId}", hardcoverId);
            return Array.Empty<string>();
        }
    }

    private static HardcoverBookResult? ExtractFirstBook(JsonDocument? doc)
    {
        if (doc is null)
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("books", out var books) ||
            books.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = books.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var id = first.TryGetProperty("id", out var idEl) ? GetStringValue(idEl) : null;
        var title = first.TryGetProperty("title", out var titleEl) ? GetStringValue(titleEl) : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var withSource = AddSource(first);
        return new HardcoverBookResult(id, title, withSource);
    }

    private static JsonElement AddSource(JsonElement book)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(book.GetRawText());
        }
        catch
        {
            node = new JsonObject();
        }

        if (node is null)
        {
            node = new JsonObject();
        }

        node["source"] = "hardcover";
        var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Replace("-", "").Replace(" ", "");
    }

    private static string? ExtractKey(JsonElement book)
    {
        if (book.TryGetProperty("id", out var id) && GetStringValue(id) is { } idStr)
        {
            return idStr;
        }

        if (book.TryGetProperty("slug", out var slug) && GetStringValue(slug) is { } slugStr)
        {
            return slugStr;
        }

        return book.TryGetProperty("title", out var title) ? GetStringValue(title) : null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop)) return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
        {
            return d;
        }

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetStringValue(JsonElement element)
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

    private static IReadOnlyList<string> ExtractGenresFromCachedTags(JsonElement tagsElement)
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

    private static string? TryResolveGenreName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? GetNested(JsonElement el, string property)
        {
            return el.TryGetProperty(property, out var nested)
                ? GetStringValue(nested)
                : null;
        }

        var direct = GetNested(element, "name") ?? GetNested(element, "label");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var tag = GetNested(element, "tag");
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        var tagSlug = GetNested(element, "tagSlug");
        if (!string.IsNullOrWhiteSpace(tagSlug))
        {
            return tagSlug;
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

    private sealed class RecAccumulator
    {
        public RecAccumulator(JsonElement book)
        {
            Book = book;
        }

        public JsonElement Book { get; }
        public int Count { get; private set; }
        public List<HardcoverListReason> Reasons { get; } = new();

        public void Increment(HardcoverListReason reason)
        {
            Count++;
            Reasons.Add(reason);
        }
    }

    private sealed record HardcoverBookResult(string HardcoverId, string? HardcoverTitle, JsonElement BookWithSource);

}
