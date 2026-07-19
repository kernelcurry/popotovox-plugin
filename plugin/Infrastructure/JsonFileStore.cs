using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopotoVox.Infrastructure;

/// <summary>
/// Small atomic JSON load/save helper for our on-disk caches. Writes go to a temp
/// file then move into place so a crash mid-write can't corrupt a cache. Saves to
/// the same path are serialized by a per-path lock so concurrent writers (e.g. two
/// casts finishing at once) can't race on the temp file.
/// </summary>
public static class JsonFileStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            // Single atomic rename — no delete window where the file is missing.
            File.Move(tmp, path, overwrite: true);
        }
    }
}
