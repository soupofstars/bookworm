using Microsoft.Extensions.FileProviders;

namespace Bookworm.Data;

public class CalibreCoverService
{
    private readonly string _coversRoot;
    public string RequestPath { get; } = "/calibre-covers";

    public CalibreCoverService(IConfiguration config)
    {
        var configDir = config["Storage:ConfigPath"];
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(AppContext.BaseDirectory, "App_Data");
        }
        _coversRoot = Path.Combine(configDir, "calibre_covers");
        Directory.CreateDirectory(_coversRoot);
    }

    public string GetCoverPath(int bookId) => Path.Combine(_coversRoot, $"{bookId}.jpg");

    public bool CoverExists(int bookId) => File.Exists(GetCoverPath(bookId));

    public async Task<string?> EnsureCoverAsync(int bookId, string libraryRoot, string relativeBookPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(relativeBookPath))
        {
            return null;
        }

        var bookFolder = Path.Combine(libraryRoot, relativeBookPath);
        if (!Directory.Exists(bookFolder))
        {
            return null;
        }

        var sourceCover = Path.Combine(bookFolder, "cover.jpg");
        if (!File.Exists(sourceCover))
        {
            return null;
        }

        var destination = Path.Combine(_coversRoot, $"{bookId}.jpg");
        var copyNeeded = !File.Exists(destination)
                         || File.GetLastWriteTimeUtc(sourceCover) > File.GetLastWriteTimeUtc(destination);

        if (copyNeeded)
        {
            await using var source = File.OpenRead(sourceCover);
            await using var target = File.Create(destination);
            await source.CopyToAsync(target, cancellationToken);
        }

        return $"{RequestPath.TrimEnd('/')}/{bookId}.jpg";
    }

    public IFileProvider AsFileProvider()
    {
        return new PhysicalFileProvider(_coversRoot);
    }
}
