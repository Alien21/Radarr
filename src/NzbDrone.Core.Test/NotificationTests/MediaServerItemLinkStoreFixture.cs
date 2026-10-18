using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class MediaServerItemLinkStoreFixture : CoreTest
    {
        private Mock<IAppFolderInfo> _folders;
        private Mock<IDiskProvider> _disk;
        private string _key;

        [SetUp]
        public void Setup()
        {
            _folders = new Mock<IAppFolderInfo>();
            _folders.SetupGet(f => f.AppDataFolder).Returns(TempFolder);
            _disk = new Mock<IDiskProvider>();
            _disk.Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            _disk.Setup(d => d.ReadAllText(It.IsAny<string>())).Returns((string path) => File.ReadAllText(path));
            _disk.Setup(d => d.EnsureFolder(It.IsAny<string>())).Callback<string>(path => Directory.CreateDirectory(path));
            _disk.Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            _key = MediaServerItemLinkKey.Create("plex", "https://server:32400/plex", "secret-token", "/movies", "/media", "movie", "123");
        }

        private MediaServerItemLinkStore NewStore()
        {
            return new MediaServerItemLinkStore(_folders.Object, _disk.Object, TestLogger);
        }

        private static List<MediaServerItemLink> Links()
        {
            return new List<MediaServerItemLink> { new MediaServerItemLink { SectionId = 1, ItemId = "10" } };
        }

        [Test]
        public void should_persist_ids_and_reload_them_after_restart_without_secrets_or_metadata()
        {
            NewStore().Set(_key, Links());

            var restarted = NewStore();
            restarted.TryGet(_key, out var links).Should().BeTrue();
            links.Should().ContainSingle();
            links[0].SectionId.Should().Be(1);
            links[0].ItemId.Should().Be("10");
            var content = File.ReadAllText(Path.Combine(TempFolder, "MediaServerItemLinks.json"));
            content.Should().NotContain("secret-token").And.NotContain("https://server").And.NotContain("/movies").And.NotContain("DateCreated");
            File.Exists(Path.Combine(TempFolder, "MediaServerItemLinks.json.tmp")).Should().BeFalse();
        }

        [Test]
        public void should_invalidate_keys_when_server_credentials_mapping_or_media_identity_changes()
        {
            var keys = new[]
            {
                _key,
                MediaServerItemLinkKey.Create("plex", "https://other:32400/plex", "secret-token", "/movies", "/media", "movie", "123"),
                MediaServerItemLinkKey.Create("plex", "https://server:32400/plex", "other-token", "/movies", "/media", "movie", "123"),
                MediaServerItemLinkKey.Create("plex", "https://server:32400/plex", "secret-token", "/other", "/media", "movie", "123"),
                MediaServerItemLinkKey.Create("plex", "https://server:32400/plex", "secret-token", "/movies", "/other", "movie", "123"),
                MediaServerItemLinkKey.Create("plex", "https://server:32400/plex", "secret-token", "/movies", "/media", "movie", "124"),
                MediaServerItemLinkKey.Create("jellyfin", "https://server:32400/plex", "secret-token", "/movies", "/media", "movie", "123")
            };

            keys.Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void should_return_defensive_copies_and_not_rewrite_unchanged_links()
        {
            var store = NewStore();
            var original = Links();
            store.Set(_key, original);
            original[0].ItemId = "changed";
            store.TryGet(_key, out var first).Should().BeTrue();
            first[0].ItemId = "changed-again";
            store.TryGet(_key, out var second).Should().BeTrue();
            second[0].ItemId.Should().Be("10");
            store.Set(_key, Links());
            _disk.Verify(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_persist_removal_of_an_invalid_binding()
        {
            var store = NewStore();
            store.Set(_key, Links());

            store.Remove(_key);

            NewStore().TryGet(_key, out _).Should().BeFalse();
        }

        [Test]
        public void should_keep_negative_results_only_in_memory()
        {
            var store = NewStore();
            store.SetNegative(_key);
            store.IsNegative(_key).Should().BeTrue();
            NewStore().IsNegative(_key).Should().BeFalse();
            store.Set(_key, Links());
            store.IsNegative(_key).Should().BeFalse();
        }

        [TestCase("{")]
        [TestCase("{\"Version\":2,\"Links\":{}}")]
        public void should_ignore_corrupt_or_unsupported_cache_files(string content)
        {
            File.WriteAllText(Path.Combine(TempFolder, "MediaServerItemLinks.json"), content);

            NewStore().TryGet(_key, out _).Should().BeFalse();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_keep_in_memory_links_when_persistence_fails()
        {
            _disk.Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("Storage unavailable"));
            var store = NewStore();

            store.Set(_key, Links());

            store.TryGet(_key, out var links).Should().BeTrue();
            links[0].ItemId.Should().Be("10");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_treat_read_failures_as_cache_misses()
        {
            _disk.Setup(d => d.FileExists(It.IsAny<string>())).Returns(true);
            _disk.Setup(d => d.ReadAllText(It.IsAny<string>())).Throws(new IOException("Storage unavailable"));

            NewStore().TryGet(_key, out _).Should().BeFalse();

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
