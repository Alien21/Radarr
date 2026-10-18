using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Notifications
{
    public class MediaServerItemLink
    {
        public int? SectionId { get; set; }
        public string ItemId { get; set; }
    }

    public interface IMediaServerItemLinkStore
    {
        bool TryGet(string key, out List<MediaServerItemLink> links);
        void Set(string key, IEnumerable<MediaServerItemLink> links);
        void Remove(string key);
        bool IsNegative(string key);
        void SetNegative(string key);
    }

    public static class MediaServerItemLinkKey
    {
        public static string Create(string provider, string serverUrl, string token, string mapFrom, string mapTo, params string[] mediaIdentity)
        {
            var server = Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
                ? uri.AbsoluteUri.TrimEnd('/')
                : serverUrl?.TrimEnd('/') ?? string.Empty;
            var identity = new[]
            {
                provider?.ToLowerInvariant() ?? string.Empty,
                server,
                Hash(token ?? string.Empty),
                mapFrom ?? string.Empty,
                mapTo ?? string.Empty
            }.Concat(mediaIdentity ?? Array.Empty<string>());

            return Hash(JsonSerializer.Serialize(identity));
        }

        private static string Hash(string value)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }

    public class MediaServerItemLinkStore : IMediaServerItemLinkStore
    {
        private readonly object _mutex = new();
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly string _path;
        private readonly Dictionary<string, DateTime> _negative = new();
        private Dictionary<string, List<MediaServerItemLink>> _links = new();
        private bool _loaded;

        public MediaServerItemLinkStore(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
            _path = Path.Combine(appFolderInfo.AppDataFolder, "MediaServerItemLinks.json");
        }

        public bool TryGet(string key, out List<MediaServerItemLink> links)
        {
            lock (_mutex)
            {
                EnsureLoaded();

                if (_links.TryGetValue(key, out var stored))
                {
                    links = Copy(stored);
                    return true;
                }

                links = null;
                return false;
            }
        }

        public void Set(string key, IEnumerable<MediaServerItemLink> links)
        {
            lock (_mutex)
            {
                EnsureLoaded();
                _negative.Remove(key);
                var values = Copy(links);

                if (values.Count == 0)
                {
                    if (_links.Remove(key))
                    {
                        Save();
                    }

                    return;
                }

                if (_links.TryGetValue(key, out var existing) &&
                    existing.Select(link => (link.SectionId, link.ItemId)).SequenceEqual(values.Select(link => (link.SectionId, link.ItemId))))
                {
                    return;
                }

                _links[key] = values;
                Save();
            }
        }

        public void Remove(string key)
        {
            lock (_mutex)
            {
                EnsureLoaded();
                _negative.Remove(key);

                if (_links.Remove(key))
                {
                    Save();
                }
            }
        }

        public bool IsNegative(string key)
        {
            lock (_mutex)
            {
                if (_negative.TryGetValue(key, out var until) && until > DateTime.UtcNow)
                {
                    return true;
                }

                _negative.Remove(key);
                return false;
            }
        }

        public void SetNegative(string key)
        {
            lock (_mutex)
            {
                EnsureLoaded();

                if (_links.Remove(key))
                {
                    Save();
                }

                _negative[key] = DateTime.UtcNow.AddSeconds(30);
            }
        }

        private static List<MediaServerItemLink> Copy(IEnumerable<MediaServerItemLink> links)
        {
            return links?.Where(link => link != null && !string.IsNullOrWhiteSpace(link.ItemId))
                .Select(link => new MediaServerItemLink { SectionId = link.SectionId, ItemId = link.ItemId })
                .DistinctBy(link => (link.SectionId, link.ItemId))
                .OrderBy(link => link.SectionId)
                .ThenBy(link => link.ItemId, StringComparer.Ordinal)
                .ToList() ?? new List<MediaServerItemLink>();
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            try
            {
                if (!_diskProvider.FileExists(_path))
                {
                    return;
                }

                var data = JsonSerializer.Deserialize<LinkFile>(_diskProvider.ReadAllText(_path));

                if (data?.Version != 1 || data.Links == null)
                {
                    throw new JsonException("Unsupported media server link cache schema");
                }

                _links = data.Links.Where(entry => entry.Key.Length == 64 && entry.Key.All(Uri.IsHexDigit))
                    .ToDictionary(entry => entry.Key, entry => Copy(entry.Value));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                _logger.Warn(ex, "Unable to load media server item links; bindings will be discovered again");
                _links.Clear();
            }
        }

        private void Save()
        {
            var temp = _path + ".tmp";

            try
            {
                _diskProvider.EnsureFolder(Path.GetDirectoryName(_path));
                _diskProvider.WriteAllText(temp, JsonSerializer.Serialize(new LinkFile { Version = 1, Links = _links }));

                // The native overwrite helper deletes the target first; this rename preserves the previous cache on failure.
                File.Move(temp, _path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                _logger.Warn(ex, "Unable to persist media server item links; in-memory bindings remain available");
            }
        }

        private sealed class LinkFile
        {
            public LinkFile()
            {
            }

            public int Version { get; set; }
            public Dictionary<string, List<MediaServerItemLink>> Links { get; set; }
        }
    }
}
