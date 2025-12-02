using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record HardcoverWantCacheEntry(
    string HardcoverId,
    string? Title,
    string? AuthorsJson,
    string? Isbn13,
    string? Isbn10,
    string? CoverUrl,
    string? BookJson,
    DateTime LastUpdatedUtc);

public record HardcoverWantCacheStats(int Count, DateTime? LastUpdatedUtc);

public class HardcoverWantCacheRepository
{
    private const string TableName = "hardcover_want_cache";
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public HardcoverWantCacheRepository(string connectionString)
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
                hardcover_id TEXT PRIMARY KEY,
                title TEXT,
                authors_json TEXT,
                isbn13 TEXT,
                isbn10 TEXT,
                cover_url TEXT,
                book_json TEXT,
                last_updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertAsync(IEnumerable<JsonElement> books, CancellationToken cancellationToken = default)
    {
        var list = books?.ToList() ?? new List<JsonElement>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {TableName} (
                hardcover_id, title, authors_json, isbn13, isbn10, cover_url, book_json, last_updated_utc
            ) VALUES (
                @id, @title, @authors, @isbn13, @isbn10, @cover, @book, @updated
            )
            ON CONFLICT(hardcover_id) DO UPDATE SET
                title = excluded.title,
                authors_json = excluded.authors_json,
                isbn13 = excluded.isbn13,
                isbn10 = excluded.isbn10,
                cover_url = excluded.cover_url,
                book_json = excluded.book_json,
                last_updated_utc = excluded.last_updated_utc;
            """;
        var pId = cmd.Parameters.Add("@id", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("@title", SqliteType.Text);
        var pAuthors = cmd.Parameters.Add("@authors", SqliteType.Text);
        var pIsbn13 = cmd.Parameters.Add("@isbn13", SqliteType.Text);
        var pIsbn10 = cmd.Parameters.Add("@isbn10", SqliteType.Text);
        var pCover = cmd.Parameters.Add("@cover", SqliteType.Text);
        var pBook = cmd.Parameters.Add("@book", SqliteType.Text);
        var pUpdated = cmd.Parameters.Add("@updated", SqliteType.Text);

        foreach (var book in list)
        {
            var id = ReadString(book, "id") ?? ReadString(book, "hardcover_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            pId.Value = id!;
            pTitle.Value = ReadString(book, "title") ?? (object)DBNull.Value;
            pAuthors.Value = SerializeArray(ReadStringArray(book, "authors"));
            pIsbn13.Value = ReadString(book, "isbn13") ?? (object)DBNull.Value;
            pIsbn10.Value = ReadString(book, "isbn10") ?? (object)DBNull.Value;
            pCover.Value = ReadString(book, "coverUrl") ?? ReadString(book, "image") ?? (object)DBNull.Value;
            pBook.Value = book.GetRawText();
            pUpdated.Value = DateTime.UtcNow.ToString("o");
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HardcoverWantCacheEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<HardcoverWantCacheEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT hardcover_id, title, authors_json, isbn13, isbn10, cover_url, book_json, last_updated_utc
            FROM {TableName}
            ORDER BY datetime(last_updated_utc) DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HardcoverWantCacheEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTime.TryParse(reader.GetString(7), out var dt) ? dt : DateTime.UtcNow
            ));
        }

        return results;
    }

    public async Task<HardcoverWantCacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt, MAX(last_updated_utc) AS max_updated
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

            return new HardcoverWantCacheStats(count, lastUpdated);
        }

        return new HardcoverWantCacheStats(0, null);
    }

    public async Task<int> DeleteByHardcoverIdAsync(string hardcoverId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE hardcover_id = @id;";
        cmd.Parameters.AddWithValue("@id", hardcoverId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {TableName}
            WHERE hardcover_id = @slug
               OR (json_valid(book_json) = 1 AND LOWER(json_extract(book_json, '$.slug')) = LOWER(@slug));
            """;
        cmd.Parameters.AddWithValue("@slug", slug);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SerializeArray(IEnumerable<string>? values)
    {
        if (values is null) return "[]";
        return JsonSerializer.Serialize(values.Where(s => !string.IsNullOrWhiteSpace(s)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var child)) return null;
        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.GetRawText(),
            _ => null
        };
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var child)) yield break;
        if (child.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in child.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var val = item.GetString();
                if (!string.IsNullOrWhiteSpace(val)) yield return val;
            }
        }
    }
}
