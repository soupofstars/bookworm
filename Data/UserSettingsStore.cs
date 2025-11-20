using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Bookworm.Data;

public record UserSettings(string? CalibreDatabasePath);

public class UserSettingsStore
{
    private const string SettingsFileName = "user-settings.json";
    private readonly string _filePath;
    private readonly object _gate = new();
    private UserSettings _settings;

    public UserSettingsStore(IConfiguration config)
    {
        var configDir = config["Storage:ConfigPath"];
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(AppContext.BaseDirectory, "App_Data");
        }
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, SettingsFileName);
        _settings = LoadSettings(config);
    }

    public string? CalibreDatabasePath => _settings.CalibreDatabasePath;

    public UserSettings Current => _settings;

    public async Task<UserSettings> SetCalibreDatabasePathAsync(string? path)
    {
        var normalized = NormalizePath(path);
        var next = _settings with { CalibreDatabasePath = normalized };
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

    private UserSettings LoadSettings(IConfiguration config)
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var parsed = JsonSerializer.Deserialize<UserSettings>(json);
                if (parsed != null)
                {
                    return parsed;
                }
            }
            catch
            {
                // ignore malformed settings and fall back
            }
        }

        var fallback = config["Calibre:DatabasePath"];
        return new UserSettings(string.IsNullOrWhiteSpace(fallback) ? null : NormalizePath(fallback));
    }

    private async Task SaveAsync(UserSettings next)
    {
        lock (_gate)
        {
            _settings = next;
        }

        var json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
    }
}
