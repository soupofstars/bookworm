using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record BookshelfStats(int Count, DateTime? LastUpdatedAt);

public class BookshelfRepository
{
    private const string TableName = "bookworm_bookshelf_books";
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public BookshelfRepository(string connectionString)
    {
        _connectionString = NormalizeConnectionString(connectionString);
        EnsureSchema();
    }

    private static string NormalizeConnectionString(string raw)
    {
        var builder = new SqliteConnectionStringBuilder(raw);
        var dataSource = builder.DataSource;
        if (!Path.IsPathRooted(dataSource))
        {
            dataSource = Path.Combine(AppContext.BaseDirectory, dataSource);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
        builder.DataSource = dataSource;
        return builder.ToString();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                authors_json TEXT,
                isbn TEXT,
                rating REAL,
                added_at TEXT,
                published_at TEXT,
                path TEXT,
                has_cover INTEGER NOT NULL DEFAULT 0,
                formats_json TEXT,
                tags_json TEXT,
                publisher TEXT,
                series TEXT,
                file_size_mb REAL,
                description TEXT,
                cover_url TEXT,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task ReplaceAllAsync(IEnumerable<CalibreMirrorBook> books, CancellationToken cancellationToken = default)
    {
        var list = books?.ToList() ?? new List<CalibreMirrorBook>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM {TableName};";
        await delete.ExecuteNonQueryAsync(cancellationToken);

        var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"""
            INSERT INTO {TableName} (
                id, title, authors_json, isbn, rating, added_at, published_at, path,
                has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                description, cover_url, updated_at
            ) VALUES (
                @id,@title,@authors,@isbn,@rating,@added,@published,@path,
                @hasCover,@formats,@tags,@publisher,@series,@size,@description,@coverUrl,@updated
            );
            """;
        var pId = insert.Parameters.Add("@id", SqliteType.Integer);
        var pTitle = insert.Parameters.Add("@title", SqliteType.Text);
        var pAuthors = insert.Parameters.Add("@authors", SqliteType.Text);
        var pIsbn = insert.Parameters.Add("@isbn", SqliteType.Text);
        var pRating = insert.Parameters.Add("@rating", SqliteType.Real);
        var pAdded = insert.Parameters.Add("@added", SqliteType.Text);
        var pPublished = insert.Parameters.Add("@published", SqliteType.Text);
        var pPath = insert.Parameters.Add("@path", SqliteType.Text);
        var pHasCover = insert.Parameters.Add("@hasCover", SqliteType.Integer);
        var pFormats = insert.Parameters.Add("@formats", SqliteType.Text);
        var pTags = insert.Parameters.Add("@tags", SqliteType.Text);
        var pPublisher = insert.Parameters.Add("@publisher", SqliteType.Text);
        var pSeries = insert.Parameters.Add("@series", SqliteType.Text);
        var pSize = insert.Parameters.Add("@size", SqliteType.Real);
        var pDescription = insert.Parameters.Add("@description", SqliteType.Text);
        var pCoverUrl = insert.Parameters.Add("@coverUrl", SqliteType.Text);
        var pUpdated = insert.Parameters.Add("@updated", SqliteType.Text);

        foreach (var book in list)
        {
            pId.Value = book.Id;
            pTitle.Value = book.Title;
            pAuthors.Value = SerializeArray(book.AuthorNames);
            pIsbn.Value = (object?)book.Isbn ?? DBNull.Value;
            pRating.Value = (object?)book.Rating ?? DBNull.Value;
            pAdded.Value = book.AddedAt?.ToString("o") ?? (object)DBNull.Value;
            pPublished.Value = book.PublishedAt?.ToString("o") ?? (object)DBNull.Value;
            pPath.Value = (object?)book.Path ?? DBNull.Value;
            pHasCover.Value = book.HasCover ? 1 : 0;
            pFormats.Value = SerializeArray(book.Formats);
            pTags.Value = SerializeArray(book.Tags);
            pPublisher.Value = (object?)book.Publisher ?? DBNull.Value;
            pSeries.Value = (object?)book.Series ?? DBNull.Value;
            pSize.Value = (object?)book.FileSizeMb ?? DBNull.Value;
            pDescription.Value = (object?)book.Description ?? DBNull.Value;
            pCoverUrl.Value = (object?)book.CoverUrl ?? DBNull.Value;
            pUpdated.Value = DateTime.UtcNow.ToString("o");
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CalibreMirrorBook>> GetBooksAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        var unlimited = take <= 0;
        cmd.CommandText = unlimited
            ? $"""
                SELECT id, title, authors_json, isbn, rating, added_at, published_at, path,
                       has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                       description, cover_url
                FROM {TableName}
                ORDER BY COALESCE(added_at, updated_at) DESC;
                """
            : $"""
                SELECT id, title, authors_json, isbn, rating, added_at, published_at, path,
                       has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                       description, cover_url
                FROM {TableName}
                ORDER BY COALESCE(added_at, updated_at) DESC
                LIMIT @take;
                """;
        if (!unlimited)
        {
            cmd.Parameters.AddWithValue("@take", take);
        }

        var results = new List<CalibreMirrorBook>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var authors = DeserializeArray(reader.IsDBNull(2) ? null : reader.GetString(2));
            var formats = DeserializeArray(reader.IsDBNull(9) ? null : reader.GetString(9));
            var tags = DeserializeArray(reader.IsDBNull(10) ? null : reader.GetString(10));
            results.Add(new CalibreMirrorBook(
                reader.GetInt32(0),
                reader.GetString(1),
                authors,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                ParseDate(reader, 5),
                ParseDate(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(8) && reader.GetBoolean(8),
                formats,
                tags,
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetDouble(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)
            ));
        }

        return results;
    }

    public async Task<BookshelfStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt,
                   MAX(updated_at) AS max_updated
            FROM {TableName};
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            DateTime? lastUpdated = null;
            if (!reader.IsDBNull(1))
            {
                var raw = reader.GetString(1);
                if (DateTime.TryParse(raw, out var parsed))
                {
                    lastUpdated = parsed;
                }
            }

            return new BookshelfStats(count, lastUpdated);
        }

        return new BookshelfStats(0, null);
    }

    private static string SerializeArray(string[] values)
        => JsonSerializer.Serialize(values ?? Array.Empty<string>());

    private string[] DeserializeArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value, _jsonOptions) ?? Array.Empty<string>();
        }
        catch
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    private static DateTime? ParseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetValue(ordinal);
        if (raw is DateTime dt) return dt;
        if (raw is string str && DateTime.TryParse(str, out var parsed)) return parsed;
        if (raw is long ticks) return DateTimeOffset.FromUnixTimeSeconds(ticks).UtcDateTime;
        return null;
    }
}
