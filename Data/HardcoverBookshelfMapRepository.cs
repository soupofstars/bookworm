using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record HardcoverBookshelfMapEntry(int CalibreId, string? HardcoverId, DateTime LastCheckedUtc);

public class HardcoverBookshelfMapRepository
{
    private const string TableName = "hardcover_bookshelf_map";
    private readonly string _connectionString;

    public HardcoverBookshelfMapRepository(string connectionString)
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
                calibre_id INTEGER PRIMARY KEY,
                hardcover_id TEXT,
                last_checked_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<HardcoverBookshelfMapEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<HardcoverBookshelfMapEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT calibre_id, hardcover_id, last_checked_utc
            FROM {TableName};
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HardcoverBookshelfMapEntry(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTime.TryParse(reader.GetString(2), out var dt) ? dt : DateTime.MinValue
            ));
        }

        return results;
    }

    public async Task UpsertAsync(int calibreId, string? hardcoverId, DateTime lastCheckedUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {TableName} (calibre_id, hardcover_id, last_checked_utc)
            VALUES (@cid, @hid, @checked)
            ON CONFLICT(calibre_id) DO UPDATE SET
                hardcover_id = excluded.hardcover_id,
                last_checked_utc = excluded.last_checked_utc;
            """;
        cmd.Parameters.AddWithValue("@cid", calibreId);
        cmd.Parameters.AddWithValue("@hid", (object?)hardcoverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@checked", lastCheckedUtc.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
