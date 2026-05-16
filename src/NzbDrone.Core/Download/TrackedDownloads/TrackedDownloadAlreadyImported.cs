using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadAlreadyImported
    {
        bool IsImported(TrackedDownload trackedDownload, List<MovieHistory> historyItems);
    }

    public class TrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public TrackedDownloadAlreadyImported(IMediaFileService mediaFileService, Logger logger)
        {
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public bool IsImported(TrackedDownload trackedDownload, List<MovieHistory> historyItems)
        {
            _logger.Trace("Checking if all movies for '{0}' have been imported", trackedDownload.DownloadItem.Title);

            if (historyItems.Empty())
            {
                _logger.Trace("No history for {0}", trackedDownload.DownloadItem.Title);
                return false;
            }

            var movie = trackedDownload.RemoteMovie.Movie;

            var lastHistoryItem = historyItems.FirstOrDefault(h => h.MovieId == movie.Id);

            if (lastHistoryItem == null)
            {
                _logger.Trace("No history for movie: {0}", movie.ToString());
                return false;
            }

            var allMoviesImportedInHistory = lastHistoryItem.EventType == MovieHistoryEventType.DownloadFolderImported &&
                                             ExistingFileMatchesImportHistory(movie, lastHistoryItem);
            _logger.Trace("Last event for movie: {0} is: {1}", movie, lastHistoryItem.EventType);

            _logger.Trace("All movies for '{0}' have been imported: {1}", trackedDownload.DownloadItem.Title, allMoviesImportedInHistory);
            return allMoviesImportedInHistory;
        }

        private bool ExistingFileMatchesImportHistory(Movie movie, MovieHistory historyItem)
        {
            var importedSize = GetImportedSize(historyItem);

            if (importedSize <= 0)
            {
                _logger.Trace("No imported file size recorded for movie: {0}", movie);
                return false;
            }

            var movieFileId = GetImportedFileId(historyItem) ?? movie.MovieFileId;

            if (movieFileId <= 0)
            {
                _logger.Trace("No current movie file for movie: {0}", movie);
                return false;
            }

            var movieFile = _mediaFileService.GetMovies(new[] { movieFileId }).FirstOrDefault();

            if (movieFile == null)
            {
                _logger.Trace("Current movie file {0} was not found for movie: {1}", movieFileId, movie);
                return false;
            }

            if (movieFile.Size != importedSize)
            {
                _logger.Trace("Current movie file size {0} does not match imported size {1} for movie: {2}", movieFile.Size, importedSize, movie);
                return false;
            }

            return true;
        }

        private static int? GetImportedFileId(MovieHistory historyItem)
        {
            if (historyItem.Data == null ||
                !historyItem.Data.TryGetValue("FileId", out var fileIdText) ||
                !int.TryParse(fileIdText, out var fileId))
            {
                return null;
            }

            return fileId;
        }

        private static long GetImportedSize(MovieHistory historyItem)
        {
            if (historyItem.Data == null ||
                !historyItem.Data.TryGetValue("Size", out var sizeText) ||
                !long.TryParse(sizeText, out var size))
            {
                return 0;
            }

            return size;
        }
    }
}
