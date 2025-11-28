using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record CalibreMirrorBook(
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
    string[] Tags,
    string? Publisher,
    string? Series,
    double? FileSizeMb,
    string? Description,
    string? CoverUrl);

public record CalibreSyncState(string? CalibrePath, DateTime? LastSnapshot);
public record CalibreMirrorReplaceResult(
    IReadOnlyList<int> NewIds,
    IReadOnlyList<int> RemovedIds,
    int TotalCount,
    DateTime Snapshot);

public class CalibreMirrorRepository
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public CalibreMirrorRepository(string connectionString)
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
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS calibre_books (
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
            CREATE TABLE IF NOT EXISTS calibre_sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                calibre_db_path TEXT,
                last_snapshot TEXT
            );
            INSERT INTO calibre_sync_state(id)
            VALUES (1)
            ON CONFLICT(id) DO NOTHING;
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "calibre_books", "authors_json", "TEXT");
        EnsureColumn(conn, "calibre_books", "formats_json", "TEXT");
        EnsureColumn(conn, "calibre_books", "tags_json", "TEXT");
        EnsureColumn(conn, "calibre_books", "description", "TEXT");
        EnsureColumn(conn, "calibre_books", "cover_url", "TEXT");
        EnsureColumn(conn, "calibre_books", "updated_at", "TEXT");
    }

    public async Task<CalibreMirrorReplaceResult> ReplaceAllAsync(IEnumerable<CalibreMirrorBook> books, string? sourcePath, DateTime snapshot, CancellationToken cancellationToken = default)
    {
        var bookList = books?.ToList() ?? new List<CalibreMirrorBook>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var existingIds = await LoadExistingIdsAsync(conn, cancellationToken);
        var newIdSet = new HashSet<int>(bookList.Select(b => b.Id));
        var added = newIdSet.Except(existingIds).ToList();
        var removed = existingIds.Except(newIdSet).ToList();

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        deleteCmd.CommandText = "DELETE FROM calibre_books;";
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

        var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT INTO calibre_books (
                id, title, authors_json, isbn, rating, added_at, published_at, path,
                has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                description, cover_url, updated_at
            ) VALUES (
                @id,@title,@authors,@isbn,@rating,@added,@published,@path,
                @hasCover,@formats,@tags,@publisher,@series,@size,@description,@coverUrl,@updated
            );
            """;
        var pId = insertCmd.Parameters.Add("@id", SqliteType.Integer);
        var pTitle = insertCmd.Parameters.Add("@title", SqliteType.Text);
        var pAuthors = insertCmd.Parameters.Add("@authors", SqliteType.Text);
        var pIsbn = insertCmd.Parameters.Add("@isbn", SqliteType.Text);
        var pRating = insertCmd.Parameters.Add("@rating", SqliteType.Real);
        var pAdded = insertCmd.Parameters.Add("@added", SqliteType.Text);
        var pPublished = insertCmd.Parameters.Add("@published", SqliteType.Text);
        var pPath = insertCmd.Parameters.Add("@path", SqliteType.Text);
        var pHasCover = insertCmd.Parameters.Add("@hasCover", SqliteType.Integer);
        var pFormats = insertCmd.Parameters.Add("@formats", SqliteType.Text);
        var pTags = insertCmd.Parameters.Add("@tags", SqliteType.Text);
        var pPublisher = insertCmd.Parameters.Add("@publisher", SqliteType.Text);
        var pSeries = insertCmd.Parameters.Add("@series", SqliteType.Text);
        var pSize = insertCmd.Parameters.Add("@size", SqliteType.Real);
        var pDescription = insertCmd.Parameters.Add("@description", SqliteType.Text);
        var pCoverUrl = insertCmd.Parameters.Add("@coverUrl", SqliteType.Text);
        var pUpdated = insertCmd.Parameters.Add("@updated", SqliteType.Text);

        foreach (var book in bookList)
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
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var stateCmd = conn.CreateCommand();
        stateCmd.Transaction = tx;
        stateCmd.CommandText = """
            INSERT INTO calibre_sync_state (id, calibre_db_path, last_snapshot)
            VALUES (1, @path, @snapshot)
            ON CONFLICT(id) DO UPDATE SET
                calibre_db_path = excluded.calibre_db_path,
                last_snapshot = excluded.last_snapshot;
            """;
        stateCmd.Parameters.AddWithValue("@path", (object?)sourcePath ?? DBNull.Value);
        stateCmd.Parameters.AddWithValue("@snapshot", snapshot.ToString("o"));
        await stateCmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        return new CalibreMirrorReplaceResult(added, removed, bookList.Count, snapshot);
    }

    public async Task<IReadOnlyList<CalibreMirrorBook>> GetBooksAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        var unlimited = take <= 0;
        cmd.CommandText = unlimited
            ? """
                SELECT id, title, authors_json, isbn, rating, added_at, published_at, path,
                       has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                       description, cover_url
                FROM calibre_books
                ORDER BY COALESCE(added_at, updated_at) DESC;
                """
            : """
                SELECT id, title, authors_json, isbn, rating, added_at, published_at, path,
                       has_cover, formats_json, tags_json, publisher, series, file_size_mb,
                       description, cover_url
                FROM calibre_books
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

    public async Task<CalibreSyncState> GetSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT calibre_db_path, last_snapshot FROM calibre_sync_state WHERE id = 1;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CalibreSyncState(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                ParseDate(reader, 1));
        }

        return new CalibreSyncState(null, null);
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

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static async Task<HashSet<int>> LoadExistingIdsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var ids = new HashSet<int>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM calibre_books;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetInt32(0));
            }
        }

        return ids;
    }
}
