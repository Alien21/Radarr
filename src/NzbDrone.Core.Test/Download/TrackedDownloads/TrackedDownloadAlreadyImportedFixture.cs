using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private Movie _movie;
        private List<MovieFile> _movieFiles;
        private TrackedDownload _trackedDownload;
        private List<MovieHistory> _historyItems;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                   .With(m => m.MovieFileId = 1001)
                                   .Build();

            _movieFiles = new List<MovieFile>
            {
                new MovieFile
                {
                    Id = _movie.MovieFileId,
                    MovieId = _movie.Id,
                    Size = 1000000
                }
            };

            var remoteMovie = Builder<RemoteMovie>.CreateNew()
                                                      .With(r => r.Movie = _movie)
                                                      .Build();

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                         .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(t => t.RemoteMovie = remoteMovie)
                                                       .With(t => t.DownloadItem = downloadItem)
                                                       .Build();

            _historyItems = new List<MovieHistory>();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns<IEnumerable<int>>(ids => _movieFiles.Where(f => ids.Contains(f.Id)).ToList());
        }

        public void GivenHistoryForMovie(Movie movie, params MovieHistoryEventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                var history = Builder<MovieHistory>.CreateNew()
                                                   .With(h => h.MovieId = movie.Id)
                                                   .With(h => h.EventType = eventType)
                                                   .Build();

                if (eventType == MovieHistoryEventType.DownloadFolderImported)
                {
                    var movieFile = _movieFiles.Single(f => f.Id == movie.MovieFileId);

                    history.Data["FileId"] = movieFile.Id.ToString();
                    history.Data["Size"] = movieFile.Size.ToString();
                }

                _historyItems.Add(history);
            }
        }

        [Test]
        public void should_return_false_if_there_is_no_history()
        {
            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_single_movie_download_is_not_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_imported_file_size_does_not_match_existing_file()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.Grabbed);
            _historyItems.First(h => h.EventType == MovieHistoryEventType.DownloadFolderImported).Data["Size"] = (_movieFiles[0].Size + 1).ToString();

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_existing_movie_file_is_missing()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.Grabbed);
            _movieFiles.Clear();

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_true_if_single_movie_download_is_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }
    }
}
