using System;
using System.Collections.Concurrent;

namespace Blocker.Core;

/// <summary>
/// Thread-safe in-memory cache keyed by (file path + last write time UTC + target height).
/// Decouples caching logic for maximum performance and full unit-testability.
/// </summary>
public class NotesAttachmentCache<T> where T : class
{
    private readonly ConcurrentDictionary<string, T> _cache = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _cache.Count;

    public void Clear()
    {
        _cache.Clear();
    }

    public static string CreateCacheKey(string filePath, DateTime lastWriteTimeUtc, int targetHeight)
    {
        return $"{filePath}|{lastWriteTimeUtc.Ticks}|{targetHeight}";
    }

    public bool TryGet(string filePath, DateTime lastWriteTimeUtc, int targetHeight, out T? value)
    {
        string key = CreateCacheKey(filePath, lastWriteTimeUtc, targetHeight);
        return _cache.TryGetValue(key, out value);
    }

    public void Set(string filePath, DateTime lastWriteTimeUtc, int targetHeight, T value)
    {
        string key = CreateCacheKey(filePath, lastWriteTimeUtc, targetHeight);
        _cache[key] = value;
    }

    public bool Remove(string filePath, DateTime lastWriteTimeUtc, int targetHeight)
    {
        string key = CreateCacheKey(filePath, lastWriteTimeUtc, targetHeight);
        return _cache.TryRemove(key, out _);
    }
}
