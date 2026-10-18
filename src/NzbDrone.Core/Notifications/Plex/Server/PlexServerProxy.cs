using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public interface IPlexServerProxy
    {
        void MarkAsRecentlyAdded(Movie movie, PlexServerSettings settings);
        List<PlexSection> GetMovieSections(PlexServerSettings settings);
        string Version(PlexServerSettings settings);
        void Update(int sectionId, string path, PlexServerSettings settings);
    }

    public class PlexServerProxy : IPlexServerProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly IMediaServerItemLinkStore _itemLinks;
        private readonly Logger _logger;

        public PlexServerProxy(IHttpClient httpClient, IConfigService configService, IMediaServerItemLinkStore itemLinks, Logger logger)
        {
            _httpClient = httpClient;
            _configService = configService;
            _itemLinks = itemLinks;
            _logger = logger;
        }

        public void MarkAsRecentlyAdded(Movie movie, PlexServerSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";
            var key = MediaServerItemLinkKey.Create(
                "plex",
                $"{scheme}://{settings.Host.ToUrlHost()}:{settings.Port}{settings.UrlBase}",
                settings.AuthToken,
                settings.MapFrom,
                settings.MapTo,
                "movie",
                movie.Id.ToString(CultureInfo.InvariantCulture),
                movie.TmdbId.ToString(CultureInfo.InvariantCulture),
                movie.ImdbId);

            if (_itemLinks.TryGet(key, out var linked))
            {
                if (TryUpdateLinkedMovie(movie, settings, key, linked))
                {
                    return;
                }

                _itemLinks.Remove(key);
            }

            if (_itemLinks.IsNegative(key))
            {
                return;
            }

            var discovered = DiscoverMovieLinks(movie, settings);

            if (discovered.Count == 0)
            {
                _logger.Debug("No existing Plex movie matched the external identity for movie {0}; skipping recently added date update", movie.Id);
                _itemLinks.SetNegative(key);
                return;
            }

            if (!TryUpdateLinkedMovie(movie, settings, key, discovered))
            {
                _itemLinks.Remove(key);
            }
        }

        private bool TryUpdateLinkedMovie(Movie movie, PlexServerSettings settings, string key, List<MediaServerItemLink> links)
        {
            if (links == null || links.Count == 0 || links.Any(link => !link.SectionId.HasValue || link.SectionId <= 0 ||
                !long.TryParse(link.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
            {
                return false;
            }

            try
            {
                var ids = links.Select(link => link.ItemId).Distinct().ToList();
                var request = BuildRequest("library/metadata/" + string.Join(",", ids), HttpMethod.Get, settings)
                    .AddQueryParam("includeGuids", 1);
                var response = ProcessRequest(request, true);
                CheckForError(response);
                var root = JsonNode.Parse(response);
                var container = root?["MediaContainer"] ?? root;
                var items = container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;

                if (items == null)
                {
                    throw new PlexException("Plex returned no collection when validating linked movie metadata");
                }

                foreach (var link in links)
                {
                    var candidates = items.OfType<JsonObject>().Where(item => item["ratingKey"]?.ToString() == link.ItemId).ToList();

                    if (candidates.Count != 1 || !MatchesMovie(candidates[0], movie))
                    {
                        return false;
                    }

                    var sectionValue = candidates[0]["librarySectionID"]?.ToString();

                    if (sectionValue != null && (!int.TryParse(sectionValue, out var sectionId) || sectionId != link.SectionId))
                    {
                        return false;
                    }
                }

                _itemLinks.Set(key, links);
                var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var section in links.GroupBy(link => link.SectionId.Value))
                {
                    var update = BuildRequest($"library/sections/{section.Key}/all", HttpMethod.Put, settings)
                        .AddQueryParam("type", 1)
                        .AddQueryParam("id", string.Join(",", section.Select(link => link.ItemId).Distinct()))
                        .AddQueryParam("addedAt.value", addedAt);
                    CheckForError(ProcessRequest(update, true));
                }

                return true;
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        private List<MediaServerItemLink> DiscoverMovieLinks(Movie movie, PlexServerSettings settings)
        {
            var providerTitle = movie.TmdbId > 0
                ? "tmdb-" + movie.TmdbId.ToString(CultureInfo.InvariantCulture)
                : !string.IsNullOrWhiteSpace(movie.ImdbId) ? "imdb-" + movie.ImdbId : null;

            if (providerTitle == null)
            {
                return new List<MediaServerItemLink>();
            }

            var triedAgents = new HashSet<string>(StringComparer.Ordinal);

            foreach (var section in GetMovieSections(settings).Where(section => section.Id > 0 && !string.IsNullOrWhiteSpace(section.Agent) && !triedAgents.Contains(section.Agent)))
            {
                var sampleRequest = BuildRequest($"library/sections/{section.Id}/all", HttpMethod.Get, settings)
                    .AddQueryParam("type", 1);
                sampleRequest.Headers.Add("X-Plex-Container-Start", "0");
                sampleRequest.Headers.Add("X-Plex-Container-Size", "1");
                var sampleResponse = ProcessRequest(sampleRequest);
                CheckForError(sampleResponse);
                var sampleRoot = JsonNode.Parse(sampleResponse);
                var sampleContainer = sampleRoot?["MediaContainer"] ?? sampleRoot;
                var samples = sampleContainer?["Metadata"] as JsonArray ?? sampleContainer?["_children"] as JsonArray;

                if (samples == null)
                {
                    throw new PlexException("Plex returned no movie collection when resolving an external identity");
                }

                if (samples.Count == 0)
                {
                    continue;
                }

                if (samples.Count != 1 || samples[0] is not JsonObject sample ||
                    sample["type"]?.ToString() != "movie" ||
                    !long.TryParse(sample["ratingKey"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var sampleId) || sampleId <= 0)
                {
                    throw new PlexException("Plex ignored the single movie sample request");
                }

                triedAgents.Add(section.Agent);
                var matchRequest = BuildRequest($"library/metadata/{sampleId}/matches", HttpMethod.Get, settings)
                    .AddQueryParam("manual", 1)
                    .AddQueryParam("agent", section.Agent)
                    .AddQueryParam("title", providerTitle)
                    .AddQueryParam("year", sample["year"]?.ToString() ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(section.Language))
                {
                    matchRequest.AddQueryParam("language", section.Language);
                }

                var matchResponse = ProcessRequest(matchRequest);
                CheckForError(matchResponse);
                var matchRoot = JsonNode.Parse(matchResponse);
                var matchContainer = matchRoot?["MediaContainer"] ?? matchRoot;
                var candidates = matchContainer?["SearchResult"] as JsonArray ?? matchContainer?["_children"] as JsonArray;

                if (candidates == null)
                {
                    throw new PlexException("Plex returned no external identity match collection");
                }

                var guids = candidates.OfType<JsonObject>()
                    .Where(candidate => candidate["type"]?.ToString() == "movie")
                    .Select(candidate => candidate["guid"]?.ToString())
                    .Where(guid => guid != null && guid.StartsWith("plex://movie/", StringComparison.Ordinal) && guid.Length > "plex://movie/".Length)
                    .Distinct(StringComparer.Ordinal)
                    .Take(20);
                var links = new List<MediaServerItemLink>();

                foreach (var guid in guids)
                {
                    links.AddRange(FindMovieLinks(movie, settings, guid));
                }

                if (links.Count > 0)
                {
                    return links.DistinctBy(link => (link.SectionId, link.ItemId)).ToList();
                }
            }

            return new List<MediaServerItemLink>();
        }

        private List<MediaServerItemLink> FindMovieLinks(Movie movie, PlexServerSettings settings, string guid)
        {
            const int pageSize = 500;
            const int maxPages = 2;
            var links = new List<MediaServerItemLink>();
            var seen = new HashSet<string>();
            var start = 0;

            for (var page = 0; page < maxPages; page++)
            {
                var request = BuildRequest("library/all", HttpMethod.Get, settings)
                    .AddQueryParam("type", 1)
                    .AddQueryParam("guid", guid)
                    .AddQueryParam("includeGuids", 1);
                request.Headers.Add("X-Plex-Container-Start", start.ToString(CultureInfo.InvariantCulture));
                request.Headers.Add("X-Plex-Container-Size", pageSize.ToString(CultureInfo.InvariantCulture));
                var response = ProcessRequest(request);
                CheckForError(response);
                var root = JsonNode.Parse(response);
                var container = root?["MediaContainer"] ?? root;
                var items = container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;

                if (items == null)
                {
                    throw new PlexException("Plex returned no GUID-filtered movie collection");
                }

                var hasTotal = int.TryParse(container?["totalSize"]?.ToString(), out var total);

                if (items.Count > pageSize || (hasTotal && (total < 0 || total > pageSize * maxPages)) ||
                    (container?["offset"] != null && (!int.TryParse(container["offset"].ToString(), out var offset) || offset != start)))
                {
                    throw new PlexException("Plex returned an invalid or unbounded GUID-filtered movie page");
                }

                if (items.Count == 0)
                {
                    return links;
                }

                var newItems = false;

                foreach (var node in items)
                {
                    if (node is not JsonObject item || item["guid"]?.ToString() != guid)
                    {
                        throw new PlexException("Plex ignored the movie GUID filter; recently added dates were not changed");
                    }

                    var id = item["ratingKey"]?.ToString();

                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var ratingKey) || ratingKey <= 0)
                    {
                        continue;
                    }

                    newItems |= seen.Add(id);

                    if (MatchesMovie(item, movie) &&
                        int.TryParse(item["librarySectionID"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var sectionId) && sectionId > 0)
                    {
                        links.Add(new MediaServerItemLink { SectionId = sectionId, ItemId = id });
                    }
                }

                if (!newItems)
                {
                    throw new PlexException("Plex repeated a GUID-filtered movie page");
                }

                start += items.Count;

                if (hasTotal ? start >= total : items.Count < pageSize)
                {
                    return links;
                }
            }

            throw new PlexException("Plex exceeded the GUID-filtered movie page limit");
        }

        private static bool MatchesMovie(JsonObject item, Movie movie)
        {
            if (item["type"]?.ToString() != "movie")
            {
                return false;
            }

            var guids = (item["Guid"] as JsonArray)?.OfType<JsonObject>()
                .Select(guid => guid["id"]?.ToString())
                .ToList() ?? new List<string>();
            guids.Add(item["guid"]?.ToString());

            return guids.Where(guid => !string.IsNullOrWhiteSpace(guid)).Any(guid =>
            {
                var id = guid.Split('?')[0];

                return (movie.TmdbId > 0 &&
                        (id.Equals($"tmdb://{movie.TmdbId}", StringComparison.OrdinalIgnoreCase) ||
                         id.Equals($"com.plexapp.agents.themoviedb://{movie.TmdbId}", StringComparison.OrdinalIgnoreCase))) ||
                       (!string.IsNullOrWhiteSpace(movie.ImdbId) &&
                        (id.Equals($"imdb://{movie.ImdbId}", StringComparison.OrdinalIgnoreCase) ||
                         id.Equals($"com.plexapp.agents.imdb://{movie.ImdbId}", StringComparison.OrdinalIgnoreCase)));
            });
        }

        public List<PlexSection> GetMovieSections(PlexServerSettings settings)
        {
            var request = BuildRequest("library/sections", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexMediaContainerLegacy>(response)
                    .Sections
                    .Where(d => d.Type == "movie")
                    .Select(s => new PlexSection
                    {
                        Id = s.Id,
                        Language = s.Language,
                        Agent = s.Agent,
                        Locations = s.Locations,
                        Type = s.Type
                    })
                    .ToList();
            }

            return Json.Deserialize<PlexResponse<PlexSectionsContainer>>(response)
                       .MediaContainer
                       .Sections
                       .Where(d => d.Type == "movie")
                       .ToList();
        }

        public void Update(int sectionId, string path, PlexServerSettings settings)
        {
            var resource = $"library/sections/{sectionId}/refresh";
            var request = BuildRequest(resource, HttpMethod.Get, settings);

            request.AddQueryParam("path", path);

            var response = ProcessRequest(request);

            CheckForError(response);
        }

        public string Version(PlexServerSettings settings)
        {
            var request = BuildRequest("identity", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexIdentity>(response)
                           .Version;
            }

            return Json.Deserialize<PlexResponse<PlexIdentity>>(response)
                       .MediaContainer
                       .Version;
        }

        private HttpRequestBuilder BuildRequest(string resource, HttpMethod method, PlexServerSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";

            var requestBuilder = new HttpRequestBuilder($"{scheme}://{settings.Host.ToUrlHost()}:{settings.Port}{settings.UrlBase}")
                                 .Accept(HttpAccept.Json)
                                 .AddQueryParam("X-Plex-Client-Identifier", _configService.PlexClientIdentifier)
                                 .AddQueryParam("X-Plex-Product", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Platform", "Windows")
                                 .AddQueryParam("X-Plex-Platform-Version", "7")
                                 .AddQueryParam("X-Plex-Device-Name", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Version", BuildInfo.Version.ToString());

            if (settings.AuthToken.IsNotNullOrWhiteSpace())
            {
                requestBuilder.AddQueryParam("X-Plex-Token", settings.AuthToken);
            }

            requestBuilder.ResourceUrl = resource;
            requestBuilder.Method = method;

            return requestBuilder;
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder, bool preserveNotFound = false)
        {
            var httpRequest = requestBuilder.Build();

            HttpResponse response;

            _logger.Debug("Url: {0}", httpRequest.Url);

            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                if (preserveNotFound && ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw;
                }

                if (ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new PlexAuthenticationException("Unauthorized - AuthToken is invalid");
                }

                throw new PlexException("Unable to connect to Plex Media Server. Status Code: {0}", ex.Response.StatusCode);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.TrustFailure)
                {
                    throw new PlexException("Unable to connect to Plex Media Server, certificate validation failed.", ex);
                }

                throw new PlexException($"Unable to connect to Plex Media Server, {ex.Message}", ex);
            }

            return response.Content;
        }

        private void CheckForError(string response)
        {
            _logger.Trace("Checking for error");

            if (response.IsNullOrWhiteSpace())
            {
                _logger.Trace("No response body returned, no error detected");
                return;
            }

            var error = response.Contains("_children") ?
                        Json.Deserialize<PlexError>(response) :
                        Json.Deserialize<PlexResponse<PlexError>>(response).MediaContainer;

            if (error != null && !error.Error.IsNullOrWhiteSpace())
            {
                throw new PlexException(error.Error);
            }

            _logger.Trace("No error detected");
        }
    }
}
