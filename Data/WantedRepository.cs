using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bookworm.Data;

public record WantedBookRequest(string Key, JsonElement Book);
public record WantedBookResponse(string Key, JsonElement Book);

public class WantedRepository
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly List<WantedBookStorage> _memory = new();
    private readonly object _gate = new();

    private sealed record WantedBookStorage(string Key, string Payload);

    public bool IsConfigured => _dataSource is not null;

    public WantedRepository(NpgsqlDataSource? dataSource)
    {
        _dataSource = dataSource;
        if (_dataSource is null) return;

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wanted_books (
                id BIGSERIAL PRIMARY KEY,
                book_key TEXT NOT NULL UNIQUE,
                payload JSONB NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
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
        await using var conn = await _dataSource!.OpenConnectionAsync(cancellationToken);
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

        await using var conn = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wanted_books (book_key, payload)
            VALUES (@key, @payload)
            ON CONFLICT (book_key) DO UPDATE
            SET payload = EXCLUDED.payload,
                updated_at = NOW();
            """;
        cmd.Parameters.AddWithValue("key", request.Key);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
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

        await using var conn = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM wanted_books WHERE book_key = @key";
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WantedBookResponse ToResponse(WantedBookStorage storage)
    {
        using var doc = JsonDocument.Parse(storage.Payload);
        return new WantedBookResponse(storage.Key, doc.RootElement.Clone());
    }
}
