using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MetadataSource.SkyHook
{
    [TestFixture]
    [IntegrationTest]
    public class SkyHookProxySearchFixture : CoreTest<SkyHookProxy>
    {
        [SetUp]
        public void Setup()
        {
            UseRealHttp();
        }

        [TestCase("Prometheus", "Prometheus")]

        // TODO: TMDB Doesn't like when we clean periods from this
        // [TestCase("The Man from U.N.C.L.E.", "The Man from U.N.C.L.E.")]
        [TestCase("imdb:tt2527336", "Star Wars: The Last Jedi")]
        [TestCase("imdb:tt2798920", "Annihilation")]
        [TestCase("https://www.imdb.com/title/tt0033467/", "Citizen Kane")]
        [TestCase("https://www.themoviedb.org/movie/775-le-voyage-dans-la-lune", "A Trip to the Moon")]
        public void successful_search(string title, string expected)
        {
            var result = Subject.SearchForNewMovie(title);

            result.Should().NotBeEmpty();

            result[0].Title.Should().Be(expected);

            ExceptionVerification.IgnoreWarns();
        }

        [Test]
        public void should_search_using_movie_info_language()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.MovieInfoLanguage)
                .Returns((int)Language.Czech);

            var result = Subject.SearchForNewMovie("Vykoupení z věznice Shawshank");

            result.Should().NotBeEmpty();

            result[0].TmdbId.Should().Be(278);

            ExceptionVerification.IgnoreWarns();
        }

        [TestCase("tmdbid:")]
        [TestCase("tmdbid: 99999999999999999999")]
        [TestCase("tmdbid: 0")]
        [TestCase("tmdbid: -12")]
        [TestCase("tmdbid:1")]
        [TestCase("adjalkwdjkalwdjklawjdlKAJD;EF")]
        [TestCase("imdb: tt9805708")]
        [TestCase("https://www.UNKNOWN-DOMAIN.com/title/tt0033467/")]
        [TestCase("https://www.themoviedb.org/MALFORMED/775-le-voyage-dans-la-lune")]
        public void no_search_result(string term)
        {
            var result = Subject.SearchForNewMovie(term);
            result.Should().BeEmpty();

            ExceptionVerification.IgnoreWarns();
        }
    }
}
