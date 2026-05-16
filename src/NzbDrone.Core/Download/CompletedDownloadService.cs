using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private static readonly HashSet<ImportRejectionReason> GenericExistingMovieImportRejectionReasons = new HashSet<ImportRejectionReason>
        {
            ImportRejectionReason.UnknownMovie,
            ImportRejectionReason.MovieAlreadyImported,
            ImportRejectionReason.NotQualityUpgrade,
            ImportRejectionReason.NotRevisionUpgrade,
            ImportRejectionReason.NotCustomFormatUpgrade
        };

        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IDownloadedMovieImportService _downloadedMovieImportService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IAggregationService _aggregationService;
        private readonly IDualAudioImportPreference _dualAudioImportPreference;
        private readonly IParsingService _parsingService;
        private readonly IMovieService _movieService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IRejectedImportService _rejectedImportService;
        private readonly Logger _logger;
        private readonly ISearchForNewMovie _searchProxy;
        private readonly IAddMovieService _addMovieService;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly ITagService _tagService;
        private readonly IConfigService _configService;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedMovieImportService downloadedMovieImportService,
                                        IDiskProvider diskProvider,
                                        IDiskScanService diskScanService,
                                        IMakeImportDecision importDecisionMaker,
                                        IAggregationService aggregationService,
                                        IDualAudioImportPreference dualAudioImportPreference,
                                        IParsingService parsingService,
                                        IMovieService movieService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IRejectedImportService rejectedImportService,
                                        ISearchForNewMovie searchProxy,
                                        IAddMovieService addMovieService,
                                        IQualityProfileRepository qualityProfileRepository,
                                        ITagService tagService,
                                        IConfigService configService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedMovieImportService = downloadedMovieImportService;
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _aggregationService = aggregationService;
            _dualAudioImportPreference = dualAudioImportPreference;
            _parsingService = parsingService;
            _movieService = movieService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _rejectedImportService = rejectedImportService;
            _searchProxy = searchProxy;
            _addMovieService = addMovieService;
            _qualityProfileRepository = qualityProfileRepository;
            _tagService = tagService;
            _configService = configService;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed).ToList();
            var historyItem = grabbedHistories.MaxBy(h => h.Date);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Radarr and not in a category, Skipping.");
                _logger.Warn("Download wasn't grabbed by Radarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            AnalyzeCompletedDownloadFile(trackedDownload);

            var movie = _parsingService.GetMovie(trackedDownload.DownloadItem.Title);

            if (movie != null)
            {
                if (BlockAutoImportForExistingMovieFile(trackedDownload, movie))
                {
                    return;
                }

                AttachExistingMovie(trackedDownload, movie);
            }

            if (movie == null && historyItem != null)
            {
                movie = _movieService.GetMovie(historyItem.MovieId);
                if (movie != null)
                {
                    if (BlockAutoImportForExistingMovieFile(trackedDownload, movie))
                    {
                        return;
                    }

                    AttachExistingMovie(trackedDownload, movie);
                }
            }

            if (trackedDownload.RemoteMovie == null)
            {
                var parsed = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title, false, _configService.ParseTmdbIdFromReleaseName);
                if (parsed != null)
                {
                    trackedDownload.RemoteMovie = _parsingService.Map(parsed, "", 0);
                }
            }

            if (trackedDownload.RemoteMovie == null)
            {
                trackedDownload.Warn($"Auto-import blocked: unable to resolve {trackedDownload.ImportItem?.Title} download to a movie.");
                _logger.Error($"Auto-import blocked: unable to resolve {trackedDownload.ImportItem?.Title} download to a movie.");
                SetStateToImportBlocked(trackedDownload);
                return;
            }

            if (movie == null)
            {
                if (!AllowAutomaticImport(trackedDownload))
                {
                    AnalyzeCompletedDownloadFile(trackedDownload);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_configService.DefaultRootFolderForAutoImport))
                {
                    trackedDownload.Warn("Auto-import blocked: no default root folder configured for auto-import.");
                    _logger.Warn("Auto-import blocked: no default root folder configured for auto-import.");
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

                QualityProfile profile;
                profile = _configService.DefaultProfileForAutoImport == -1 ? _qualityProfileRepository.All().FirstOrDefault() : _qualityProfileRepository.Get(_configService.DefaultProfileForAutoImport);

                if (profile == null)
                {
                    trackedDownload.Warn("Auto-import blocked: default quality profile not found (id: {0}).", _configService.DefaultProfileForAutoImport);
                    _logger.Warn("Auto-import blocked: default quality profile not found (id: {0}).", _configService.DefaultProfileForAutoImport);
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

                var movies = _searchProxy.SearchForNewMovie(Path.GetFileName(trackedDownload.DownloadItem.Title));
                if (movies == null || movies.Count <= 0)
                {
                    trackedDownload.Warn("Auto-import blocked: no movie match found for '{0}'.", trackedDownload.DownloadItem.Title);
                    _logger.Warn("Auto-import blocked: no movie match found for '{0}'.", trackedDownload.DownloadItem.Title);
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

                var parsedYear = trackedDownload.RemoteMovie?.ParsedMovieInfo?.Year;

                if (parsedYear > 1890)
                {
                    var moviesInYear = movies.Where(m => m.Year == parsedYear).ToList();

                    if (moviesInYear.Count == 1)
                    {
                        movie = moviesInYear.First();
                    }
                    else
                    {
                        _logger.Debug("Auto-import found {0} candidate movies for '{1}' in year {2}; trying exact original/localized title match.",
                            moviesInYear.Count,
                            trackedDownload.DownloadItem.Title,
                            parsedYear);

                        movie = _searchProxy.SearchForNewMovieByExactTitle(Path.GetFileName(trackedDownload.DownloadItem.Title), parsedYear.Value, moviesInYear);

                        if (movie != null)
                        {
                            _logger.Debug("Auto-import exact title match for '{0}' resolved to '{1}' tmdbid: {2}", trackedDownload.DownloadItem.Title, movie.Title, movie.TmdbId);
                        }
                        else
                        {
                            _logger.Debug("Auto-import exact original/localized title match did not resolve '{0}' for year {1}.", trackedDownload.DownloadItem.Title, parsedYear);
                        }
                    }
                }

                if (movie == null)
                {
                    trackedDownload.Warn("Auto-import blocked: no unique match for '{0}' (parsed year: {1}).", trackedDownload.DownloadItem.Title, parsedYear);
                    _logger.Warn("Auto-import blocked: no unique match for '{0}' (parsed year: {1}).", trackedDownload.DownloadItem.Title, parsedYear);
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

                var existingMovie = _movieService.FindByTmdbId(movie.TmdbId);
                if (existingMovie != null)
                {
                    if (BlockAutoImportForExistingMovieFile(trackedDownload, existingMovie))
                    {
                        return;
                    }

                    _logger.Debug($"Joining movie '{movie.Title}' tmdbid: {movie.TmdbId} to existing movie '{existingMovie.Path}'");

                    AttachExistingMovie(trackedDownload, existingMovie);
                    AnalyzeCompletedDownloadFile(trackedDownload);
                    return;
                }

                _logger.Debug($"Autocreate movie '{movie.Title}' tmdbid: {movie.TmdbId}");

                movie.Monitored = true;

                foreach (var tag in GetOrCreateAutoImportTags())
                {
                    movie.Tags.Add(tag.Id);
                }

                movie.QualityProfile = profile;
                movie.QualityProfileId = profile.Id;
                movie.MinimumAvailability = MovieStatusType.Announced;
                movie.RootFolderPath = _configService.DefaultRootFolderForAutoImport;
                movie.AddOptions = new AddMovieOptions
                {
                    AddMethod = AddMovieMethod.Manual,
                    Monitor = MonitorTypes.None,
                    SearchForMovie = false
                };

                var newMovie = _addMovieService.AddMovie(movie);

                trackedDownload.RemoteMovie.Movie = newMovie;

                if (newMovie != null)
                {
                    trackedDownload.RemoteMovie.Movie.QualityProfile = profile;
                    trackedDownload.RemoteMovie.Movie.QualityProfileId = profile.Id;

                    trackedDownload.ClearStatus();
                }
                else
                {
                    trackedDownload.Warn($"Auto-import blocked: failed to add movie '{movie.Title}' (tmdbid: {movie.TmdbId}).");
                    _logger.Error($"Auto-import blocked: failed to add movie '{movie.Title}' tmdbid: {movie.TmdbId}.");
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

                // trackedDownload.State = TrackedDownloadState.ImportPending;
                // return;
            }

            _logger.Debug($"Set State='{TrackedDownloadState.ImportPending}' for movie '{movie.Title}' tmdbid: {movie.TmdbId}");

            AnalyzeCompletedDownloadFile(trackedDownload);

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        private bool AllowAutomaticImport(TrackedDownload trackedDownload)
        {
            if (_configService.AllowAutomaticImport)
            {
                return true;
            }

            trackedDownload.Warn("Auto-import blocked: automatic import is disabled.");
            _logger.Debug("Auto-import blocked: automatic import is disabled.");
            SetStateToImportBlocked(trackedDownload);
            return false;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            if (trackedDownload.RemoteMovie?.Movie == null)
            {
                trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            if (BlockAutoImportForExistingMovieFile(trackedDownload, trackedDownload.RemoteMovie.Movie))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var importResults = _downloadedMovieImportService.ProcessPath(outputPath,
                ImportMode.Auto,
                trackedDownload.RemoteMovie.Movie,
                trackedDownload.ImportItem);

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                return;
            }

            if (importResults.Count == 1)
            {
                var firstResult = importResults.First();

                if (_rejectedImportService.Process(trackedDownload, firstResult))
                {
                    return;
                }
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
                                 {
                                    new TrackedDownloadStatusMessage("One or more movies expected in this release were not imported or missing", new List<string>())
                                 };

            if (importResults.Any(c => c.Result != ImportResultType.Imported))
            {
                statusMessages.AddRange(
                    importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.LocalMovie != null)
                        .OrderBy(v => v.ImportDecision.LocalMovie.Path)
                        .Select(v =>
                            new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.LocalMovie.Path),
                                v.Errors)));
            }

            if (statusMessages.Any())
            {
                trackedDownload.Warn(statusMessages.ToArray());
                SetStateToImportBlocked(trackedDownload);
            }
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var allMoviesImported = importResults.Where(c => c.Result == ImportResultType.Imported)
                                       .Select(c => c.ImportDecision.LocalMovie.Movie)
                                       .Any();

            if (allMoviesImported)
            {
                _logger.Debug("All movies were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));
                return true;
            }

            // Double check if all movies were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.
            var atLeastOneMovieImported = importResults.Any(c => c.Result == ImportResultType.Imported);

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                                                  .OrderByDescending(h => h.Date)
                                                  .ToList();

            var allMoviesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allMoviesImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.
                if (atLeastOneMovieImported)
                {
                    _logger.Debug("All movies were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No Movies were just imported, but all movies were previously imported, possible issue with download history.")
                           .Property("MovieId", trackedDownload.RemoteMovie.Movie.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.ImportItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));

                return true;
            }

            _logger.Debug("Not all movies have been imported for {0}", trackedDownload.DownloadItem.Title);
            return false;
        }

        private void SetStateToImportBlocked(TrackedDownload trackedDownload)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;

            if (!trackedDownload.HasNotifiedManualInteractionRequired)
            {
                var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed).ToList();

                trackedDownload.HasNotifiedManualInteractionRequired = true;

                var releaseInfo = grabbedHistories.Count > 0 ? new GrabbedReleaseInfo(grabbedHistories) : null;
                var manualInteractionEvent = new ManualInteractionRequiredEvent(trackedDownload, releaseInfo);

                _eventAggregator.PublishEvent(manualInteractionEvent);
            }
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private List<Tag> GetOrCreateAutoImportTags()
        {
            var tags = _tagService.All();

            return new[] { "autocreated", "default" }
                .Select(label =>
                {
                    var tag = tags.FirstOrDefault(t => t.Label.EqualsIgnoreCase(label));

                    if (tag != null)
                    {
                        return tag;
                    }

                    tag = _tagService.Add(new Tag { Label = label });
                    tags.Add(tag);

                    return tag;
                })
                .ToList();
        }

        private void AttachExistingMovie(TrackedDownload trackedDownload, Movie movie)
        {
            EnsureRemoteMovie(trackedDownload, movie);

            trackedDownload.ClearStatus();

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        private bool BlockAutoImportForExistingMovieFile(TrackedDownload trackedDownload, Movie movie)
        {
            if (!_configService.BlockAutoImportForExistingMovieFiles || movie == null || !movie.HasFile)
            {
                return false;
            }

            EnsureRemoteMovie(trackedDownload, movie);

            if (MarkAsImportedIfAlreadyImportedInHistory(trackedDownload))
            {
                return true;
            }

            AnalyzeCompletedDownloadFile(trackedDownload);

            var importDecisions = GetExistingMovieAutoImportDecisions(trackedDownload, movie);

            if (ShouldBypassExistingMovieAutoImportBlock(importDecisions, movie))
            {
                return false;
            }

            var importDecisionBlockReason = GetExistingMovieAutoImportDecisionBlockReason(importDecisions, movie);
            if (importDecisionBlockReason.IsNotNullOrWhiteSpace())
            {
                var importDecisionBlockMessage = FormatAutoImportBlockReason(importDecisionBlockReason);

                trackedDownload.Warn("Auto-import blocked: {0}", importDecisionBlockMessage);
                _logger.Warn("Auto-import blocked: '{0}' tmdbid: {1} import rejected while replacing existing movie file: {2}", movie.Title, movie.TmdbId, importDecisionBlockReason);
                SetStateToImportBlocked(trackedDownload);
                return true;
            }

            trackedDownload.Warn("Auto-import blocked: '{0}' already has a movie file in library (tmdbid: {1}).", movie.Title, movie.TmdbId);
            _logger.Warn("Auto-import blocked: '{0}' tmdbid: {1} already has a movie file in library.", movie.Title, movie.TmdbId);
            SetStateToImportBlocked(trackedDownload);
            return true;
        }

        private bool MarkAsImportedIfAlreadyImportedInHistory(TrackedDownload trackedDownload)
        {
            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            if (!_trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems) ||
                !RemainingDownloadFilesMatchImportHistory(trackedDownload, historyItems))
            {
                return false;
            }

            _logger.Debug("All movies were imported in history for {0}", trackedDownload.DownloadItem.Title);

            trackedDownload.State = TrackedDownloadState.Imported;
            _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));

            return true;
        }

        private bool RemainingDownloadFilesMatchImportHistory(TrackedDownload trackedDownload, List<MovieHistory> historyItems)
        {
            var outputPath = trackedDownload.ImportItem?.OutputPath.FullPath;

            if (outputPath.IsNullOrWhiteSpace() ||
                !TryGetCompletedDownloadVideoFiles(outputPath, out var videoFiles) ||
                videoFiles.Empty())
            {
                return true;
            }

            foreach (var videoFile in videoFiles)
            {
                var importHistory = historyItems.FirstOrDefault(history =>
                    history.EventType == MovieHistoryEventType.DownloadFolderImported &&
                    history.Data?.TryGetValue("DroppedPath", out var droppedPath) == true &&
                    droppedPath.Equals(videoFile, StringComparison.OrdinalIgnoreCase));

                if (importHistory == null)
                {
                    _logger.Trace("Remaining download file '{0}' does not match any imported file history.", videoFile);
                    return false;
                }

                if (!importHistory.Data.TryGetValue("Size", out var importedSizeText) ||
                    !long.TryParse(importedSizeText, out var importedSize) ||
                    importedSize <= 0)
                {
                    _logger.Trace("Remaining download file '{0}' does not have an imported file size recorded.", videoFile);
                    return false;
                }

                var currentSize = _diskProvider.GetFileSize(videoFile);

                if (currentSize != importedSize)
                {
                    _logger.Trace("Remaining download file '{0}' size {1} does not match imported size {2}.", videoFile, currentSize, importedSize);
                    return false;
                }
            }

            return true;
        }

        private bool TryGetCompletedDownloadVideoFiles(string outputPath, out List<string> videoFiles)
        {
            if (_diskProvider.FolderExists(outputPath))
            {
                var directoryInfo = new DirectoryInfo(outputPath);
                videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName))
                                            .OrderBy(path => path)
                                            .ToList();

                return true;
            }

            if (_diskProvider.FileExists(outputPath) &&
                MediaFileExtensions.Extensions.Contains(Path.GetExtension(outputPath)))
            {
                videoFiles = new List<string> { outputPath };
                return true;
            }

            videoFiles = new List<string>();
            return false;
        }

        private List<ImportDecision> GetExistingMovieAutoImportDecisions(TrackedDownload trackedDownload, Movie movie)
        {
            if (trackedDownload.ImportItem == null ||
                trackedDownload.ImportItem.OutputPath.FullPath.IsNullOrWhiteSpace())
            {
                return new List<ImportDecision>();
            }

            return GetCompletedDownloadImportDecisions(trackedDownload, movie, trackedDownload.ImportItem.OutputPath.FullPath);
        }

        private bool ShouldBypassExistingMovieAutoImportBlock(List<ImportDecision> decisions, Movie movie)
        {
            if (decisions.Empty())
            {
                return false;
            }

            var qualityUpgradeDecision = GetApprovedQualityUpgradeDecision(decisions, movie);

            if (qualityUpgradeDecision != null)
            {
                _logger.Info("Auto-import block bypassed: '{0}' tmdbid: {1} has an approved quality upgrade '{2}' ({3} > {4}).",
                    movie.Title,
                    movie.TmdbId,
                    qualityUpgradeDecision.LocalMovie.Path,
                    qualityUpgradeDecision.LocalMovie.Quality,
                    movie.MovieFile.Quality);

                return true;
            }

            if (!_configService.PreferDualAudio)
            {
                return false;
            }

            var preferredDualAudioDecision = decisions.FirstOrDefault(decision =>
                decision.Approved &&
                IsPreferredDualAudioUpgradeDecision(decision, movie));

            if (preferredDualAudioDecision == null)
            {
                return false;
            }

            _logger.Info("Auto-import block bypassed: '{0}' tmdbid: {1} has an approved preferred dual-audio upgrade '{2}'.", movie.Title, movie.TmdbId, preferredDualAudioDecision.LocalMovie.Path);
            return true;
        }

        private ImportDecision GetApprovedQualityUpgradeDecision(List<ImportDecision> decisions, Movie movie)
        {
            return decisions.FirstOrDefault(decision =>
                decision.Approved &&
                IsQualityUpgradeDecision(decision, movie));
        }

        private string GetExistingMovieAutoImportDecisionBlockReason(List<ImportDecision> decisions, Movie movie)
        {
            var rejectionMessages = decisions
                .Where(decision => IsExistingMovieAutoImportBypassCandidate(decision, movie))
                .SelectMany(decision => decision.Rejections)
                .Where(rejection => !GenericExistingMovieImportRejectionReasons.Contains(rejection.Reason))
                .Select(rejection => rejection.Message)
                .Where(message => message.IsNotNullOrWhiteSpace())
                .Distinct()
                .ToList();

            return rejectionMessages.Any() ? string.Join("; ", rejectionMessages) : null;
        }

        private bool IsExistingMovieAutoImportBypassCandidate(ImportDecision decision, Movie movie)
        {
            return IsQualityUpgradeDecision(decision, movie) ||
                   IsPreferredDualAudioUpgradeDecision(decision, movie);
        }

        private bool IsQualityUpgradeDecision(ImportDecision decision, Movie movie)
        {
            if (movie.MovieFile?.Quality == null ||
                movie.QualityProfile == null)
            {
                return false;
            }

            var qualityComparer = new QualityModelComparer(movie.QualityProfile);

            return decision.LocalMovie?.Quality != null &&
                   !ReplacesPreferredDualAudioWithNonDual(decision.LocalMovie, movie.MovieFile) &&
                   qualityComparer.Compare(decision.LocalMovie.Quality, movie.MovieFile.Quality) > 0;
        }

        private bool IsPreferredDualAudioUpgradeDecision(ImportDecision decision, Movie movie)
        {
            if (!_configService.PreferDualAudio ||
                decision.LocalMovie == null ||
                movie.MovieFile == null)
            {
                return false;
            }

            var dualAudioPreference = _dualAudioImportPreference.Evaluate(decision.LocalMovie, movie.MovieFile);

            return dualAudioPreference?.IsPreferredUpgrade == true;
        }

        private static string FormatAutoImportBlockReason(string reason)
        {
            return reason.EndsWith(".") ? reason : $"{reason}.";
        }

        private bool ReplacesPreferredDualAudioWithNonDual(LocalMovie localMovie, MovieFile existingMovieFile)
        {
            if (!_configService.PreferDualAudio)
            {
                return false;
            }

            var preferredLanguage = (Language)_configService.MovieInfoLanguage;

            if (!IsKnownLanguage(preferredLanguage) ||
                !HasPreferredDualAudio(existingMovieFile.MediaInfo, existingMovieFile.Languages, preferredLanguage))
            {
                return false;
            }

            return !HasPreferredDualAudio(localMovie.MediaInfo, localMovie.Languages, preferredLanguage);
        }

        private static bool HasPreferredDualAudio(MediaInfoModel mediaInfo, List<Language> parsedLanguages, Language preferredLanguage)
        {
            var audioLanguages = GetAudioLanguages(mediaInfo, parsedLanguages);

            return audioLanguages.KnownLanguages.Contains(preferredLanguage) &&
                   audioLanguages.DistinctAudioLanguages.Count > 1;
        }

        private static AudioLanguageSet GetAudioLanguages(MediaInfoModel mediaInfo, List<Language> parsedLanguages)
        {
            var languages = new AudioLanguageSet();

            foreach (var audioLanguage in mediaInfo?.AudioLanguages ?? new List<string>())
            {
                AddRawLanguage(languages, audioLanguage);
            }

            foreach (var language in parsedLanguages ?? new List<Language>())
            {
                AddKnownLanguage(languages, language);
            }

            return languages;
        }

        private static void AddRawLanguage(AudioLanguageSet languages, string rawLanguage)
        {
            if (rawLanguage.IsNullOrWhiteSpace())
            {
                return;
            }

            var language = ParseLanguage(rawLanguage);

            if (IsKnownLanguage(language))
            {
                AddKnownLanguage(languages, language);
                return;
            }

            languages.DistinctAudioLanguages.Add(rawLanguage.Trim().ToLowerInvariant());
        }

        private static void AddKnownLanguage(AudioLanguageSet languages, Language language)
        {
            if (!IsKnownLanguage(language))
            {
                return;
            }

            languages.KnownLanguages.Add(language);
            languages.DistinctAudioLanguages.Add($"language:{language.Id}");
        }

        private static Language ParseLanguage(string rawLanguage)
        {
            var trimmedLanguage = rawLanguage.Trim();

            return IsoLanguages.Find(trimmedLanguage)?.Language ??
                   IsoLanguages.FindByName(trimmedLanguage)?.Language;
        }

        private static bool IsKnownLanguage(Language language)
        {
            return language is { Id: > 0 };
        }

        private void AnalyzeCompletedDownloadFile(TrackedDownload trackedDownload)
        {
            if (!_configService.AnalyzeCompletedDownloadFiles ||
                trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed ||
                trackedDownload.ImportItem == null)
            {
                return;
            }

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;

            if (outputPath.IsNullOrWhiteSpace() ||
                trackedDownload.AnalyzedMediaInfoPath?.Equals(outputPath) == true)
            {
                return;
            }

            trackedDownload.AnalyzedMediaInfoPath = outputPath;

            try
            {
                var movie = trackedDownload.RemoteMovie?.Movie;
                var localMovie = movie == null
                    ? GetCompletedDownloadQueueMovie(trackedDownload, outputPath)
                    : SelectQueueMediaInfoMovie(GetCompletedDownloadImportDecisions(trackedDownload, movie, outputPath), movie);

                if (localMovie == null)
                {
                    _logger.Debug("Completed download file analysis did not find a media file for queue item '{0}'", trackedDownload.DownloadItem.Title);
                    return;
                }

                var externalSubtitles = GetExternalSubtitleFiles(localMovie);
                ApplyCompletedDownloadFileAnalysis(trackedDownload, localMovie, externalSubtitles);

                var mediaInfo = localMovie.MediaInfo;

                _logger.Debug("Completed download file analysis updated queue item '{0}' from file '{1}'. Quality: '{2}', queue languages: '{3}', queue subtitles: '{4}', media title: '{5}', embedded audio: '{6}', embedded subtitles: '{7}', external subtitles: '{8}'",
                    trackedDownload.DownloadItem.Title,
                    localMovie.Path,
                    localMovie.Quality,
                    FormatValues(localMovie.Languages),
                    FormatValues(trackedDownload.AnalyzedSubtitleLanguages),
                    mediaInfo?.Title ?? "none",
                    FormatValues(mediaInfo?.AudioLanguages),
                    FormatValues(mediaInfo?.Subtitles),
                    FormatExternalSubtitleFiles(externalSubtitles));
            }
            catch (System.Exception ex)
            {
                _logger.Warn(ex, "Unable to analyze completed download file for queue item '{0}'", trackedDownload.DownloadItem.Title);
            }
        }

        private void ApplyCompletedDownloadFileAnalysis(TrackedDownload trackedDownload, LocalMovie localMovie, List<ExternalSubtitleFile> externalSubtitles)
        {
            if (localMovie.Quality?.Quality != Quality.Unknown)
            {
                trackedDownload.AnalyzedQuality = localMovie.Quality;
            }

            if (localMovie.Languages?.Any(l => l != Language.Unknown) == true)
            {
                trackedDownload.AnalyzedLanguages = localMovie.Languages;
            }

            var subtitleLanguages = GetSubtitleLanguages(localMovie, externalSubtitles);
            if (subtitleLanguages.Any())
            {
                trackedDownload.AnalyzedSubtitleLanguages = subtitleLanguages;
            }

            if (trackedDownload.RemoteMovie != null)
            {
                if (trackedDownload.RemoteMovie.ParsedMovieInfo == null)
                {
                    trackedDownload.RemoteMovie.ParsedMovieInfo = new ParsedMovieInfo
                    {
                        ReleaseTitle = trackedDownload.DownloadItem.Title
                    };
                }

                if (localMovie.Quality?.Quality != Quality.Unknown)
                {
                    trackedDownload.RemoteMovie.ParsedMovieInfo.Quality = localMovie.Quality;
                }

                if (localMovie.Languages?.Any(l => l != Language.Unknown) == true)
                {
                    trackedDownload.RemoteMovie.Languages = localMovie.Languages;
                    trackedDownload.RemoteMovie.ParsedMovieInfo.Languages = localMovie.Languages;
                }
            }
        }

        private LocalMovie GetCompletedDownloadQueueMovie(TrackedDownload trackedDownload, string outputPath)
        {
            if (_diskProvider.FolderExists(outputPath))
            {
                var directoryInfo = new DirectoryInfo(outputPath);
                var folderInfo = Parser.Parser.ParseMovieTitle(GetCleanedUpFolderName(directoryInfo.Name), false, _configService.ParseTmdbIdFromReleaseName);
                var videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName)).ToList();

                return videoFiles
                    .Select(videoFile => GetCompletedDownloadQueueMovie(trackedDownload, videoFile, folderInfo))
                    .Where(localMovie => localMovie?.MediaInfo != null)
                    .OrderByDescending(localMovie => localMovie.Size)
                    .FirstOrDefault();
            }

            if (_diskProvider.FileExists(outputPath) &&
                MediaFileExtensions.Extensions.Contains(Path.GetExtension(outputPath)))
            {
                return GetCompletedDownloadQueueMovie(trackedDownload, outputPath, null);
            }

            return null;
        }

        private LocalMovie GetCompletedDownloadQueueMovie(TrackedDownload trackedDownload, string videoFile, ParsedMovieInfo folderInfo)
        {
            var fileInfo = Parser.Parser.ParseMoviePath(videoFile, _configService.ParseTmdbIdFromReleaseName);

            var localMovie = new LocalMovie
            {
                Path = videoFile,
                DownloadItem = trackedDownload.ImportItem,
                DownloadClientMovieInfo = trackedDownload.RemoteMovie?.ParsedMovieInfo,
                FolderMovieInfo = folderInfo,
                FileMovieInfo = fileInfo ?? GetFallbackFileMovieInfo(videoFile)
            };

            return _aggregationService.Augment(localMovie, trackedDownload.ImportItem);
        }

        private ParsedMovieInfo GetFallbackFileMovieInfo(string path)
        {
            return new ParsedMovieInfo
            {
                ReleaseTitle = Path.GetFileNameWithoutExtension(path),
                SimpleReleaseTitle = Path.GetFileNameWithoutExtension(path),
                Quality = QualityParser.ParseQuality(path),
                Languages = LanguageParser.ParseLanguages(path)
            };
        }

        private List<ImportDecision> GetCompletedDownloadImportDecisions(TrackedDownload trackedDownload, Movie movie, string outputPath)
        {
            if (_diskProvider.FolderExists(outputPath))
            {
                var directoryInfo = new DirectoryInfo(outputPath);
                var folderInfo = Parser.Parser.ParseMovieTitle(GetCleanedUpFolderName(directoryInfo.Name), false, _configService.ParseTmdbIdFromReleaseName);
                var videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName)).ToList();

                return _importDecisionMaker.GetImportDecisions(videoFiles, movie, trackedDownload.ImportItem, folderInfo, true);
            }

            if (_diskProvider.FileExists(outputPath) &&
                MediaFileExtensions.Extensions.Contains(Path.GetExtension(outputPath)))
            {
                return _importDecisionMaker.GetImportDecisions(new List<string> { outputPath }, movie, trackedDownload.ImportItem, null, true);
            }

            return new List<ImportDecision>();
        }

        private LocalMovie SelectQueueMediaInfoMovie(List<ImportDecision> decisions, Movie movie)
        {
            var approvedDecisions = decisions.Where(decision => decision.Approved).ToList();

            if (approvedDecisions.Any())
            {
                if (movie.QualityProfile != null)
                {
                    return approvedDecisions
                        .OrderByDescending(decision => decision.LocalMovie.Quality ?? new QualityModel { Quality = Quality.Unknown }, new QualityModelComparer(movie.QualityProfile))
                        .ThenByDescending(decision => decision.LocalMovie.Size)
                        .First()
                        .LocalMovie;
                }

                return approvedDecisions
                    .OrderByDescending(decision => decision.LocalMovie.Size)
                    .First()
                    .LocalMovie;
            }

            return decisions
                .Where(decision => decision.LocalMovie?.MediaInfo != null)
                .OrderByDescending(decision => decision.LocalMovie.Size)
                .FirstOrDefault()
                ?.LocalMovie;
        }

        private List<Language> GetSubtitleLanguages(LocalMovie localMovie, List<ExternalSubtitleFile> externalSubtitles)
        {
            var languages = new List<Language>();

            var embeddedSubtitleLanguages = localMovie.MediaInfo?.Subtitles?
                                                      .Where(language => language.IsNotNullOrWhiteSpace())
                                                      .Distinct()
                                                      .ToList() ?? new List<string>();

            foreach (var subtitleLanguage in embeddedSubtitleLanguages)
            {
                languages.AddIfNotNull(IsoLanguages.Find(subtitleLanguage)?.Language);
            }

            if (externalSubtitles != null)
            {
                foreach (var subtitleFile in externalSubtitles)
                {
                    languages.AddIfNotNull(subtitleFile.Info?.Language);
                }
            }

            return languages
                .Where(language => language != Language.Unknown)
                .GroupBy(language => language.Id)
                .Select(group => group.First())
                .ToList();
        }

        private List<ExternalSubtitleFile> GetExternalSubtitleFiles(LocalMovie localMovie)
        {
            if (localMovie?.Path.IsNullOrWhiteSpace() != false)
            {
                return new List<ExternalSubtitleFile>();
            }

            var sourceFolder = _diskProvider.GetParentFolder(localMovie.Path);

            if (sourceFolder.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(sourceFolder))
            {
                return new List<ExternalSubtitleFile>();
            }

            var subtitleFiles = _diskProvider.GetFiles(sourceFolder, false)
                                             .Where(file => SubtitleFileExtensions.Extensions.Contains(Path.GetExtension(file)))
                                             .ToList();

            if (subtitleFiles.Empty())
            {
                return new List<ExternalSubtitleFile>();
            }

            var sourceFileName = Path.GetFileNameWithoutExtension(localMovie.Path);
            var matchingFiles = subtitleFiles
                .Where(file => Path.GetFileNameWithoutExtension(file).StartsWithIgnoreCase(sourceFileName))
                .ToList();

            if (matchingFiles.Empty() && localMovie.FileMovieInfo != null)
            {
                matchingFiles = subtitleFiles
                    .Where(file =>
                    {
                        var fileMovieInfo = Parser.Parser.ParseMoviePath(file, _configService.ParseTmdbIdFromReleaseName);

                        return fileMovieInfo?.MovieTitle == localMovie.FileMovieInfo.MovieTitle &&
                               fileMovieInfo.Year.Equals(localMovie.FileMovieInfo.Year);
                    })
                    .ToList();
            }

            if (matchingFiles.Empty())
            {
                var videoFiles = _diskProvider.GetFiles(sourceFolder, false)
                                              .Where(file => MediaFileExtensions.Extensions.Contains(Path.GetExtension(file)))
                                              .ToList();

                if (videoFiles.Count == 1)
                {
                    matchingFiles = subtitleFiles;
                }
            }

            return matchingFiles
                .Select(file => new ExternalSubtitleFile
                {
                    Path = file,
                    Info = LanguageParser.ParseSubtitleLanguageInformation(file)
                })
                .ToList();
        }

        private string FormatValues<T>(IEnumerable<T> values)
        {
            var formattedValues = values?.Select(value => value?.ToString())
                                         .Where(value => value.IsNotNullOrWhiteSpace())
                                         .Distinct()
                                         .ToList();

            return formattedValues?.Any() == true ? string.Join(", ", formattedValues) : "none";
        }

        private string FormatExternalSubtitleFiles(List<ExternalSubtitleFile> subtitleFiles)
        {
            if (subtitleFiles.Empty())
            {
                return "none";
            }

            return string.Join("; ", subtitleFiles.Select(file =>
            {
                var info = file.Info;
                var details = new List<string>
                {
                    $"language: {info.Language}"
                };

                if (info.LanguageTags?.Any() == true)
                {
                    details.Add($"tags: {string.Join(", ", info.LanguageTags)}");
                }

                if (info.Title.IsNotNullOrWhiteSpace())
                {
                    details.Add($"title: {info.Title}");
                }

                if (info.Copy > 0)
                {
                    details.Add($"copy: {info.Copy}");
                }

                return $"{file.Path} [{string.Join(", ", details)}]";
            }));
        }

        private string GetCleanedUpFolderName(string folder)
        {
            return folder.Replace("_UNPACK_", "")
                         .Replace("_FAILED_", "");
        }

        private void EnsureRemoteMovie(TrackedDownload trackedDownload, Movie movie)
        {
            if (trackedDownload.RemoteMovie == null)
            {
                var parsed = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title, false, _configService.ParseTmdbIdFromReleaseName);
                trackedDownload.RemoteMovie = parsed != null
                    ? _parsingService.Map(parsed, movie.Id)
                    : new RemoteMovie { Movie = movie };
            }

            if (trackedDownload.RemoteMovie.Movie == null)
            {
                trackedDownload.RemoteMovie.Movie = movie;
            }
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping.", downloadItemOutputPath);
                return false;
            }

            return true;
        }

        private class ExternalSubtitleFile
        {
            public string Path { get; set; }
            public SubtitleTitleInfo Info { get; set; }
        }

        private class AudioLanguageSet
        {
            public HashSet<Language> KnownLanguages { get; } = new HashSet<Language>();
            public HashSet<string> DistinctAudioLanguages { get; } = new HashSet<string>();
        }
    }
}
