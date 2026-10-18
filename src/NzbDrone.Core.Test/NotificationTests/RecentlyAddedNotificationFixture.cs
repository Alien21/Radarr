using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class RecentlyAddedNotificationFixture : CoreTest<NotificationService>
    {
        private Mock<INotification> _notification;
        private Mock<IRecentlyAddedNotification> _recentlyAdded;
        private NotificationDefinition _definition;
        private Movie _movie;

        [SetUp]
        public void Setup()
        {
            _notification = new Mock<INotification>();
            _recentlyAdded = _notification.As<IRecentlyAddedNotification>();
            _definition = new NotificationDefinition { Name = "Media Server", OnDownload = true };
            _notification.SetupGet(n => n.Definition).Returns(_definition);
            _movie = new Movie { Title = "Test Movie", ImdbId = "tt0123456" };

            Mocker.GetMock<INotificationFactory>()
                .Setup(f => f.OnDownloadEnabled(true))
                .Returns(new List<INotification> { _notification.Object });
            Mocker.GetMock<IConfigService>()
                .SetupGet(c => c.MarkImportedMediaAsRecentlyAdded)
                .Returns(true);
        }

        private MovieFileImportedEvent GivenImport(bool replacement = false, bool newDownload = true)
        {
            var oldFiles = new List<DeletedMovieFile>();

            if (replacement)
            {
                oldFiles.Add(new DeletedMovieFile(new MovieFile(), null));
            }

            return new MovieFileImportedEvent(
                new LocalMovie { Movie = _movie, Quality = new QualityModel() },
                new MovieFile(),
                oldFiles,
                newDownload,
                null);
        }

        [Test]
        public void should_mark_replacements_even_when_upgrade_notifications_are_disabled()
        {
            Subject.Handle(GivenImport(true));

            _recentlyAdded.Verify(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()), Times.Once());
            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Never());
        }

        [Test]
        public void should_mark_before_the_existing_import_notification()
        {
            var calls = new List<string>();
            _recentlyAdded.Setup(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>())).Callback(() => calls.Add("date"));
            _notification.Setup(n => n.OnDownload(It.IsAny<DownloadMessage>())).Callback(() => calls.Add("notification"));

            Subject.Handle(GivenImport());

            calls.Should().Equal("date", "notification");
        }

        [Test]
        public void should_keep_upgrade_notifications_when_enabled()
        {
            _definition.OnUpgrade = true;

            Subject.Handle(GivenImport(true));

            _recentlyAdded.Verify(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()), Times.Once());
            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Once());
        }

        [Test]
        public void should_preserve_the_existing_notification_when_date_update_fails()
        {
            _recentlyAdded.Setup(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()))
                .Throws(new InvalidOperationException("Metadata update failed"));

            Subject.Handle(GivenImport());

            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Once());
            Mocker.GetMock<INotificationStatusService>().Verify(s => s.RecordFailure(It.IsAny<int>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_leave_dates_unchanged_when_the_setting_is_disabled()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.MarkImportedMediaAsRecentlyAdded).Returns(false);

            Subject.Handle(GivenImport());

            _recentlyAdded.Verify(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()), Times.Never());
            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Once());
        }

        [Test]
        public void should_not_mark_existing_disk_scan_files()
        {
            Subject.Handle(GivenImport(newDownload: false));

            _recentlyAdded.Verify(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()), Times.Never());
            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Never());
        }

        [Test]
        public void should_respect_connection_tags()
        {
            _definition.Tags.Add(42);

            Subject.Handle(GivenImport());

            _recentlyAdded.Verify(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()), Times.Never());
            _notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Never());
        }

        [Test]
        public void should_keep_notifications_without_recently_added_support()
        {
            var notification = new Mock<INotification>();
            notification.SetupGet(n => n.Definition).Returns(new NotificationDefinition());
            Mocker.GetMock<INotificationFactory>().Setup(f => f.OnDownloadEnabled(true))
                .Returns(new List<INotification> { notification.Object });

            Subject.Handle(GivenImport());

            notification.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Once());
        }
    }
}
