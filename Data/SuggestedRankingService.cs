using System.Text.Json;

namespace Bookworm.Data;

public record SuggestedRankedResult(
    int SuggestedId,
    JsonElement Book,
    IReadOnlyList<string> BaseGenres,
    IReadOnlyList<HardcoverListReason> Reasons,
    int MatchScore,
    int AuthorMatches, // 1 if any author overlaps with Calibre
    int GenreMatches,  // number of overlapping genre words
    int TagMatches,    // shared meaningful title words count
    IReadOnlyList<string> TitleBonusWords,
    string? SourceKey,
    bool AlreadyInCalibre,
    int? CalibreId,
    bool MatchedByIsbn,
    IReadOnlyDictionary<string, int> DebugBreakdown);

public class SuggestedRankingService
{
    private readonly CalibreMirrorRepository _calibreRepo;
    private readonly HardcoverListCacheRepository _cacheRepo;
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "in", "for", "and", "it", "an"
    };

    public SuggestedRankingService(CalibreMirrorRepository calibreRepo, HardcoverListCacheRepository cacheRepo)
    {
        _calibreRepo = calibreRepo;
        _cacheRepo = cacheRepo;
    }

    public async Task<IReadOnlyList<SuggestedRankedResult>> RankAsync(
        IReadOnlyList<SuggestedResponse> suggested,
        CancellationToken cancellationToken = default)
    {
        var books = await _calibreRepo.GetBooksAsync(0, cancellationToken);
        var cacheEntries = await _cacheRepo.GetAllAsync(cancellationToken);

        // Aggregate library signals for cheap deterministic scoring
        var authorSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titleKeywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var calibreTitleWordSets = new List<HashSet<string>>();
        var isbnToCalibre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var titleToCalibre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var baseGenreWordSet = BuildCacheBaseGenreWords(cacheEntries);

        foreach (var book in books)
        {
            foreach (var author in book.AuthorNames ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(author))
                {
                    authorSet.Add(author.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(book.Isbn))
            {
                var norm = NormalizeIsbn(book.Isbn);
                if (!string.IsNullOrWhiteSpace(norm))
                {
                    isbnToCalibre[norm] = book.Id;
                }
            }

            if (!string.IsNullOrWhiteSpace(book.Title))
            {
                var normalizedTitle = NormalizeTitle(book.Title);
                if (!string.IsNullOrWhiteSpace(normalizedTitle))
                {
                    titleToCalibre[normalizedTitle] = book.Id;
                }

                var keywords = GetTitleKeywords(book.Title);
                foreach (var kw in keywords)
                {
                    titleKeywordSet.Add(kw);
                }
                calibreTitleWordSets.Add(new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase));
            }
        }

        var results = new List<SuggestedRankedResult>(suggested.Count);
        foreach (var entry in suggested)
        {
            var authors = ExtractAuthors(entry.Book);
            var hardcoverGenres = ExtractTags(entry.Book);
            var baseGenres = entry.BaseGenres ?? Array.Empty<string>();
            var isbns = ExtractIsbns(entry.Book);
            var title = ExtractTitle(entry.Book);
            var titleKeywords = GetTitleKeywords(title);
            var sharedTitleWords = titleKeywords.Count(kw => titleKeywordSet.Contains(kw));
            var (hasTwoWordTitleOverlap, bonusWords) = GetTwoWordTitleOverlap(titleKeywords, calibreTitleWordSets);
            var titleDoubleBonus = hasTwoWordTitleOverlap ? 1 : 0;
            var authorMatched = authors.Any(authorSet.Contains);
            var combinedGenreWords = CombineGenreWords(hardcoverGenres);
            var (genreOverlapCount, genreOverlapWords) = EvaluateGenreOverlap(combinedGenreWords, baseGenreWordSet);

            int? calibreId = null;
            var matchedByIsbn = false;
            var debug = new Dictionary<string, int>
            {
                ["sharedTitleWords"] = sharedTitleWords,
                ["authors"] = authorMatched ? 1 : 0,
                ["genres"] = genreOverlapCount,
                ["genreWordOverlap"] = genreOverlapCount
            };

            foreach (var isbn in isbns)
            {
                if (isbnToCalibre.TryGetValue(isbn, out var id))
                {
                    calibreId = id;
                    matchedByIsbn = true;
                    break;
                }
            }

            if (calibreId is null && !string.IsNullOrWhiteSpace(title))
            {
                var normTitle = NormalizeTitle(title);
                if (!string.IsNullOrWhiteSpace(normTitle) && titleToCalibre.TryGetValue(normTitle, out var id))
                {
                    calibreId = id;
                }
            }

            var titleScore = sharedTitleWords > 0 ? 5 : 0;
            var authorAdjustment = authorMatched ? 3 : -1;
            var isbnAdjustment = isbns.Count > 0 ? 0 : -1;
            var genreAdjustment = genreOverlapCount;
            var score = Math.Clamp(titleScore + authorAdjustment + isbnAdjustment + genreAdjustment + titleDoubleBonus, 1, 20);
            debug["titleScore"] = titleScore;
            debug["authorAdjustment"] = authorAdjustment;
            debug["isbnAdjustment"] = isbnAdjustment;
            debug["genreAdjustment"] = genreAdjustment;
            debug["titleTwoWordBonus"] = titleDoubleBonus;
            if (genreOverlapWords.Count > 0)
            {
                debug["genreWordsMatched"] = genreOverlapWords.Count;
            }
            debug["finalScore"] = score;

            results.Add(new SuggestedRankedResult(
                entry.Id,
                entry.Book,
                baseGenres,
                entry.Reasons ?? Array.Empty<HardcoverListReason>(),
                score,
                authorMatched ? 1 : 0,
                genreOverlapCount,
                sharedTitleWords,
                bonusWords,
                entry.SourceKey,
                calibreId is not null,
                calibreId,
                matchedByIsbn,
                debug));
        }

        return results
            .OrderByDescending(r => r.MatchScore)
            .ThenByDescending(r => r.AlreadyInCalibre)
            .ThenByDescending(r => r.AuthorMatches)
            .ThenByDescending(r => r.GenreMatches)
            .ThenByDescending(r => r.TagMatches)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractAuthors(JsonElement book)
    {
        var authors = new List<string>();

        if (book.TryGetProperty("cached_contributors", out var cached) && cached.ValueKind == JsonValueKind.Array)
        {
            foreach (var contributor in cached.EnumerateArray())
            {
                if (contributor.ValueKind == JsonValueKind.Object &&
                    contributor.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        authors.Add(name.Trim());
                    }
                }
            }
        }

        if (authors.Count == 0 && book.TryGetProperty("contributions", out var contribs) && contribs.ValueKind == JsonValueKind.Array)
        {
            foreach (var contribution in contribs.EnumerateArray())
            {
                if (contribution.ValueKind != JsonValueKind.Object) continue;
                if (contribution.TryGetProperty("author", out var authorObj) &&
                    authorObj.ValueKind == JsonValueKind.Object &&
                    authorObj.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        authors.Add(name.Trim());
                    }
                }
            }
        }

        return authors.Count == 0 ? Array.Empty<string>() : authors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ExtractTags(JsonElement book)
    {
        if (!book.TryGetProperty("cached_tags", out var tagsElement))
        {
            return Array.Empty<string>();
        }

        var tags = ExtractGenresFromCachedTags(tagsElement);
        return tags.Count == 0 ? Array.Empty<string>() : tags;
    }

    private static IReadOnlyList<string> ExtractIsbns(JsonElement book)
    {
        var isbns = new List<string>();
        void Add(string? raw)
        {
            var norm = NormalizeIsbn(raw);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                isbns.Add(norm);
            }
        }

        if (book.TryGetProperty("default_physical_edition", out var physical) && physical.ValueKind == JsonValueKind.Object)
        {
            if (physical.TryGetProperty("isbn_13", out var i13) && i13.ValueKind == JsonValueKind.String) Add(i13.GetString());
            if (physical.TryGetProperty("isbn_10", out var i10) && i10.ValueKind == JsonValueKind.String) Add(i10.GetString());
        }
        if (book.TryGetProperty("default_ebook_edition", out var ebook) && ebook.ValueKind == JsonValueKind.Object)
        {
            if (ebook.TryGetProperty("isbn_13", out var e13) && e13.ValueKind == JsonValueKind.String) Add(e13.GetString());
            if (ebook.TryGetProperty("isbn_10", out var e10) && e10.ValueKind == JsonValueKind.String) Add(e10.GetString());
        }
        if (book.TryGetProperty("isbn13", out var isbn13) && isbn13.ValueKind == JsonValueKind.String) Add(isbn13.GetString());
        if (book.TryGetProperty("isbn_13", out var isbn13b) && isbn13b.ValueKind == JsonValueKind.String) Add(isbn13b.GetString());
        if (book.TryGetProperty("isbn10", out var isbn10) && isbn10.ValueKind == JsonValueKind.String) Add(isbn10.GetString());
        if (book.TryGetProperty("isbn_10", out var isbn10b) && isbn10b.ValueKind == JsonValueKind.String) Add(isbn10b.GetString());

        return isbns.Count == 0 ? Array.Empty<string>() : isbns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Replace("-", "").Replace(" ", "");
    }

    private static string? NormalizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? ExtractTitle(JsonElement book)
    {
        if (book.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
        {
            return t.GetString();
        }
        return null;
    }

    private static IReadOnlyList<string> GetTitleKeywords(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<string>();
        var normalized = title.ToLowerInvariant();
        var parts = new List<string>();
        var current = new List<char>();
        void Flush()
        {
            if (current.Count == 0) return;
            var word = new string(current.ToArray());
            if (word.Length >= 3 && !Stopwords.Contains(word))
            {
                parts.Add(word);
            }
            current.Clear();
        }

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Add(ch);
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return parts.Count == 0 ? Array.Empty<string>() : parts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (bool match, IReadOnlyList<string> overlapWords) GetTwoWordTitleOverlap(
        IReadOnlyList<string> titleKeywords,
        IReadOnlyList<HashSet<string>> calibreTitleWordSets)
    {
        if (titleKeywords.Count < 2)
        {
            return (false, Array.Empty<string>());
        }

        var best = Array.Empty<string>();
        foreach (var set in calibreTitleWordSets)
        {
            var overlap = titleKeywords.Where(set.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (overlap.Length >= 2 && overlap.Length > best.Length)
            {
                best = overlap;
            }
        }

        return best.Length >= 2
            ? (true, best)
            : (false, Array.Empty<string>());
    }

    private static HashSet<string> CombineGenreWords(IReadOnlyList<string> genres)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres)
        {
            foreach (var word in GetTitleKeywords(genre))
            {
                words.Add(word);
            }
        }
        return words;
    }

    private static (int overlapCount, IReadOnlyList<string> overlapWords) EvaluateGenreOverlap(
        IReadOnlyCollection<string> hardcoverGenreWords,
        IReadOnlyCollection<string> cacheBaseGenreWords)
    {
        if (hardcoverGenreWords.Count == 0 || cacheBaseGenreWords.Count == 0)
        {
            return (0, Array.Empty<string>());
        }

        var overlap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in hardcoverGenreWords)
        {
            if (cacheBaseGenreWords.Contains(word))
            {
                overlap.Add(word);
            }
        }

        return (overlap.Count, overlap.Count == 0 ? Array.Empty<string>() : overlap.ToArray());
    }

    private static IReadOnlyCollection<string> BuildCacheBaseGenreWords(IReadOnlyList<HardcoverListCacheEntry> cacheEntries)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in cacheEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.BaseGenresJson))
            {
                continue;
            }

            try
            {
                var genres = JsonSerializer.Deserialize<string[]>(entry.BaseGenresJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                             ?? Array.Empty<string>();
                foreach (var word in CombineGenreWords(genres))
                {
                    words.Add(word);
                }
            }
            catch
            {
                // ignore malformed genre JSON
            }
        }

        return words;
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
}
