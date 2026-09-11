using Microsoft.Extensions.Options;

namespace Crypton.Core.Storage;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Directory for uploaded KYC documents and dispute evidence. Keep it outside any web root and back it up.</summary>
    public string LocalPath { get; set; } = "data/uploads";
}

public sealed class LocalFileStorage(IOptions<StorageOptions> options, TimeProvider clock) : IFileStorage
{
    private string Root => Path.GetFullPath(options.Value.LocalPath);

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (ext.Length > 6 || ext.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException("Invalid file extension.", nameof(extension));
        }

        var now = clock.GetUtcNow();
        var key = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Stored file not found.", key);
        }

        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    private string Resolve(string key)
    {
        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Invalid storage key.");
        }

        return full;
    }
}

public static class UploadValidation
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["application/pdf"] = ".pdf",
    };

    /// <summary>Detects the real content type from magic bytes. Returns null for unsupported files.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (header.Length >= 5 && header[..5].SequenceEqual("%PDF-"u8))
        {
            return "application/pdf";
        }

        return null;
    }
}
