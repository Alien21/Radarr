using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.NotificationTests.Plex
{
    [TestFixture]
    public class PlexRecentlyAddedFixture : CoreTest<PlexServerProxy>
    {
        private const string PlexGuid = "plex://movie/test-movie";
        private Queue<string> _pages;
        private Dictionary<string, JsonObject> _metadata;
        private Dictionary<string, string> _sampleResponses;
        private Dictionary<string, string> _matchResponses;
        private Queue<System.Net.HttpStatusCode> _metadataErrors;
        private Queue<System.Net.HttpStatusCode> _putErrors;
        private List<HttpRequest> _requests;
        private Movie _movie;
        private PlexServerSettings _settings;
        private string _updateResponse;
        private string _sectionsResponse;
        private string _matchResponse;

        [SetUp]
        public void Setup()
        {
            _pages = new Queue<string>();
            _metadata = new Dictionary<string, JsonObject>();
            _sampleResponses = new Dictionary<string, string>();
            _matchResponses = new Dictionary<string, string>();
            _metadataErrors = new Queue<System.Net.HttpStatusCode>();
            _putErrors = new Queue<System.Net.HttpStatusCode>();
            Mocker.SetConstant<IMediaServerItemLinkStore>(new InMemoryMediaServerItemLinkStore());
            _requests = new List<HttpRequest>();
            _movie = new Movie { Title = "Test Movie", TmdbId = 123, ImdbId = "tt0123456", Year = 2020 };
            _settings = new PlexServerSettings { Host = "localhost", UseSsl = true, UrlBase = "/plex", AuthToken = "plex-token" };
            _updateResponse = string.Empty;
            _sectionsResponse = """{"MediaContainer":{"Directory":[{"key":1,"type":"movie","agent":"tv.plex.agents.movie","language":"cs-CZ"}]}}""";
            GivenMatches(PlexGuid);
            Mocker.GetMock<IConfigService>().SetupGet(c => c.PlexClientIdentifier).Returns("radarr-client");

            Mocker.GetMock<IHttpClient>().Setup(c => c.Execute(It.IsAny<HttpRequest>())).Returns<HttpRequest>(request =>
            {
                _requests.Add(request);
                string response;

                if (request.Method == HttpMethod.Put)
                {
                    ThrowQueuedError(request, _putErrors);
                    response = _updateResponse;
                }
                else if (request.Url.Path.EndsWith("/matches", StringComparison.Ordinal))
                {
                    var agent = Uri.UnescapeDataString(request.Url.Query).TrimStart('?').Split('&')
                        .Single(part => part.StartsWith("agent=", StringComparison.Ordinal)).Substring(6);
                    response = _matchResponses.TryGetValue(agent, out var matches) ? matches : _matchResponse;
                }
                else if (request.Url.Path.Contains("/library/metadata/", StringComparison.Ordinal))
                {
                    ThrowQueuedError(request, _metadataErrors);
                    var ids = Uri.UnescapeDataString(request.Url.Path.Split('/').Last()).Split(',');
                    var items = ids.Where(_metadata.ContainsKey).Select(id => (JsonNode)_metadata[id].DeepClone()).ToArray();
                    response = new JsonObject
                    {
                        ["MediaContainer"] = new JsonObject { ["Metadata"] = new JsonArray(items) }
                    }.ToJsonString();
                }
                else if (request.Url.Path.EndsWith("/library/sections", StringComparison.Ordinal))
                {
                    response = _sectionsResponse;
                }
                else if (request.Url.Path.Contains("/library/sections/", StringComparison.Ordinal) &&
                         request.Url.Path.EndsWith("/all", StringComparison.Ordinal))
                {
                    response = _sampleResponses.TryGetValue(request.Url.Path, out var sample)
                        ? sample
                        : """{"MediaContainer":{"totalSize":14500,"Metadata":[{"ratingKey":"999","type":"movie","year":1999}]}}""";
                }
                else if (request.Url.Path.EndsWith("/library/all", StringComparison.Ordinal))
                {
                    response = _pages.Dequeue();
                }
                else
                {
                    throw new InvalidOperationException("Unexpected Plex request: " + request.Url.Path);
                }

                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, response);
            });
        }

        private static void ThrowQueuedError(HttpRequest request, Queue<System.Net.HttpStatusCode> errors)
        {
            if (errors.Count > 0)
            {
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, errors.Dequeue()));
            }
        }

        private static JsonObject Item(string id, string guid, string type = "movie", int sectionId = 1, string plexGuid = PlexGuid)
        {
            return new JsonObject
            {
                ["ratingKey"] = id,
                ["type"] = type,
                ["title"] = "Test Movie",
                ["guid"] = plexGuid,
                ["librarySectionID"] = sectionId,
                ["Guid"] = new JsonArray(new JsonObject { ["id"] = guid })
            };
        }

        private void GivenMatches(params string[] guids)
        {
            _matchResponse = new JsonObject
            {
                ["MediaContainer"] = new JsonObject
                {
                    ["SearchResult"] = new JsonArray(guids.Select(guid => (JsonNode)new JsonObject { ["type"] = "movie", ["guid"] = guid }).ToArray())
                }
            }.ToJsonString();
        }

        private void GivenPage(int total, params JsonObject[] items)
        {
            foreach (var item in items)
            {
                _metadata[item["ratingKey"].ToString()] = (JsonObject)item.DeepClone();
            }

            _pages.Enqueue(new JsonObject
            {
                ["MediaContainer"] = new JsonObject
                {
                    ["totalSize"] = total,
                    ["Metadata"] = new JsonArray(items)
                }
            }.ToJsonString());
        }

        [Test]
        public void should_resolve_the_external_identity_and_query_the_exact_global_guid_without_a_release_year()
        {
            _movie.Year = 0;
            GivenPage(1, Item("10", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            var sample = _requests.Single(r => r.Method == HttpMethod.Get && r.Url.Path.EndsWith("/sections/1/all", StringComparison.Ordinal));
            sample.Headers.GetSingleValue("X-Plex-Container-Size").Should().Be("1");
            sample.Headers.GetSingleValue("X-Plex-Container-Start").Should().Be("0");
            sample.Url.Query.Should().NotContain("year=");
            var match = _requests.Single(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal));
            Uri.UnescapeDataString(match.Url.Query).Should().Contain("manual=1").And.Contain("title=tmdb-123")
                .And.Contain("year=1999").And.Contain("language=cs-CZ").And.Contain("agent=tv.plex.agents.movie");
            var global = _requests.Single(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            Uri.UnescapeDataString(global.Url.Query).Should().Contain("type=1").And.Contain("guid=" + PlexGuid).And.Contain("includeGuids=1");
            global.Headers.GetSingleValue("X-Plex-Container-Size").Should().Be("500");
            var update = _requests.Single(r => r.Method == HttpMethod.Put);
            update.Url.FullUri.Should().StartWith("https://localhost:32400/plex/library/sections/1/all?");
            update.Url.Query.Should().Contain("type=1").And.Contain("id=10").And.Contain("addedAt.value=").And.Contain("X-Plex-Token=plex-token");
            update.Url.Query.Should().NotContain("addedAt.locked");
        }

        [Test]
        public void should_use_imdb_when_tmdb_is_unavailable()
        {
            _movie.TmdbId = 0;
            GivenPage(1, Item("10", "imdb://tt0123456"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            Uri.UnescapeDataString(_requests.Single(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal)).Url.Query)
                .Should().Contain("title=imdb-tt0123456");
            _requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_find_all_copies_with_one_global_query_and_keep_their_sections()
        {
            GivenPage(4,
                Item("10", "tmdb://123"),
                Item("11", "tmdb://123"),
                Item("20", "tmdb://123", sectionId: 2),
                Item("21", "tmdb://123", sectionId: 2));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Should().ContainSingle(r => r.Method == HttpMethod.Get && r.Url.Path.EndsWith("/sections/1/all", StringComparison.Ordinal));
            var updates = _requests.Where(r => r.Method == HttpMethod.Put).ToList();
            updates.Should().HaveCount(2);
            Uri.UnescapeDataString(updates.Single(r => r.Url.Path.EndsWith("/sections/1/all", StringComparison.Ordinal)).Url.Query)
                .Should().Contain("id=10,11");
            Uri.UnescapeDataString(updates.Single(r => r.Url.Path.EndsWith("/sections/2/all", StringComparison.Ordinal)).Url.Query)
                .Should().Contain("id=20,21");
        }

        [Test]
        public void should_validate_each_ambiguous_guid_candidate_against_the_external_identity()
        {
            GivenMatches("plex://movie/wrong-candidate", PlexGuid);
            GivenPage(1, Item("10", "tmdb://999", plexGuid: "plex://movie/wrong-candidate"));
            GivenPage(1, Item("20", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal)).Should().Be(2);
            _requests.Single(r => r.Method == HttpMethod.Put).Url.Query.Should().Contain("id=20");
            _requests.Single(r => r.Url.Path.Contains("/library/metadata/", StringComparison.Ordinal) &&
                !r.Url.Path.EndsWith("/matches", StringComparison.Ordinal)).Url.Path.Should().EndWith("/20");
        }

        [TestCase("tmdb://1234", "movie", 1)]
        [TestCase("tmdb://123", "episode", 1)]
        [TestCase("tmdb://123", "movie", 0)]
        public void should_not_update_a_wrong_provider_type_or_invalid_section(string providerGuid, string type, int sectionId)
        {
            GivenPage(1, Item("10", providerGuid, type, sectionId));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [TestCase("com.plexapp.agents.themoviedb://123?lang=en")]
        [TestCase("com.plexapp.agents.imdb://tt0123456?lang=en")]
        public void should_accept_exact_legacy_provider_guids_after_targeted_discovery(string providerGuid)
        {
            GivenPage(1, Item("10", providerGuid));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_page_only_exact_guid_results()
        {
            GivenPage(2, Item("10", "tmdb://123"));
            GivenPage(2, Item("11", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            var pages = _requests.Where(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal)).ToList();
            pages.Should().HaveCount(2);
            pages[1].Headers.GetSingleValue("X-Plex-Container-Start").Should().Be("1");
            pages.Should().OnlyContain(r => Uri.UnescapeDataString(r.Url.Query).Contains("guid=" + PlexGuid, StringComparison.Ordinal));
            _requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_stop_without_writes_if_plex_ignores_the_guid_filter()
        {
            GivenPage(2, Item("10", "tmdb://123", plexGuid: "plex://movie/unrelated"));
            GivenPage(2, Item("11", "tmdb://123"));

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<PlexException>();
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_reject_unbounded_global_results_without_loading_more_pages()
        {
            GivenPage(14500, Item("10", "tmdb://123"));

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<PlexException>();
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_reject_repeated_global_pages_without_writes()
        {
            GivenPage(3, Item("10", "tmdb://123"));
            GivenPage(3, Item("10", "tmdb://123"));

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<PlexException>();
            _requests.Count(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal)).Should().Be(2);
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_bound_ambiguous_identity_candidates()
        {
            GivenMatches(Enumerable.Range(0, 21).Select(index => "plex://movie/candidate-" + index).ToArray());

            for (var candidate = 0; candidate < 20; candidate++)
            {
                GivenPage(0);
            }

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal)).Should().Be(20);
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_skip_empty_sections_and_use_only_one_movie_sample()
        {
            _sectionsResponse = """{"MediaContainer":{"Directory":[{"key":1,"type":"movie","agent":"tv.plex.agents.movie","language":"cs-CZ"},{"key":2,"type":"movie","agent":"tv.plex.agents.movie","language":"en-US"}]}}""";
            _sampleResponses["/plex/library/sections/1/all"] = """{"MediaContainer":{"Metadata":[]}}""";
            GivenPage(1, Item("10", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Method == HttpMethod.Get && r.Url.Path.Contains("/library/sections/", StringComparison.Ordinal)).Should().Be(2);
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal));
            Uri.UnescapeDataString(_requests.Single(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal)).Url.Query)
                .Should().Contain("language=en-US");
        }

        [Test]
        public void should_try_each_agent_once_after_an_empty_identity_resolution()
        {
            _sectionsResponse = """{"MediaContainer":{"Directory":[{"key":1,"type":"movie","agent":"legacy.agent","language":"en"},{"key":2,"type":"movie","agent":"legacy.agent","language":"en"},{"key":3,"type":"movie","agent":"tv.plex.agents.movie","language":"cs-CZ"}]}}""";
            _matchResponses["legacy.agent"] = """{"MediaContainer":{"SearchResult":[]}}""";
            GivenPage(1, Item("10", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            var samples = _requests.Where(r => r.Method == HttpMethod.Get && r.Url.Path.Contains("/library/sections/", StringComparison.Ordinal)).ToList();
            samples.Should().HaveCount(2);
            samples.Should().NotContain(r => r.Url.Path.Contains("/sections/2/", StringComparison.Ordinal));
            _requests.Count(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal)).Should().Be(2);
            _requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_not_repeat_an_unsuccessful_agent_in_every_movie_section()
        {
            _sectionsResponse = new JsonObject
            {
                ["MediaContainer"] = new JsonObject
                {
                    ["Directory"] = new JsonArray(Enumerable.Range(1, 9).Select(id => (JsonNode)new JsonObject
                    {
                        ["key"] = id,
                        ["type"] = "movie",
                        ["agent"] = "tv.plex.agents.movie",
                        ["language"] = "cs-CZ"
                    }).ToArray())
                }
            }.ToJsonString();
            GivenMatches();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().HaveCount(3);
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal));
            _requests.Should().NotContain(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
        }

        [Test]
        public void should_skip_an_empty_server_without_global_queries()
        {
            _sectionsResponse = """{"MediaContainer":{"Directory":[]}}""";

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().ContainSingle();
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_stop_after_empty_identity_matches()
        {
            GivenMatches();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().NotContain(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [Test]
        public void should_skip_cold_discovery_without_external_ids()
        {
            _movie.TmdbId = 0;
            _movie.ImdbId = null;

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().BeEmpty();
        }

        [Test]
        public void should_preserve_agent_and_language_from_legacy_sections()
        {
            _sectionsResponse = """{"_children":[{"key":1,"type":"movie","agent":"tv.plex.agents.movie","language":"cs-CZ","_children":[]}]}""";

            var sections = Subject.GetMovieSections(_settings);

            sections.Should().ContainSingle();
            sections[0].Agent.Should().Be("tv.plex.agents.movie");
            sections[0].Language.Should().Be("cs-CZ");
        }

        [Test]
        public void should_reuse_linked_ids_after_a_replacement_without_any_library_search()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();
            _movie.MovieFile = new NzbDrone.Core.MediaFiles.MovieFile { RelativePath = "new-replacement-filename.mkv" };
            _movie.Year = 0;

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Path.Should().EndWith("/library/metadata/10");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [Test]
        public void should_validate_multiple_linked_items_in_one_metadata_request()
        {
            GivenPage(2, Item("10", "tmdb://123"), Item("11", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().HaveCount(2);
            Uri.UnescapeDataString(_requests[0].Url.Path).Should().EndWith("/library/metadata/10,11");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [Test]
        public void should_rediscover_a_missing_link_once()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();
            _metadataErrors.Enqueue(System.Net.HttpStatusCode.NotFound);
            GivenPage(1, Item("20", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Url.Path.EndsWith("/library/sections", StringComparison.Ordinal)).Should().Be(1);
            _requests.Count(r => r.Method == HttpMethod.Get && r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal)).Should().Be(1);
            _requests.Single(r => r.Method == HttpMethod.Put).Url.Query.Should().Contain("id=20");
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal));
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Path.Should().EndWith("/library/metadata/20");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [TestCase("guid")]
        [TestCase("type")]
        [TestCase("section")]
        public void should_rediscover_a_link_whose_identity_changed(string change)
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();

            if (change == "guid")
            {
                _metadata["10"]["Guid"] = new JsonArray(new JsonObject { ["id"] = "tmdb://999" });
            }
            else if (change == "type")
            {
                _metadata["10"]["type"] = "episode";
            }
            else
            {
                _metadata["10"]["librarySectionID"] = 999;
            }

            GivenPage(1, Item("20", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Url.Path.EndsWith("/library/sections", StringComparison.Ordinal)).Should().Be(1);
            _requests.Single(r => r.Method == HttpMethod.Put).Url.Query.Should().Contain("id=20");
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/matches", StringComparison.Ordinal));
            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/all", StringComparison.Ordinal));
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Path.Should().EndWith("/library/metadata/20");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [Test]
        public void should_stop_after_one_rediscovery_when_the_new_link_is_also_invalid()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();
            _metadataErrors.Enqueue(System.Net.HttpStatusCode.NotFound);
            GivenPage(1, Item("20", "tmdb://123"));
            _metadata["20"]["type"] = "episode";

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Count(r => r.Url.Path.EndsWith("/library/sections", StringComparison.Ordinal)).Should().Be(1);
            _requests.Should().NotContain(r => r.Method == HttpMethod.Put);
        }

        [TestCase(System.Net.HttpStatusCode.Unauthorized)]
        [TestCase(System.Net.HttpStatusCode.Forbidden)]
        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        public void should_preserve_a_link_without_searching_when_metadata_access_fails(System.Net.HttpStatusCode status)
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();
            _metadataErrors.Enqueue(status);

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<Exception>();
            _requests.Should().ContainSingle();
            _requests[0].Url.Path.Should().EndWith("/library/metadata/10");
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Should().HaveCount(2);
        }

        [Test]
        public void should_preserve_a_discovered_link_when_the_date_write_fails()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            _putErrors.Enqueue(System.Net.HttpStatusCode.InternalServerError);

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<Exception>();
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Should().HaveCount(2);
            _requests[0].Url.Path.Should().EndWith("/library/metadata/10");
        }

        [Test]
        public void should_suppress_repeated_missing_movie_searches_temporarily()
        {
            GivenPage(0);
            GivenPage(0);
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().BeEmpty();
        }

        [Test]
        public void should_discover_new_links_after_the_token_changes()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            Subject.MarkAsRecentlyAdded(_movie, _settings);
            _requests.Clear();
            _settings.AuthToken = "replacement-token";
            GivenPage(1, Item("20", "tmdb://123"));

            Subject.MarkAsRecentlyAdded(_movie, _settings);

            _requests.Should().ContainSingle(r => r.Url.Path.EndsWith("/library/sections", StringComparison.Ordinal));
            _requests.Single(r => r.Method == HttpMethod.Put).Url.Query.Should().Contain("id=20");
        }

        [Test]
        public void should_report_error_payloads_from_date_updates()
        {
            GivenPage(1, Item("10", "tmdb://123"));
            _updateResponse = """{"MediaContainer":{"error":"Metadata update denied"}}""";

            Action action = () => Subject.MarkAsRecentlyAdded(_movie, _settings);

            action.Should().Throw<PlexException>();
        }
    }
}
