using System.Collections.Generic;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISearchForNewMovie
    {
        List<Movie> SearchForNewMovie(string title);

        Movie SearchForNewMovieByExactTitle(string title, int year, List<Movie> candidates);

        MovieMetadata MapMovieToTmdbMovie(MovieMetadata movie);
    }
}
