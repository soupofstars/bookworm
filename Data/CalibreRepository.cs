using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record CalibreBook(
    int Id,
    string Title,
    string[] AuthorNames,
    string? Isbn,
    double? Rating,
    DateTime? AddedAt,
    DateTime? PublishedAt,
    string? Path,
    bool HasCover,
    string[] Formats,
    string? Publisher,
    string? Series,
    double? FileSizeMb)
{
    public string? Description { get; init; }
}

public class CalibreRepository
{
    private readonly UserSettingsStore _settings;

    public bool IsConfigured
    {
        get
        {
            if (!TryGetDatabasePath(out var path) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                var fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }
    }

    public CalibreRepository(UserSettingsStore settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<CalibreBook>> GetRecentBooksAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        if (!TryGetDatabasePath(out var databasePath) || string.IsNullOrWhiteSpace(databasePath))
        {
            return Array.Empty<CalibreBook>();
        }

        var normalizedPath = Path.GetFullPath(databasePath);
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException($"Calibre metadata database not found at {normalizedPath}");
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = normalizedPath };
        var connectionString = builder.ToString();

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var hasRatingColumn = await TableHasColumnAsync(conn, "books", "rating", cancellationToken);
        var ratingSelect = hasRatingColumn ? "b.rating" : "NULL";
        var hasCommentsTable = await TableHasColumnAsync(conn, "comments", "value", cancellationToken);
        var commentsSelect = hasCommentsTable ? "COALESCE(c.value, '')" : "''";
        var commentsJoin = hasCommentsTable ? "LEFT JOIN comments c ON c.book = b.id" : string.Empty;
        var hasDataSize = await TableHasColumnAsync(conn, "data", "size", cancellationToken);
        var hasDataUncompressed = await TableHasColumnAsync(conn, "data", "uncompressed_size", cancellationToken);
        var sizeExpression = hasDataUncompressed
            ? (hasDataSize ? "COALESCE(d.uncompressed_size, d.size)" : "d.uncompressed_size")
            : (hasDataSize ? "d.size" : "0");
        var hasIdentifiers = await TableHasColumnAsync(conn, "identifiers", "val", cancellationToken);
        var identifierSelect = hasIdentifiers
            ? "GROUP_CONCAT(DISTINCT i.val)"
            : "''";
        var identifierJoin = hasIdentifiers
            ? "LEFT JOIN identifiers i ON i.book = b.id AND LOWER(i.type) IN ('isbn','isbn10','isbn-10','isbn13','isbn-13','isbn_10','isbn_13')"
            : string.Empty;
        var unlimited = take <= 0;
        var sql = $"""
            SELECT
                b.id,
                b.title,
                b.isbn,
                {ratingSelect} AS rating_value,
                b.timestamp,
                b.pubdate,
                b.path,
                b.has_cover,
                {commentsSelect} AS comments_value,
                COALESCE(GROUP_CONCAT(DISTINCT a.name), '') AS authors,
                COALESCE(GROUP_CONCAT(DISTINCT d.format), '') AS formats,
                COALESCE(p.name, '') AS publisher_name,
                COALESCE(s.name, '') AS series_name,
                COALESCE(SUM({sizeExpression}), 0) AS total_bytes,
                {identifierSelect} AS identifier_values
            FROM books b
            LEFT JOIN books_authors_link bal ON bal.book = b.id
            LEFT JOIN authors a ON a.id = bal.author
            LEFT JOIN data d ON d.book = b.id
            {commentsJoin}
            LEFT JOIN books_publishers_link bpl ON bpl.book = b.id
            LEFT JOIN publishers p ON p.id = bpl.publisher
            LEFT JOIN books_series_link bsl ON bsl.book = b.id
            LEFT JOIN series s ON s.id = bsl.series
            {identifierJoin}
            GROUP BY b.id
            ORDER BY b.timestamp DESC
            {(unlimited ? string.Empty : "LIMIT @take;")}
            """;

        var results = new List<CalibreBook>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!unlimited)
        {
            cmd.Parameters.AddWithValue("@take", take);
        }
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            var title = reader.GetString(1);
            var inlineIsbn = reader.IsDBNull(2) ? null : reader.GetString(2);
            double? rating = null;
            if (!reader.IsDBNull(3))
            {
                rating = reader.GetDouble(3);
            }

            DateTime? addedAt = ParseSqliteDate(reader, 4);
            DateTime? pubDate = ParseSqliteDate(reader, 5);
            var path = reader.IsDBNull(6) ? null : reader.GetString(6);
            var hasCover = !reader.IsDBNull(7) && reader.GetBoolean(7);
            var comments = reader.IsDBNull(8) ? null : reader.GetString(8);
            var authors = SplitConcat(reader.IsDBNull(9) ? null : reader.GetString(9));
            var formats = SplitConcat(reader.IsDBNull(10) ? null : reader.GetString(10));
            var publisher = reader.IsDBNull(11) ? null : reader.GetString(11);
            var series = reader.IsDBNull(12) ? null : reader.GetString(12);
            if (string.IsNullOrWhiteSpace(publisher)) publisher = null;
            if (string.IsNullOrWhiteSpace(series)) series = null;
            var identifierValues = reader.IsDBNull(14) ? null : reader.GetString(14);
            double? sizeMb = null;
            if (!reader.IsDBNull(13))
            {
                var bytesObj = reader.GetValue(13);
                var bytes = bytesObj switch
                {
                    double d => d,
                    float f => f,
                    long l => (double)l,
                    int i => i,
                    decimal m => (double)m,
                    _ => 0d
                };
                if (bytes > 0)
                {
                    sizeMb = Math.Round(bytes / (1024 * 1024), 2, MidpointRounding.AwayFromZero);
                }
            }

            var isbn = ChooseIsbn(inlineIsbn, identifierValues);

            results.Add(new CalibreBook(
                id,
                title,
                authors,
                string.IsNullOrWhiteSpace(isbn) ? null : isbn,
                rating,
                addedAt,
                pubDate,
                path,
                hasCover,
                formats,
                publisher,
                series,
                sizeMb)
            {
                Description = comments
            });
        }

        return results;
    }

    private bool TryGetDatabasePath(out string? path)
    {
        path = _settings.CalibreDatabasePath;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static DateTime? ParseSqliteDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetValue(ordinal);
        if (raw is DateTime dt)
        {
            return dt;
        }

        if (raw is string text && DateTime.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (raw is long ticks)
        {
            return DateTimeOffset.FromUnixTimeSeconds(ticks).UtcDateTime;
        }

        return null;
    }

    private static string? ChooseIsbn(string? inlineIsbn, string? identifierValues)
    {
        var candidates = new List<string>();

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return;
            candidates.Add(trimmed);
        }

        AddCandidate(inlineIsbn);
        foreach (var value in SplitConcat(identifierValues))
        {
            AddCandidate(value);
        }

        string Normalize(string raw)
        {
            var buffer = new char[raw.Length];
            var idx = 0;
            foreach (var ch in raw)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[idx++] = ch;
                }
            }

            return idx == 0 ? string.Empty : new string(buffer, 0, idx);
        }

        string? first = null;
        string? best13 = null;
        string? best10 = null;

        foreach (var candidate in candidates)
        {
            first ??= candidate;
            var cleaned = Normalize(candidate);
            if (cleaned.Length >= 13 && cleaned.Length <= 16)
            {
                best13 ??= cleaned;
            }
            else if (cleaned.Length == 10 || cleaned.Length == 11)
            {
                best10 ??= cleaned;
            }
        }

        return best13 ?? best10 ?? first;
    }

    private static string[] SplitConcat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        // Support both the default comma separator and the custom "|||"
        // used in earlier versions of this query.
        var separators = value.Contains("|||", StringComparison.Ordinal)
            ? new[] { "|||", "," }
            : new[] { "," };

        return value
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<bool> TableHasColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        var safeTable = table.Replace("'", "''");
        var sql = $"PRAGMA table_info('{safeTable}');";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.FieldCount > 2)
            {
                var name = reader.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

}
