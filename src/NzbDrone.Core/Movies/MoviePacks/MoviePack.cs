using System.Collections.Generic;

namespace NzbDrone.Core.Movies.MoviePacks
{
    public class MoviePack
    {
        public const string HistoryKey = "moviePackTmdbIds";

        public List<int> MovieTmdbIds { get; set; } = new List<int>();
        public List<Movie> Movies { get; set; } = new List<Movie>();
        public string Error { get; set; }
    }
}
