using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MoviePacks;

namespace NzbDrone.Core.Parser.Model
{
    public class RemoteMovie
    {
        public ReleaseInfo Release { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public MovieMatchType MovieMatchType { get; set; }
        public Movie Movie { get; set; }
        public bool MovieRequested { get; set; }
        public bool DownloadAllowed { get; set; }
        public TorrentSeedConfiguration SeedConfiguration { get; set; }
        public List<Language> Languages { get; set; }
        public ReleaseSourceType ReleaseSource { get; set; }
        public MoviePack MoviePack { get; set; }

        public IEnumerable<Movie> GetMovies()
        {
            return MoviePack?.Movies ?? (Movie == null ? new List<Movie>() : new List<Movie> { Movie });
        }

        public RemoteMovie ForMovie(Movie movie)
        {
            var result = (RemoteMovie)MemberwiseClone();
            result.Movie = movie;
            return result;
        }

        public long GetMovieSize(long totalSize)
        {
            if (MoviePack == null || Movie == null || MoviePack.Movies.Count == 0)
            {
                return totalSize;
            }

            // An indexer supplies the whole pack size. Estimate each movie's share by
            // runtime for selection; import checks use the actual individual files.
            var runtime = Movie.MovieMetadata.Value.Runtime > 0 ? Movie.MovieMetadata.Value.Runtime : 110;
            var totalRuntime = MoviePack.Movies.Sum(m => m.MovieMetadata.Value.Runtime > 0 ? m.MovieMetadata.Value.Runtime : 110);
            return (long)Math.Ceiling((decimal)totalSize * runtime / totalRuntime);
        }

        public RemoteMovie()
        {
            CustomFormats = new List<CustomFormat>();
            Languages = new List<Language>();
        }

        public override string ToString()
        {
            return Release.Title;
        }
    }

    public enum ReleaseSourceType
    {
        Unknown = 0,
        Rss = 1,
        Search = 2,
        UserInvokedSearch = 3,
        InteractiveSearch = 4,
        ReleasePush = 5
    }
}
