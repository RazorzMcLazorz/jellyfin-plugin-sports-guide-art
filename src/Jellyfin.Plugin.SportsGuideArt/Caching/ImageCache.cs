using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Caching;

/// <summary>
/// Keeps local copies of every image the plugin uses under the server's data folder,
/// so art survives guide refreshes and provider URL changes.
/// </summary>
public sealed class ImageCache
{
    private const long MaxImageBytes = 15 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageCache> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageCache"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ImageCache(IApplicationPaths paths, IHttpClientFactory httpClientFactory, ILogger<ImageCache> logger)
        : this(Path.Combine(paths.DataPath, "sports-guide-art"), httpClientFactory, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageCache"/> class at a given root.
    /// </summary>
    /// <param name="root">The cache root folder.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ImageCache(string root, IHttpClientFactory httpClientFactory, ILogger<ImageCache> logger)
    {
        Root = root;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        Directory.CreateDirectory(CopiedDir);
        Directory.CreateDirectory(GeneratedDir);
        Directory.CreateDirectory(LogosDir);
    }

    /// <summary>Gets the cache root.</summary>
    public string Root { get; }

    /// <summary>Gets the folder for art copied from other programs.</summary>
    public string CopiedDir => Path.Combine(Root, "copied");

    /// <summary>Gets the folder for generated matchup images.</summary>
    public string GeneratedDir => Path.Combine(Root, "generated");

    /// <summary>Gets the folder for team logos.</summary>
    public string LogosDir => Path.Combine(Root, "logos");

    /// <summary>Returns a short stable hash of a string, used for file names.</summary>
    /// <param name="value">The value.</param>
    /// <returns>32 hex characters.</returns>
    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    /// <summary>Gets whether a path lies inside a cache folder.</summary>
    /// <param name="path">The image path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>True when the path is inside the folder.</returns>
    public static bool IsIn(string? path, string folder)
        => !string.IsNullOrEmpty(path)
           && path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Returns a local copy of an image URL or file, downloading or copying it once.
    /// </summary>
    /// <param name="source">An http(s) URL or a local file path.</param>
    /// <param name="folder">The cache folder to store it in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The local file path, or null when the image could not be fetched.</returns>
    public async Task<string?> EnsureLocalAsync(string source, string folder, CancellationToken cancellationToken)
    {
        if (IsIn(source, Root) && File.Exists(source))
        {
            Touch(source);
            return source;
        }

        var hash = Hash(source);
        var existing = Directory.EnumerateFiles(folder, hash + ".*").FirstOrDefault();
        if (existing is not null)
        {
            Touch(existing);
            return existing;
        }

        try
        {
            byte[] bytes;
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
                {
                    _logger.LogDebug("Image fetch failed ({Status}) for {Url}", (int)response.StatusCode, source);
                    return null;
                }

                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(source))
            {
                bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return null;
            }

            var extension = DetectExtension(bytes);
            if (extension is null || bytes.LongLength > MaxImageBytes)
            {
                _logger.LogDebug("Skipping {Source}: not a supported image", source);
                return null;
            }

            return await WriteAsync(folder, hash + extension, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not cache image {Source}", source);
            return null;
        }
    }

    /// <summary>Writes bytes atomically into a cache folder.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final path.</returns>
    public static async Task<string> WriteAsync(string folder, string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, true);
        return path;
    }

    /// <summary>Marks a cached file as recently used.</summary>
    /// <param name="path">The file.</param>
    public static void Touch(string path)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes cached images not used for the given number of days.</summary>
    /// <param name="retentionDays">Days to keep unused files.</param>
    /// <returns>The number of deleted files.</returns>
    public int Prune(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;
        foreach (var folder in new[] { CopiedDir, GeneratedDir, LogosDir })
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return deleted;
    }

    /// <summary>Detects an image file extension from its leading bytes.</summary>
    /// <param name="bytes">The file content.</param>
    /// <returns>The extension with a dot, or null if not a known image.</returns>
    public static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return null;
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return ".jpg";
        }

        if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return ".webp";
        }

        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
        {
            return ".gif";
        }

        return null;
    }
}
