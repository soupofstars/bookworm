using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record WantedBookRequest(string Key, JsonElement Book);
public record WantedBookResponse(string Key, JsonElement Book);

public class WantedRepository
{
    private const string TableName = "bookworm_wanted_books";
    private const string LegacyTableName = "wanted_books";
    private readonly string? _connectionString;
    private readonly List<WantedBookStorage> _memory = new();
    private readonly object _gate = new();

    private sealed record WantedBookStorage(string Key, string Payload);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public WantedRepository(string? connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : NormalizeConnectionString(connectionString);

        if (_connectionString is null) return;

        EnsureDatabase();
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

    private void EnsureDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        MigrateLegacyTable(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                book_key TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", tableName);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static void MigrateLegacyTable(SqliteConnection conn)
    {
        var legacyExists = TableExists(conn, LegacyTableName);
        var targetExists = TableExists(conn, TableName);
        if (legacyExists && !targetExists)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {LegacyTableName} RENAME TO {TableName};";
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<IReadOnlyList<WantedBookResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            lock (_gate)
            {
                return _memory.Select(ToResponse).ToList();
            }
        }

        var results = new List<WantedBookResponse>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT book_key, payload FROM {TableName} ORDER BY created_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var payload = reader.GetString(1);
            results.Add(ToResponse(new WantedBookStorage(key, payload)));
        }

        return results;
    }

    public async Task UpsertAsync(WantedBookRequest request, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(request.Book);

        if (!IsConfigured)
        {
            lock (_gate)
            {
                var idx = _memory.FindIndex(x => x.Key == request.Key);
                if (idx >= 0)
                {
                    _memory[idx] = _memory[idx] with { Payload = payload };
                }
                else
                {
                    _memory.Add(new WantedBookStorage(request.Key, payload));
                }
            }
            return;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {TableName} (book_key, payload, created_at, updated_at)
            VALUES (@key, @payload, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(book_key) DO UPDATE SET
                payload = excluded.payload,
                updated_at = CURRENT_TIMESTAMP;
            """;
        cmd.Parameters.AddWithValue("@key", request.Key);
        cmd.Parameters.AddWithValue("@payload", payload);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            lock (_gate)
            {
                var idx = _memory.FindIndex(x => x.Key == key);
                if (idx >= 0)
                {
                    _memory.RemoveAt(idx);
                }
            }
            return;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE book_key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WantedBookResponse ToResponse(WantedBookStorage storage)
    {
        using var doc = JsonDocument.Parse(storage.Payload);
        return new WantedBookResponse(storage.Key, doc.RootElement.Clone());
    }
}
