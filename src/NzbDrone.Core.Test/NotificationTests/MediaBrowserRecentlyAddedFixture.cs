using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Emby;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class MediaBrowserRecentlyAddedFixture : CoreTest<MediaBrowserProxy>
    {
        private Queue<string> _pages;
        private Queue<System.Net.HttpStatusCode> _metadataErrors;
        private Queue<System.Net.HttpStatusCode> _postErrors;
        private List<HttpRequest> _requests;
        private JsonObject _metadata;
        private Movie _movie;
        private MediaBrowserSettings _settings;

        [SetUp]
        public void Setup()
        {
            _pages = new Queue<string>();
            _metadataErrors = new Queue<System.Net.HttpStatusCode>();
            _postErrors = new Queue<System.Net.HttpStatusCode>();
            Mocker.SetConstant<IMediaServerItemLinkStore>(new InMemoryMediaServerItemLinkStore());
            _requests = new List<HttpRequest>();
            _movie = new Movie
            {
                Title = "Test Movie",
                TmdbId = 123,
                ImdbId = "tt0123456",
                Year = 2020,
                Path = @"C:\Test\Movies\Test Movie".AsOsAgnostic()
            };
            _settings = new MediaBrowserSettings
            {
                Host = "localhost",
                UseSsl = true,
                UrlBase = "/jellyfin",
                ApiKey = "jellyfin-token"
            };
            _metadata = new JsonObject
            {
                ["Id"] = "movie-id",
                ["Name"] = "Test Movie",
                ["Type"] = "Movie",
                ["DateCreated"] = "2001-01-01T00:00:00Z",
                ["LockData"] = true,
                ["LockedFields"] = new JsonArray("Overview", "DateCreated"),
                ["Tags"] = new JsonArray("Keep Tag"),
                ["Genres"] = new JsonArray("Drama"),
                ["Overview"] = null,
                ["ForcedSortName"] = "Keep Sort",
                ["PreferredMetadataLanguage"] = "cs",
                ["PreferredMetadataCountryCode"] = "CZ",
                ["People"] = new JsonArray(new JsonObject { ["Name"] = "Keep Actor", ["Role"] = "Keep Role" }),
                ["ProviderIds"] = new JsonObject { ["Tmdb"] = "123", ["Imdb"] = "tt0123456" },
                ["UnknownFutureField"] = new JsonObject { ["Keep"] = true }
            };

            Mocker.GetMock<IHttpClient>().Setup(c => c.Get(It.IsAny<HttpRequest>())).Returns<HttpRequest>(request =>
            {
                _requests.Add(request);

                if (request.Url.Query.Contains("ids=", StringComparison.Ordinal))
                {
                    ThrowQueuedError(request, _metadataErrors);
                }

                var content = request.Url.Query.Contains("ids=", StringComparison.Ordinal)
                    ? new JsonObject { ["Items"] = new JsonArray(_metadata.DeepClone()) }.ToJsonString()
                    : _pages.Dequeue();
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content);
            });
            Mocker.GetMock<IHttpClient>().Setup(c => c.Post(It.IsAny<HttpRequest>())).Returns<HttpRequest>(request =>
            {
                request.Method = System.Net.Http.HttpMethod.Post;
                _requests.Add(request);
                ThrowQueuedError(request, _postErrors);
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, string.Empty);
            });
        }

        private static void ThrowQueuedError(HttpRequest request, Queue<System.Net.HttpStatusCode> errors)
        {
            if (errors.Count > 0)
            {
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, errors.Dequeue()));
            }
        }

        private JsonObject Item(string id = "movie-id", string tmdb = "123")
        {
            return new JsonObject
            {
                ["Id"] = id,
                ["Name"] = "Test Movie",
                ["Type"] = "Movie",
                ["Path"] = Path.Combine(_movie.Path, "old-filename.mkv"),
                ["ProviderIds"] = new JsonObject { ["Tmdb"] = tmdb }
            };
        }

        private void GivenPage(int total, params JsonObject[] items)
        {
            _pages.Enqueue(new JsonObject
            {
                ["TotalRecordCount"] = total,
                ["Items"] = new JsonArray(items)
            }.ToJsonString());
        }

        [Test]
        public void should_change_only_date_created_and_preserve_raw_metadata()
        {
            GivenPage(1, Item());
            var before = DateTime.UtcNow;

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            var metadataRequest = _requests.Single(r => r.Url.Query.Contains("ids=", StringComparison.Ordinal));
            Uri.UnescapeDataString(metadataRequest.Url.Query).Should().Contain("Settings").And.Contain("SpecialEpisodeNumbers");
            metadataRequest.Url.Query.Should().Contain("collapseBoxSetItems=false");
            metadataRequest.Headers.GetSingleValue("X-MediaBrowser-Token").Should().Be("jellyfin-token");
            var update = _requests.Single(r => r.Method == System.Net.Http.HttpMethod.Post);
            update.Url.FullUri.Should().Be("https://localhost:8096/jellyfin/Items/movie-id");
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(update.ContentData)).AsObject();
            DateTime.Parse(payload["DateCreated"].GetValue<string>()).ToUniversalTime().Should().BeOnOrAfter(before);
            payload.Remove("DateCreated");
            var original = (JsonObject)_metadata.DeepClone();
            original.Remove("DateCreated");
            JsonNode.DeepEquals(payload, original).Should().BeTrue();
        }

        [Test]
        public void should_use_the_year_shortlist_before_loading_full_metadata()
        {
            GivenPage(1, Item());

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            var searches = _requests.Where(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal)).ToList();
            searches.Should().ContainSingle();
            searches[0].Url.Query.Should().Contain("years=2020").And.Contain("collapseBoxSetItems=false");
        }

        [Test]
        public void should_find_exact_ids_on_later_year_filtered_pages()
        {
            GivenPage(2, Item("other-id", "1234"));
            GivenPage(2, Item());

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            var searches = _requests.Where(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal)).ToList();
            searches.Should().HaveCount(2);
            searches.Should().OnlyContain(r => r.Url.Query.Contains("years=2020", StringComparison.Ordinal));
            searches[1].Url.Query.Should().Contain("startIndex=1");
            _requests.Should().ContainSingle(r => r.Method == System.Net.Http.HttpMethod.Post);
        }

        [TestCase("1234", "Movie")]
        [TestCase("123", "Episode")]
        public void should_not_change_dates_for_a_title_conflicting_id_or_wrong_type_match(string tmdb, string type)
        {
            for (var page = 0; page < 2; page++)
            {
                var item = Item("other-id", tmdb);
                item["Type"] = type;
                GivenPage(1, item);
            }

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().NotContain(r => r.Method == System.Net.Http.HttpMethod.Post);
        }

        [Test]
        public void should_match_the_mapped_movie_folder_when_provider_ids_are_absent()
        {
            _settings.MapFrom = @"C:\Test\Movies".AsOsAgnostic();
            _settings.MapTo = @"D:\Library".AsOsAgnostic();
            var item = Item();
            item["ProviderIds"] = new JsonObject();
            item["Path"] = Path.Combine(_settings.MapTo, "Test Movie", "old-filename.mkv");
            GivenPage(1, item);

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().ContainSingle(r => r.Method == System.Net.Http.HttpMethod.Post);
        }

        [TestCase("LockData")]
        [TestCase("LockedFields")]
        [TestCase("Tags")]
        [TestCase("Genres")]
        [TestCase("Name")]
        public void should_not_write_incomplete_metadata(string field)
        {
            GivenPage(1, Item());
            _metadata.Remove(field);

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().NotContain(r => r.Method == System.Net.Http.HttpMethod.Post);
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase("Id", "different-id")]
        [TestCase("Type", "Episode")]
        public void should_not_write_metadata_for_a_different_item(string field, string value)
        {
            GivenPage(1, Item());
            _metadata[field] = value;

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().NotContain(r => r.Method == System.Net.Http.HttpMethod.Post);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_scan_the_full_library_when_the_year_shortlist_is_empty()
        {
            GivenPage(0);

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().ContainSingle();
            _requests[0].Url.Query.Should().Contain("years=2020");
            _requests.Should().NotContain(r => r.Method == System.Net.Http.HttpMethod.Post);
        }

        [Test]
        public void should_skip_discovery_without_a_release_year()
        {
            _movie.Year = 0;

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().BeEmpty();
        }

        [Test]
        public void should_reuse_a_link_and_fresh_metadata_after_a_replacement_without_searching()
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _movie.MovieFile = new NzbDrone.Core.MediaFiles.MovieFile { RelativePath = "new-replacement-filename.mkv" };
            _movie.Year = 0;
            _metadata["Tags"] = new JsonArray("Edited outside Radarr");
            _metadata["Overview"] = null;

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Query.Should().Contain("ids=movie-id");
            var update = _requests.Single(r => r.Method == System.Net.Http.HttpMethod.Post);
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(update.ContentData)).AsObject();
            payload.Remove("DateCreated");
            var expected = (JsonObject)_metadata.DeepClone();
            expected.Remove("DateCreated");
            JsonNode.DeepEquals(payload, expected).Should().BeTrue();
        }

        [Test]
        public void should_rediscover_a_missing_link_only_once()
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _metadataErrors.Enqueue(System.Net.HttpStatusCode.NotFound);
            GivenPage(1, Item("new-id"));
            _metadata["Id"] = "new-id";

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            var searches = _requests.Where(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal)).ToList();
            searches.Should().ContainSingle();
            _requests.Single(r => r.Method == System.Net.Http.HttpMethod.Post).Url.Path.Should().EndWith("/Items/new-id");
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Query.Should().Contain("ids=new-id");
            _requests[1].Method.Should().Be(System.Net.Http.HttpMethod.Post);
            _requests[1].Url.Path.Should().EndWith("/Items/new-id");
        }

        [TestCase("Id")]
        [TestCase("Type")]
        [TestCase("ProviderIds")]
        public void should_rediscover_a_link_with_a_changed_identity(string field)
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            var calls = 0;
            Mocker.GetMock<IHttpClient>().Setup(c => c.Get(It.Is<HttpRequest>(r => r.Url.Query.Contains("ids=", StringComparison.Ordinal)))).Returns<HttpRequest>(request =>
            {
                _requests.Add(request);
                var item = (JsonObject)_metadata.DeepClone();

                if (calls++ == 0)
                {
                    if (field == "ProviderIds")
                    {
                        item[field] = new JsonObject { ["Tmdb"] = "999" };
                    }
                    else
                    {
                        item[field] = field == "Id" ? "different-id" : "Episode";
                    }
                }
                else
                {
                    item["Id"] = "new-id";
                }

                return new HttpResponse(request, new HttpHeader(), new JsonObject { ["Items"] = new JsonArray(item) }.ToJsonString());
            });
            GivenPage(1, Item("new-id"));

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Count(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal)).Should().Be(1);
            _requests.Single(r => r.Method == System.Net.Http.HttpMethod.Post).Url.Path.Should().EndWith("/Items/new-id");
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().HaveCount(2);
            _requests[0].Url.Query.Should().Contain("ids=new-id");
            _requests[1].Method.Should().Be(System.Net.Http.HttpMethod.Post);
            _requests[1].Url.Path.Should().EndWith("/Items/new-id");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_stop_after_one_rediscovery_when_new_metadata_also_has_the_wrong_identity()
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _metadataErrors.Enqueue(System.Net.HttpStatusCode.NotFound);
            GivenPage(1, Item("new-id"));
            _metadata["Id"] = "new-id";
            _metadata["Type"] = "Episode";

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Count(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal)).Should().Be(1);
            _requests.Should().NotContain(r => r.Method == System.Net.Http.HttpMethod.Post);
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase(System.Net.HttpStatusCode.Unauthorized)]
        [TestCase(System.Net.HttpStatusCode.Forbidden)]
        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        public void should_keep_the_link_without_searching_after_metadata_access_fails(System.Net.HttpStatusCode status)
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _metadataErrors.Enqueue(status);

            Action action = () => Subject.MarkAsRecentlyAdded(_settings, _movie);

            action.Should().Throw<HttpException>();
            _requests.Should().ContainSingle();
            _requests[0].Url.Query.Should().Contain("ids=movie-id");
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Should().HaveCount(2);
        }

        [Test]
        public void should_keep_the_discovered_link_if_the_date_write_fails()
        {
            GivenPage(1, Item());
            _postErrors.Enqueue(System.Net.HttpStatusCode.InternalServerError);

            Action action = () => Subject.MarkAsRecentlyAdded(_settings, _movie);

            action.Should().Throw<HttpException>();
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Should().HaveCount(2);
            _requests[0].Url.Query.Should().Contain("ids=movie-id");
        }

        [Test]
        public void should_not_evict_a_link_or_search_when_metadata_is_incomplete()
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _metadata.Remove("LockData");

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().ContainSingle();
            _requests[0].Url.Query.Should().Contain("ids=movie-id");
            _requests.Clear();
            _metadata["LockData"] = true;
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Should().HaveCount(2);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_suppress_repeated_missing_movie_searches_temporarily()
        {
            GivenPage(0);
            GivenPage(0);
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().BeEmpty();
        }

        [Test]
        public void should_discover_new_links_after_the_token_changes()
        {
            GivenPage(1, Item());
            Subject.MarkAsRecentlyAdded(_settings, _movie);
            _requests.Clear();
            _settings.ApiKey = "replacement-token";
            GivenPage(1, Item("new-id"));
            _metadata["Id"] = "new-id";

            Subject.MarkAsRecentlyAdded(_settings, _movie);

            _requests.Should().ContainSingle(r => r.Method == System.Net.Http.HttpMethod.Get && !r.Url.Query.Contains("ids=", StringComparison.Ordinal));
            _requests.Single(r => r.Method == System.Net.Http.HttpMethod.Post).Url.Path.Should().EndWith("/Items/new-id");
        }
    }
}
