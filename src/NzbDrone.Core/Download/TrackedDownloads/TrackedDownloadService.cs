using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<MovieGrabbedEvent>,
                                          IHandle<MovieAddedEvent>,
                                          IHandle<MovieEditedEvent>,
                                          IHandle<MoviesBulkEditedEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IConfigService _config;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      IHistoryService historyService,
                                      IDownloadHistoryService downloadHistoryService,
                                      ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                      IConfigService config,
                                      IRemoteMovieAggregationService aggregationService,
                                      ICustomFormatCalculationService formatCalculator,
                                      IEventAggregator eventAggregator,
                                      ICacheManager cacheManager,
                                      Logger logger)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _config = config;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _logger = logger;

            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
        }

        public TrackedDownload Find(string downloadId)
        {
            return _cache.Find(downloadId);
        }

        public void StopTracking(string downloadId)
        {
            var trackedDownload = _cache.Find(downloadId);

            _cache.Remove(downloadId);
            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { trackedDownload }));
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = new List<TrackedDownload>();

            foreach (var downloadId in downloadIds)
            {
                var trackedDownload = _cache.Find(downloadId);

                _cache.Remove(downloadId);
                trackedDownloads.Add(trackedDownload);
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var existingItem = Find(downloadItem.DownloadId);

            if (existingItem != null &&
                existingItem.State != TrackedDownloadState.Downloading &&
                existingItem.State != TrackedDownloadState.Imported)
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                UpdateCachedCompletedDownloadState(existingItem);

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false
            };

            try
            {
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();

                var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(downloadItem.DownloadId);

                var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title, false, _config.ParseTmdbIdFromReleaseName);

                if (parsedMovieInfo != null)
                {
                    if (downloadHistory is { EventType: DownloadHistoryEventType.DownloadImported })
                    {
                        trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo, downloadHistory.MovieId);
                    }

                    trackedDownload.RemoteMovie ??= _parsingService.Map(parsedMovieInfo, "", 0, null);
                }

                if (historyItems.Any())
                {
                    var firstHistoryItem = historyItems.First();
                    var grabbedEvent = historyItems.FirstOrDefault(v => v.EventType == MovieHistoryEventType.Grabbed);

                    trackedDownload.Indexer = grabbedEvent?.Data?.GetValueOrDefault("indexer");
                    trackedDownload.Added = grabbedEvent?.Date;

                    if (parsedMovieInfo == null ||
                        trackedDownload.RemoteMovie?.Movie == null)
                    {
                        parsedMovieInfo = Parser.Parser.ParseMovieTitle(firstHistoryItem.SourceTitle, false, _config.ParseTmdbIdFromReleaseName);

                        if (parsedMovieInfo != null)
                        {
                            trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo,
                                firstHistoryItem.MovieId);
                        }
                    }

                    if (trackedDownload.RemoteMovie != null)
                    {
                        trackedDownload.RemoteMovie.Release ??= new ReleaseInfo();
                        trackedDownload.RemoteMovie.Release.Indexer = trackedDownload.Indexer;
                        trackedDownload.RemoteMovie.Release.Title = trackedDownload.RemoteMovie.ParsedMovieInfo?.ReleaseTitle;

                        if (Enum.TryParse(grabbedEvent?.Data?.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            trackedDownload.RemoteMovie.Release.IndexerFlags = flags;
                        }

                        if (downloadHistory != null)
                        {
                            trackedDownload.RemoteMovie.Release.IndexerId = downloadHistory.IndexerId;
                        }
                    }
                }

                if (trackedDownload.RemoteMovie != null)
                {
                    _aggregationService.Augment(trackedDownload.RemoteMovie);

                    // Calculate custom formats
                    trackedDownload.RemoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(trackedDownload.RemoteMovie, downloadItem.TotalSize);
                }

                if (downloadHistory != null)
                {
                    trackedDownload.State = GetStateFromHistory(downloadHistory.EventType, trackedDownload, historyItems);
                }

                // Track it so it can be displayed in the queue even though we can't determine which movie it is for
                if (trackedDownload.RemoteMovie == null)
                {
                    _logger.Trace("No Movie found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (MultipleMoviesFoundException e)
            {
                _logger.Debug(e, "Found multiple movies for " + downloadItem.Title);

                trackedDownload.Warn("Unable to import automatically, found multiple movies: {0}", string.Join(", ", e.Movies));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find movie for " + downloadItem.Title);

                trackedDownload.Warn("Unable to parse movie from title");
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().ExceptBy(t => t.DownloadItem.DownloadId, trackedDownloads, t => t.DownloadItem.DownloadId, StringComparer.CurrentCulture).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} RadarrStage={4} Movie='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteMovie?.ParsedMovieInfo,
                    downloadItem.OutputPath);
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title, false, _config.ParseTmdbIdFromReleaseName);

            trackedDownload.RemoteMovie = parsedMovieInfo == null ? null : _parsingService.Map(parsedMovieInfo, "", 0, null);

            _aggregationService.Augment(trackedDownload.RemoteMovie);
        }

        private void UpdateCachedCompletedDownloadState(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(trackedDownload.DownloadItem.DownloadId);

            if (downloadHistory == null)
            {
                return;
            }

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            var state = GetStateFromHistory(downloadHistory.EventType, trackedDownload, historyItems);

            if (!IsTerminalState(state))
            {
                return;
            }

            if (trackedDownload.State != state)
            {
                _logger.Debug("Updating cached completed download '{0}' from {1} to {2} based on download history.",
                    trackedDownload.DownloadItem.Title,
                    trackedDownload.State,
                    state);
            }

            trackedDownload.State = state;

            if (state == TrackedDownloadState.Imported)
            {
                trackedDownload.ClearStatus();
            }
        }

        private static bool IsTerminalState(TrackedDownloadState state)
        {
            return state == TrackedDownloadState.Imported ||
                   state == TrackedDownloadState.Failed ||
                   state == TrackedDownloadState.Ignored;
        }

        private TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType, TrackedDownload trackedDownload, List<MovieHistory> historyItems)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImported:
                    return GetImportedStateFromHistory(trackedDownload, historyItems);
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.Failed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        private TrackedDownloadState GetImportedStateFromHistory(TrackedDownload trackedDownload, List<MovieHistory> historyItems)
        {
            if (trackedDownload.Protocol != DownloadProtocol.Torrent)
            {
                return TrackedDownloadState.Imported;
            }

            if (trackedDownload.RemoteMovie?.Movie == null)
            {
                _logger.Debug("Ignoring imported download history for '{0}' because it does not map to a movie.", trackedDownload.DownloadItem.Title);
                return TrackedDownloadState.Downloading;
            }

            if (_trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems))
            {
                return TrackedDownloadState.Imported;
            }

            _logger.Debug("Ignoring imported download history for '{0}' because the current movie file does not match this download.", trackedDownload.DownloadItem.Title);
            return TrackedDownloadState.Downloading;
        }

        public void Handle(MovieGrabbedEvent message)
        {
            if (message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var trackedDownload = _cache.Find(message.DownloadId);

            if (trackedDownload is { State: TrackedDownloadState.Imported or
                                            TrackedDownloadState.Failed or
                                            TrackedDownloadState.Ignored })
            {
                _cache.Remove(message.DownloadId);
            }
        }

        public void Handle(MovieAddedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie == null ||
                    message.Movie?.TmdbId == t.RemoteMovie.Movie.TmdbId)
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MovieEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    (t.RemoteMovie.Movie.Id == message.Movie?.Id || t.RemoteMovie.Movie.TmdbId == message.Movie?.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesBulkEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesDeletedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
