using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class DualAudioImportPreferenceFixture : CoreTest<DualAudioImportPreference>
    {
        private Movie _movie;
        private LocalMovie _localMovie;
        private MovieFile _existingMovieFile;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie
            {
                QualityProfile = new QualityProfile
                {
                    Items = Qualities.QualityFixture.GetDefaultQualities()
                }
            };

            _movie.MovieMetadata.Value.OriginalLanguage = Language.French;

            _localMovie = new LocalMovie
            {
                Movie = _movie,
                Size = 800,
                Quality = new QualityModel(Quality.WEBDL1080p),
                Languages = new List<Language> { Language.Czech, Language.English }
            };

            _existingMovieFile = new MovieFile
            {
                Size = 1000,
                Quality = new QualityModel(Quality.WEBDL1080p),
                Languages = new List<Language> { Language.English }
            };

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.PreferDualAudio)
                .Returns(true);

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.MovieInfoLanguage)
                .Returns((int)Language.Czech);
        }

        [Test]
        public void should_not_apply_when_disabled()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.PreferDualAudio)
                .Returns(false);

            Subject.Evaluate(_localMovie, _existingMovieFile).Applies.Should().BeFalse();
        }

        [Test]
        public void should_prefer_candidate_with_movie_info_language_and_another_audio_language()
        {
            var result = Subject.Evaluate(_localMovie, _existingMovieFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeTrue();
            result.RequiresManualReview.Should().BeFalse();
        }

        [Test]
        public void should_not_apply_when_existing_file_already_has_preferred_dual_audio()
        {
            _existingMovieFile.Languages = new List<Language> { Language.Czech, Language.English };

            Subject.Evaluate(_localMovie, _existingMovieFile).Applies.Should().BeFalse();
        }

        [Test]
        public void should_require_manual_review_when_candidate_is_lower_quality()
        {
            _localMovie.Quality = new QualityModel(Quality.HDTV720p);
            _existingMovieFile.Quality = new QualityModel(Quality.WEBDL1080p);

            var result = Subject.Evaluate(_localMovie, _existingMovieFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeFalse();
            result.RequiresManualReview.Should().BeTrue();
            result.ManualReviewReason.Should().Contain("lower quality");
        }

        [Test]
        public void should_require_manual_review_when_candidate_is_more_than_30_percent_smaller()
        {
            _localMovie.Size = 699;

            var result = Subject.Evaluate(_localMovie, _existingMovieFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeFalse();
            result.RequiresManualReview.Should().BeTrue();
            result.ManualReviewReason.Should().Contain("30% smaller");
        }

        [Test]
        public void should_count_unknown_secondary_audio_from_media_info()
        {
            _localMovie.Languages = new List<Language> { Language.Czech };
            _localMovie.MediaInfo = new MediaInfoModel
            {
                AudioLanguages = new List<string> { "ces", "pirate" }
            };

            var result = Subject.Evaluate(_localMovie, _existingMovieFile);

            result.IsPreferredUpgrade.Should().BeTrue();
        }
    }
}
