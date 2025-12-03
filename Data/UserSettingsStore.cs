using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Bookworm.Data;

public record UserSettings(string? CalibreDatabasePath, string? HardcoverListId);

public class UserSettingsStore
{
    private const string SettingsFileName = "user-settings.json";
    private const string TableName = "bookworm_settings";
    private readonly string _filePath;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private UserSettings _settings;

    public UserSettingsStore(IConfiguration config, string connectionString)
    {
        var configDir = config["Storage:ConfigPath"];
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(AppContext.BaseDirectory, "App_Data");
        }
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, SettingsFileName);
        _connectionString = NormalizeConnectionString(connectionString);
        EnsureSchema();
        _settings = LoadSettings(config);
    }

    public string? CalibreDatabasePath => _settings.CalibreDatabasePath;
    public string? HardcoverListId => _settings.HardcoverListId;

    public UserSettings Current => _settings;

    public async Task<UserSettings> SetCalibreDatabasePathAsync(string? path)
    {
        var normalized = NormalizePath(path);
        var next = _settings with { CalibreDatabasePath = normalized };
        await SaveAsync(next);
        return next;
    }

    public async Task<UserSettings> SetHardcoverListIdAsync(string? listId)
    {
        var trimmed = string.IsNullOrWhiteSpace(listId) ? null : listId.Trim();
        var next = _settings with { HardcoverListId = trimmed };
        await SaveAsync(next);
        return next;
    }

    private string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(raw.Trim());
        }
        catch
        {
            return raw.Trim();
        }
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
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private UserSettings LoadSettings(IConfiguration config)
    {
        var dbSettings = LoadFromDatabase();
        if (dbSettings is not null)
        {
            return dbSettings;
        }

        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var parsed = JsonSerializer.Deserialize<UserSettings>(json);
                if (parsed != null)
                {
                    // migrate to DB for future
                    SaveToDatabase(parsed);
                    return parsed;
                }
            }
            catch
            {
                // ignore malformed settings and fall back
            }
        }

        var fallback = config["Calibre:DatabasePath"];
        var hardcoverListId = config["Hardcover:ListId"];
        var settings = new UserSettings(
            string.IsNullOrWhiteSpace(fallback) ? null : NormalizePath(fallback),
            string.IsNullOrWhiteSpace(hardcoverListId) ? null : hardcoverListId?.Trim());
        SaveToDatabase(settings);
        return settings;
    }

    private UserSettings? LoadFromDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT key, value FROM {TableName}
            WHERE key IN ('CalibreDatabasePath','HardcoverListId');
            """;

        string? calibrePath = null;
        string? hardcoverListId = null;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.Equals(key, "CalibreDatabasePath", StringComparison.OrdinalIgnoreCase))
            {
                calibrePath = value;
            }
            else if (string.Equals(key, "HardcoverListId", StringComparison.OrdinalIgnoreCase))
            {
                hardcoverListId = value;
            }
        }

        if (calibrePath != null || hardcoverListId != null)
        {
            return new UserSettings(calibrePath, hardcoverListId);
        }

        return null;
    }

    private void SaveToDatabase(UserSettings settings)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {TableName} (key, value) VALUES ('CalibreDatabasePath', @calibre)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO {TableName} (key, value) VALUES ('HardcoverListId', @list)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@calibre", (object?)settings.CalibreDatabasePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@list", (object?)settings.HardcoverListId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private async Task SaveAsync(UserSettings next)
    {
        lock (_gate)
        {
            _settings = next;
        }

        SaveToDatabase(next);

        // keep file as a simple backup/migration path
        var json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
    }
}
