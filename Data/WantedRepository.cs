using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record WantedBookRequest(string Key, JsonElement Book);
public record WantedBookResponse(string Key, JsonElement Book);

public class WantedRepository
{
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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wanted_books (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                book_key TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();
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
        cmd.CommandText = "SELECT book_key, payload FROM wanted_books ORDER BY created_at DESC";
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
        cmd.CommandText = """
            INSERT INTO wanted_books (book_key, payload, created_at, updated_at)
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
        cmd.CommandText = "DELETE FROM wanted_books WHERE book_key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WantedBookResponse ToResponse(WantedBookStorage storage)
    {
        using var doc = JsonDocument.Parse(storage.Payload);
        return new WantedBookResponse(storage.Key, doc.RootElement.Clone());
    }
}
