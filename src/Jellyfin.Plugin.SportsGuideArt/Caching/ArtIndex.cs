using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.SportsGuideArt.Matching;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Caching;

/// <summary>
/// Remembers the cached art for each event key, so slots that outlive the event's own
/// guide entry (for example "Postgame") still get art after the game drops out of the guide.
/// </summary>
public sealed class ArtIndex
{
    private readonly string _file;
    private readonly ILogger<ArtIndex> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtIndex"/> class.
    /// </summary>
    /// <param name="cache">The image cache whose root holds the index.</param>
    /// <param name="logger">Logger.</param>
    public ArtIndex(ImageCache cache, ILogger<ArtIndex> logger)
    {
        _file = Path.Combine(cache.Root, "art-index.json");
        _logger = logger;
    }

    /// <summary>Gets the remembered local files for a key, if they still exist.</summary>
    /// <param name="key">The match key.</param>
    /// <returns>Images by kind, or null.</returns>
    public IReadOnlyDictionary<ArtKind, string>? Get(string key)
    {
        EnsureLoaded();
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var existing = entry.Files.Where(f => File.Exists(f.Value)).ToDictionary(f => f.Key, f => f.Value);
        return existing.Count > 0 ? existing : null;
    }

    /// <summary>Records the cached art for a key.</summary>
    /// <param name="key">The match key.</param>
    /// <param name="files">Local files by kind.</param>
    public void Set(string key, IReadOnlyDictionary<ArtKind, string> files)
    {
        EnsureLoaded();
        _entries[key] = new Entry { Files = new Dictionary<ArtKind, string>(files), LastSeenUtc = DateTime.UtcNow };
    }

    /// <summary>Drops entries older than the retention period and saves the index.</summary>
    /// <param name="retentionDays">Days to keep entries.</param>
    public void Save(int retentionDays)
    {
        EnsureLoaded();
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var stale in _entries.Where(e => e.Value.LastSeenUtc < cutoff).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(stale, out _);
            }
        }

        try
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _file, true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save art index {File}", _file);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file));
            foreach (var pair in data ?? [])
            {
                _entries[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable art index {File}", _file);
        }
    }

    /// <summary>A remembered entry.</summary>
    public sealed class Entry
    {
        /// <summary>Gets or sets the local files by kind.</summary>
        public Dictionary<ArtKind, string> Files { get; set; } = [];

        /// <summary>Gets or sets when the art was last seen in the guide.</summary>
        public DateTime LastSeenUtc { get; set; }
    }
}
