using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ManualImportRecoveryFixture : CoreTest<ManualImportService>
    {
        private Movie _movie;
        private TrackedDownload _trackedDownload;
        private ManualImportCommand _command;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Title = "Droned", Path = @"C:\Movies\Droned (1998)".AsOsAgnostic() };
            var downloadItem = new DownloadClientItem
            {
                DownloadId = "download-id",
                Title = "Droned.1998",
                Category = "movies",
                Status = DownloadItemStatus.Completed,
                OutputPath = new OsPath(@"C:\Downloads\Droned.1998.mkv".AsOsAgnostic())
            };
            _trackedDownload = new TrackedDownload
            {
                DownloadItem = downloadItem,
                ImportItem = downloadItem,
                IsTrackable = true,
                State = TrackedDownloadState.ImportBlocked,
                RemoteMovie = new RemoteMovie { Movie = _movie }
            };
            _command = new ManualImportCommand
            {
                Files = new List<ManualImportFile>
                {
                    new ManualImportFile
                    {
                        Path = downloadItem.OutputPath.FullPath,
                        DownloadId = downloadItem.DownloadId,
                        MovieId = _movie.Id,
                        Languages = new List<Language> { Language.English }
                    }
                }
            };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(_movie.Id)).Returns(_movie);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(downloadItem.DownloadId)).Returns(_trackedDownload);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { _trackedDownload });
            Mocker.GetMock<IAggregationService>()
                .Setup(s => s.Augment(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()))
                .Returns<LocalMovie, DownloadClientItem>((movie, item) => movie);
        }

        private void GivenImportResult(params string[] errors)
        {
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, _trackedDownload.DownloadItem, It.IsAny<ImportMode>()))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, newDownload, item, mode) =>
                    new List<ImportResult> { new ImportResult(decisions.Single(), errors) });
        }

        [Test]
        public void should_show_failed_import_error_and_leave_download_available_for_recheck()
        {
            GivenImportResult("Failed to import movie, unable to move existing file to the Recycle Bin.");
            Subject.Execute(_command);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
            _trackedDownload.StatusMessages.SelectMany(s => s.Messages)
                .Should().Contain("Failed to import movie, unable to move existing file to the Recycle Bin.");
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void should_release_importing_state_if_import_throws()
        {
            var exception = new IOException("No space left on device");
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, _trackedDownload.DownloadItem, It.IsAny<ImportMode>()))
                .Throws(exception);
            Assert.Throws<IOException>(() => Subject.Execute(_command)).Should().BeSameAs(exception);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.StatusMessages.SelectMany(s => s.Messages).Should().Contain(s => s.Contains(exception.Message));
        }

        [Test]
        public void should_release_importing_state_if_cleanup_throws()
        {
            GivenImportResult();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_trackedDownload.ImportItem.OutputPath.FullPath))
                .Throws(new IOException("Archive unavailable"));
            Assert.Throws<IOException>(() => Subject.Execute(_command));
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.StatusMessages.SelectMany(s => s.Messages).Should().Contain(s => s.Contains("Archive unavailable"));
        }

        [Test]
        public void should_mark_successful_manual_import_as_imported()
        {
            GivenImportResult();
            Subject.Execute(_command);
            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void should_import_automatically_after_failed_manual_import_is_rechecked()
        {
            GivenImportResult("No space left on device");
            Subject.Execute(_command);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IProvideImportItemService>()
                .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                .Returns<DownloadClientItem, DownloadClientItem>((item, previous) => item);
            Mocker.GetMock<IParsingService>().Setup(s => s.GetMovie(_trackedDownload.DownloadItem.Title)).Returns(_movie);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId)).Returns(new List<MovieHistory>());
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(s => s.ProcessPath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _movie, _trackedDownload.DownloadItem))
                .Returns(new List<ImportResult> { new ImportResult(new ImportDecision(new LocalMovie { Movie = _movie })) });
            var completedDownloadService = Mocker.Resolve<CompletedDownloadService>();
            completedDownloadService.Check(_trackedDownload);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            completedDownloadService.Import(_trackedDownload);
            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }
    }
}
