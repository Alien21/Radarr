using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.1998")
                                                    .Build();

            var remoteMovie = BuildRemoteMovie();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteMovie = remoteMovie)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.MostRecentForDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new MovieHistory());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.1998"))
                  .Returns(remoteMovie.Movie);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);
        }

        private RemoteMovie BuildRemoteMovie()
        {
            return new RemoteMovie
            {
                Movie = new Movie()
            };
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
               .Setup(s => s.MostRecentForDownloadId(It.Is<string>(i => i == "1234")))
               .Returns(new MovieHistory() { SourceTitle = "Droned 1998" });

            Mocker.GetMock<IParsingService>()
               .Setup(s => s.GetMovie(It.IsAny<string>()))
               .Returns((Movie)null);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetMovie("Droned 1998"))
                .Returns(BuildRemoteMovie().Movie);
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteMovie.Movie);
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_rejected()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1999.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent<DownloadCompletedEvent>(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_no_movies_were_parsed()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_skipped()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_movies_were_imported_but_extra_files_were_not()
        {
            GivenSeriesMatch();

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie })),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
               });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_the_download_can_be_tracked_using_the_source_movieid()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie }))
               });

            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovie(It.IsAny<int>()))
                  .Returns(BuildRemoteMovie().Movie);

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_block_auto_import_if_movie_already_has_file()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingMovieFiles)
                .Returns(true);

            _trackedDownload.RemoteMovie.Movie.MovieFileId = 1;

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_mark_as_imported_if_existing_movie_block_finds_movie_imported_in_history()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingMovieFiles)
                .Returns(true);

            var outputFolder = @"C:\DropFolder\MyDownload".AsOsAgnostic();
            var sourcePath = @"C:\DropFolder\MyDownload\Droned.1998.mkv".AsOsAgnostic();
            var fileSize = 1234L;
            var movie = _trackedDownload.RemoteMovie.Movie;
            movie.MovieFileId = 10;

            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputFolder);

            var history = Builder<MovieHistory>.CreateListOfSize(1)
                                               .All()
                                               .With(h => h.MovieId = movie.Id)
                                               .With(h => h.EventType = MovieHistoryEventType.DownloadFolderImported)
                                               .BuildList();

            history[0].Data["FileId"] = movie.MovieFileId.ToString();
            history[0].Data["DroppedPath"] = sourcePath;
            history[0].Data["Size"] = fileSize.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, It.IsAny<List<MovieHistory>>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(outputFolder))
                  .Returns(true);

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.GetVideoFiles(outputFolder, It.IsAny<bool>()))
                  .Returns(new[] { sourcePath });

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.FilterPaths(outputFolder, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                  .Returns(new List<string> { sourcePath });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(sourcePath))
                  .Returns(fileSize);

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void should_not_mark_as_imported_from_history_if_remaining_file_size_differs()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingMovieFiles)
                .Returns(true);

            var outputFolder = @"C:\DropFolder\MyDownload".AsOsAgnostic();
            var sourcePath = @"C:\DropFolder\MyDownload\Droned.1998.mkv".AsOsAgnostic();
            var importedSize = 1234L;
            var movie = _trackedDownload.RemoteMovie.Movie;
            movie.MovieFileId = 10;

            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputFolder);

            var history = Builder<MovieHistory>.CreateListOfSize(1)
                                               .All()
                                               .With(h => h.MovieId = movie.Id)
                                               .With(h => h.EventType = MovieHistoryEventType.DownloadFolderImported)
                                               .BuildList();

            history[0].Data["DroppedPath"] = sourcePath;
            history[0].Data["Size"] = importedSize.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, It.IsAny<List<MovieHistory>>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(outputFolder))
                  .Returns(true);

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.GetVideoFiles(outputFolder, It.IsAny<bool>()))
                  .Returns(new[] { sourcePath });

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.FilterPaths(outputFolder, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                  .Returns(new List<string> { sourcePath });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(sourcePath))
                  .Returns(importedSize + 1);

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedMovieInfo>(), true))
                  .Returns(new List<ImportDecision>());

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_bypass_existing_movie_auto_import_block_for_preferred_dual_audio_upgrade()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingMovieFiles)
                .Returns(true);

            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.PreferDualAudio)
                .Returns(true);

            var outputPath = @"C:\DropFolder\MyDownload.mkv".AsOsAgnostic();
            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputPath);

            var movie = _trackedDownload.RemoteMovie.Movie;
            movie.MovieFileId = 1;
            movie.MovieFile = new MovieFile();

            var localMovie = new LocalMovie
            {
                Path = outputPath,
                Movie = movie
            };

            Mocker.GetMock<IDiskProvider>()
                .Setup(v => v.FileExists(outputPath))
                .Returns(true);

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(v => v.GetImportDecisions(It.IsAny<List<string>>(), movie, _trackedDownload.DownloadItem, null, true))
                .Returns(new List<ImportDecision>
                {
                    new ImportDecision(localMovie)
                });

            Mocker.GetMock<IDualAudioImportPreference>()
                .Setup(v => v.Evaluate(localMovie, movie.MovieFile))
                .Returns(new DualAudioImportPreferenceResult
                {
                    Applies = true,
                    IsPreferredUpgrade = true
                });

            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(outputPath, ImportMode.Auto, movie, _trackedDownload.DownloadItem))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(new ImportDecision(localMovie))
                });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        private void AssertNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertImported()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.ProcessPath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }
    }
}
