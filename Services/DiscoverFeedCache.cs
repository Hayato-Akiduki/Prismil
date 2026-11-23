using System.IO;
using System.Text.Json;
using ZenTube.Models;

namespace ZenTube.Services;

internal static class DiscoverFeedCache
{
    internal sealed record Snapshot(List<VideoItem> Items, DateTimeOffset SavedAt, int EnabledSites, bool HasMore)
    {
        public bool IsFresh(int enabledSites)
        {
            var age = DateTimeOffset.UtcNow - SavedAt;
            return EnabledSites == enabledSites && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5);
        }
    }

    public static string GetPath(string libraryPath, SiteType? site, int shortsMode) =>
        Path.Combine(Path.GetDirectoryName(libraryPath)!, $"discover-{site?.ToString() ?? "all"}-{shortsMode}.json");

    public static Snapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var snapshot = json.TrimStart().StartsWith('[')
                ? new Snapshot(JsonSerializer.Deserialize<List<VideoItem>>(json) ?? [], default, 0, false)
                : JsonSerializer.Deserialize<Snapshot>(json);
            if (snapshot?.Items == null) return null;
            return snapshot with
            {
                Items = snapshot.Items.Where(item => item is { Title: not null, Author: not null } &&
                    !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.WatchUrl)).ToList()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Write("DiscoverCache.Read", ex);
        }
        return null;
    }

    public static void Write(string path, Snapshot snapshot)
    {
        string tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Write("DiscoverCache.Write", ex);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write("DiscoverCache.Cleanup", ex);
            }
        }
    }
}
