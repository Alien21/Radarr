using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Notifications.Emby
{
    public class MediaBrowserProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly IMediaServerItemLinkStore _itemLinks;
        private readonly Logger _logger;

        public MediaBrowserProxy(IHttpClient httpClient, IMediaServerItemLinkStore itemLinks, Logger logger)
        {
            _httpClient = httpClient;
            _itemLinks = itemLinks;
            _logger = logger;
        }

        public void MarkAsRecentlyAdded(MediaBrowserSettings settings, Movie movie)
        {
            var key = MediaServerItemLinkKey.Create(
                "jellyfin",
                GetUrl(settings),
                settings.ApiKey,
                settings.MapFrom,
                settings.MapTo,
                "movie",
                movie.Id.ToString(CultureInfo.InvariantCulture),
                movie.TmdbId.ToString(CultureInfo.InvariantCulture),
                movie.ImdbId);
            var mappedPath = new OsPath(movie.Path);

            if (settings.MapTo.IsNotNullOrWhiteSpace())
            {
                mappedPath = new OsPath(settings.MapTo) + (mappedPath - new OsPath(settings.MapFrom));
            }

            if (_itemLinks.TryGet(key, out var linked))
            {
                if (TryUpdateLinkedMovie(settings, movie, mappedPath.FullPath, key, linked))
                {
                    return;
                }

                _itemLinks.Remove(key);
            }

            if (_itemLinks.IsNegative(key))
            {
                return;
            }

            if (movie.Year <= 0)
            {
                _logger.Debug("Skipping recently added date discovery for movie {0}: release year is unknown", movie.Id);
                return;
            }

            const int pageSize = 500;
            var matches = new HashSet<string>();
            var seen = new HashSet<string>();
            var start = 0;

            while (true)
            {
                var request = BuildRequest("/Items", settings)
                    .AddQueryParam("recursive", "true")
                    .AddQueryParam("includeItemTypes", "Movie")
                    .AddQueryParam("collapseBoxSetItems", "false")
                    .AddQueryParam("fields", "Path,ProviderIds")
                    .AddQueryParam("enableUserData", "false")
                    .AddQueryParam("enableImages", "false")
                    .AddQueryParam("startIndex", start)
                    .AddQueryParam("limit", pageSize)
                    .AddQueryParam("years", movie.Year);
                var response = JsonNode.Parse(_httpClient.Get(request.Build()).Content);
                var items = response?["Items"] as JsonArray;

                if (items == null || items.Count == 0)
                {
                    break;
                }

                var newItems = false;

                foreach (var item in items.OfType<JsonObject>())
                {
                    var id = GetString(item["Id"]);

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    newItems |= seen.Add(id);

                    if (MatchesMovie(item, movie, mappedPath.FullPath))
                    {
                        matches.Add(id);
                    }
                }

                start += items.Count;
                var hasTotal = int.TryParse(response?["TotalRecordCount"]?.ToString(), out var total);

                if (!newItems || (hasTotal ? start >= total : items.Count < pageSize))
                {
                    break;
                }
            }

            if (matches.Count == 0)
            {
                _logger.Debug("No existing Emby/Jellyfin movie matched the release year and identity for movie {0}; skipping recently added date update", movie.Id);
                _itemLinks.SetNegative(key);
                return;
            }

            var discovered = matches.Select(id => new MediaServerItemLink { ItemId = id }).ToList();

            if (!TryUpdateLinkedMovie(settings, movie, mappedPath.FullPath, key, discovered))
            {
                _itemLinks.Remove(key);
            }
        }

        private bool TryUpdateLinkedMovie(MediaBrowserSettings settings, Movie movie, string mappedPath, string key, List<MediaServerItemLink> links)
        {
            if (links == null || links.Count == 0 || links.Any(link => string.IsNullOrWhiteSpace(link.ItemId)))
            {
                return false;
            }

            try
            {
                var payloads = new List<JsonObject>();

                foreach (var link in links)
                {
                    var request = BuildRequest("/Items", settings)
                        .AddQueryParam("ids", link.ItemId)
                        .AddQueryParam("collapseBoxSetItems", "false")
                        .AddQueryParam("recursive", "true")
                        .AddQueryParam("fields", "DateCreated,Settings,Tags,Genres,Overview,OriginalTitle,ProviderIds,People,Studios,ProductionLocations,CustomRating,Taglines,SortName,SpecialEpisodeNumbers,Path")
                        .AddQueryParam("enableUserData", "false")
                        .AddQueryParam("enableImages", "false")
                        .Build();
                    var response = JsonNode.Parse(_httpClient.Get(request).Content);
                    var items = response?["Items"] as JsonArray;

                    if (items == null)
                    {
                        throw new InvalidOperationException("Emby/Jellyfin returned no collection when validating linked movie metadata");
                    }

                    if (items.Count != 1 || items[0] is not JsonObject item ||
                        GetString(item["Id"]) != link.ItemId || !MatchesMovie(item, movie, mappedPath))
                    {
                        _logger.Warn("Skipping recently added date update for Emby/Jellyfin item {0}: linked movie identity was not returned", link.ItemId);
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(GetString(item["Name"])) ||
                        item["LockData"] is not JsonValue lockData || !lockData.TryGetValue<bool>(out _) ||
                        item["LockedFields"] is not JsonArray ||
                        item["Tags"] is not JsonArray ||
                        item["Genres"] is not JsonArray)
                    {
                        _logger.Warn("Skipping recently added date update for Emby/Jellyfin item {0}: complete movie metadata was not returned", link.ItemId);
                        return true;
                    }

                    payloads.Add((JsonObject)item.DeepClone());
                }

                _itemLinks.Set(key, links);
                var addedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

                foreach (var payload in payloads)
                {
                    var id = GetString(payload["Id"]);
                    payload["DateCreated"] = addedAt;
                    var update = BuildRequest("/Items/" + Uri.EscapeDataString(id), settings).Build();
                    update.Headers.ContentType = "application/json";
                    update.SetContent(payload.ToJsonString());
                    ProcessRequest(update);
                }

                return true;
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        private static bool MatchesMovie(JsonObject item, Movie movie, string mappedPath)
        {
            if (GetString(item["Type"]) != "Movie")
            {
                return false;
            }

            var providerIds = item["ProviderIds"] as JsonObject;
            var tmdb = providerIds?["Tmdb"]?.ToString();
            var imdb = GetString(providerIds?["Imdb"]);

            if (int.TryParse(tmdb, out var tmdbId) && tmdbId > 0)
            {
                return movie.TmdbId > 0 && tmdbId == movie.TmdbId;
            }

            if (!string.IsNullOrWhiteSpace(imdb))
            {
                return !string.IsNullOrWhiteSpace(movie.ImdbId) && imdb.Equals(movie.ImdbId, StringComparison.OrdinalIgnoreCase);
            }

            var path = GetString(item["Path"]);
            return !string.IsNullOrWhiteSpace(path) &&
                   (path.PathEquals(mappedPath) || path.GetParentPath().PathEquals(mappedPath));
        }

        private static string GetString(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
        }

        public void TestConnection(MediaBrowserSettings settings)
        {
            var path = "/System/Configuration";
            var request = BuildRequest(path, settings).Build();

            var response = _httpClient.Get(request);
            _logger.Trace("Response: {0}", response.Content);
        }

        public void Notify(MediaBrowserSettings settings, string title, string message)
        {
            var path = "/Notifications/Admin";
            var request = BuildRequest(path, settings).Build();
            request.Headers.ContentType = "application/json";
            request.LogHttpError = false;

            request.SetContent(new
            {
                Name = title,
                Description = message,
                ImageUrl = "https://raw.github.com/Radarr/Radarr/develop/Logo/64.png"
            }.ToJson());

            ProcessRequest(request);
        }

        public HashSet<string> GetPaths(MediaBrowserSettings settings, Movie movie)
        {
            var path = "/Items";

            var request = BuildRequest(path, settings)
                .AddQueryParam("recursive", "true")
                .AddQueryParam("includeItemTypes", "Movie")
                .AddQueryParam("fields", "Path,ProviderIds")
                .AddQueryParam("years", movie.Year)
                .Build();

            try
            {
                var paths = ProcessGetRequest<MediaBrowserItems>(request).Items.GroupBy(item =>
                {
                    if (item is { ProviderIds.Tmdb: int tmdbid } && tmdbid != 0 && tmdbid == movie.TmdbId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { ProviderIds.Imdb: string imdbid } && imdbid == movie.ImdbId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { Name: var name } && name == movie.Title)
                    {
                        return MediaBrowserMatchQuality.Name;
                    }

                    return MediaBrowserMatchQuality.None;
                },
                    item => item.Path).OrderBy(group => (int)group.Key).First();

                if (paths.Key == MediaBrowserMatchQuality.None)
                {
                    _logger.Trace("Could not find movie by name");

                    return new HashSet<string>();
                }

                _logger.Trace("Found movie by name/id: {0}", string.Join(" ", paths));

                return paths.ToHashSet();
            }
            catch (InvalidOperationException)
            {
                _logger.Trace("Could not find movie by name.");

                return new HashSet<string>();
            }
        }

        public void Update(MediaBrowserSettings settings, string moviePath, string updateType)
        {
            var path = "/Library/Media/Updated";
            var request = BuildRequest(path, settings).Build();
            request.Headers.ContentType = "application/json";

            request.SetContent(new
            {
                Updates = new[]
                {
                    new
                    {
                        Path = moviePath,
                        UpdateType = updateType
                    }
                }
            }.ToJson());

            ProcessRequest(request);
        }

        private T ProcessGetRequest<T>(HttpRequest request)
            where T : new()
        {
            var response = _httpClient.Get<T>(request);
            _logger.Trace("Response: {0}", response.Content);

            CheckForError(response);

            return response.Resource;
        }

        private string ProcessRequest(HttpRequest request)
        {
            var response = _httpClient.Post(request);
            _logger.Trace("Response: {0}", response.Content);

            CheckForError(response);

            return response.Content;
        }

        private string GetUrl(MediaBrowserSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";
            return $@"{scheme}://{settings.Address}";
        }

        private HttpRequestBuilder BuildRequest(string path, MediaBrowserSettings settings)
        {
            var url = GetUrl(settings);
            var request = new HttpRequestBuilder(url).Resource(path);

            request.Headers.Add("X-MediaBrowser-Token", settings.ApiKey);
            request.Headers.Add("Authorization", $"MediaBrowser Token=\"{settings.ApiKey}\"");

            return request;
        }

        private void CheckForError(HttpResponse response)
        {
            _logger.Debug("Looking for error in response: {0}", response);

            // TODO: actually check for the error
        }
    }
}
