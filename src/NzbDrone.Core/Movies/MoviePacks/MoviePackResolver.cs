using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies.Collections;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Movies.MoviePacks
{
    public interface IMoviePackResolver
    {
        MoviePack Resolve(string title);
        MoviePack Restore(string title, IEnumerable<MovieHistory> history);
        MoviePack Refresh(MoviePack pack);
        Movie MatchFile(string path, MoviePack pack);
        List<string> GetSearchTitles(Movie movie);
    }

    public class MoviePackResolver : IMoviePackResolver
    {
        private static readonly Regex PackMarker = new Regex(@"\b(?:collection|trilogy|quadrilogy|tetralogy|pentalogy|hexalogy|heptalogy|octalogy|duology|anthology|box[ ._-]*set|(?:movie|film)[ ._-]*pack)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearRange = new Regex(@"(?<!\d)(?<first>(?:18|19|20)\d{2})\s*[-–]\s*(?<last>(?:18|19|20)\d{2})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex MovieCount = new Regex(@"(?<!\d)(?<count>\d{1,3})[ ._-]*(?:movies?|films?)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CollectionSuffix = new Regex(@"\s+collection$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Separators = new Regex(@"[\W_]+", RegexOptions.Compiled);
        private static readonly Regex PrefixWords = new Regex(@"\b(?:the|complete|ultimate|extended|edition|movies?|films?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, int> NamedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "duology", 2 }, { "trilogy", 3 }, { "quadrilogy", 4 }, { "tetralogy", 4 },
            { "pentalogy", 5 }, { "hexalogy", 6 }, { "heptalogy", 7 }, { "octalogy", 8 }
        };

        private readonly IMovieService _movies;
        private readonly IMovieCollectionService _collections;
        private readonly IParsingService _parsing;
        private readonly IMovieMetadataService _metadata;

        public MoviePackResolver(IMovieService movies, IMovieCollectionService collections, IParsingService parsing, IMovieMetadataService metadata)
        {
            _movies = movies;
            _collections = collections;
            _parsing = parsing;
            _metadata = metadata;
        }

        public MoviePack Resolve(string title)
        {
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var listedMovies = Regex.Split(title, @"\s+(?:\+|&)\s+");
            if (listedMovies.Length > 1 && listedMovies.All(t => Regex.IsMatch(t, @"(?:18|19|20)\d{2}")))
            {
                var pack = new MoviePack();
                foreach (var listedTitle in listedMovies)
                {
                    var movie = MatchTitle(listedTitle);
                    if (movie == null)
                    {
                        pack.Error = "Every movie listed in a pack must match a movie in the library";
                    }
                    else
                    {
                        pack.Movies.Add(movie);
                        pack.MovieTmdbIds.Add(movie.TmdbId);
                    }
                }

                if (pack.MovieTmdbIds.Distinct().Count() != listedMovies.Length)
                {
                    pack.Error = "The movie list is incomplete or contains duplicate matches";
                }

                return pack;
            }

            if (!PackMarker.IsMatch(title.Replace('_', ' ')))
            {
                return null;
            }

            var matches = new List<MoviePack>();
            foreach (var collection in _collections.GetAllCollections())
            {
                var collectionTitle = GetCollectionTitle(collection.Title);
                if (collectionTitle.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var words = Separators.Split(collectionTitle).Where(w => w.Length > 0).ToList();
                if (words.FirstOrDefault()?.Equals("the", StringComparison.OrdinalIgnoreCase) == true)
                {
                    words.RemoveAt(0);
                }

                if (words.Count == 0)
                {
                    continue;
                }

                var pattern = @"^(?:the[\W_]+)?" + string.Join(@"[\W_]+", words.Select(Regex.Escape)) + @"(?=$|[\W_])(?<tail>.*)$";
                var match = Regex.Match(title, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (!match.Success)
                {
                    continue;
                }

                var tail = match.Groups["tail"].Value.Replace('_', ' ');
                var marker = PackMarker.Match(tail);
                if (!marker.Success || Regex.IsMatch(PrefixWords.Replace(tail.Substring(0, marker.Index), ""), @"\p{L}"))
                {
                    continue;
                }

                var metadata = _metadata.GetMoviesByCollectionTmdbId(collection.TmdbId);
                var range = YearRange.Match(tail);
                if (range.Success)
                {
                    var first = int.Parse(range.Groups["first"].Value);
                    var last = int.Parse(range.Groups["last"].Value);
                    metadata = metadata.Where(m => m.Year >= first && m.Year <= last).ToList();
                }

                var pack = new MoviePack { MovieTmdbIds = metadata.Select(m => m.TmdbId).Distinct().ToList() };
                Refresh(pack);

                if (!collection.LastInfoSync.HasValue)
                {
                    pack.Error = "Refresh this collection before downloading packs so its complete movie list is known";
                }

                var counts = MovieCount.Matches(tail).Select(m => int.Parse(m.Groups["count"].Value))
                    .Concat(PackMarker.Matches(tail).Select(m => NamedCounts.GetValueOrDefault(m.Value)))
                    .Where(count => count > 0).Distinct().ToList();
                if (counts.Any(count => count != pack.MovieTmdbIds.Count))
                {
                    pack.Error = "The number of movies in the title does not match the collection metadata";
                }

                matches.Add(pack);
            }

            return matches.Count switch
            {
                0 => IsOrdinaryMovie(title) ? null : new MoviePack { Error = "The pack cannot be matched to a known collection or an explicit movie list" },
                1 => matches[0],
                _ => new MoviePack { Error = "The pack title matches more than one collection" }
            };
        }

        public MoviePack Restore(string title, IEnumerable<MovieHistory> history)
        {
            var saved = history?.OrderByDescending(h => h.Date)
                .FirstOrDefault(h => h.Data?.ContainsKey(MoviePack.HistoryKey) == true);
            if (saved == null)
            {
                return Resolve(title);
            }

            var pack = new MoviePack();
            foreach (var value in saved.Data[MoviePack.HistoryKey].Split(','))
            {
                if (!int.TryParse(value, out var tmdbId) || tmdbId <= 0 || pack.MovieTmdbIds.Contains(tmdbId))
                {
                    pack.Error = "The saved movie pack list is invalid; manual review is required";
                    return pack;
                }

                pack.MovieTmdbIds.Add(tmdbId);
            }

            return Refresh(pack);
        }

        public MoviePack Refresh(MoviePack pack)
        {
            if (pack.Error == "Every movie in the pack must have metadata and already be in the library")
            {
                pack.Error = null;
            }

            pack.Movies = pack.MovieTmdbIds.Select(_movies.FindByTmdbId).Where(m => m != null).ToList();
            if (pack.MovieTmdbIds.Count < 2 || pack.MovieTmdbIds.Any(id => id <= 0) || pack.MovieTmdbIds.Distinct().Count() != pack.MovieTmdbIds.Count || pack.MovieTmdbIds.Count != pack.Movies.Count)
            {
                pack.Error = "Every movie in the pack must have metadata and already be in the library";
            }

            return pack;
        }

        public Movie MatchFile(string path, MoviePack pack)
        {
            // Resolve the file independently. A known but unexpected movie must never
            // inherit the identity of the download folder or another file in the pack.
            var movie = MatchTitle(Path.GetFileName(path));
            if (movie != null)
            {
                return pack.MovieTmdbIds.Contains(movie.TmdbId) ? movie : null;
            }

            var parsed = Parser.Parser.ParseMovieTitle(Path.GetFileName(path));
            if (parsed?.Year > 0 || parsed?.TmdbId > 0 || parsed?.ImdbId.IsNotNullOrWhiteSpace() == true)
            {
                return null;
            }

            // Obfuscated files are only usable when their immediate folder identifies
            // an individual movie. The collection folder itself is never a movie match.
            var parent = Path.GetFileName(Path.GetDirectoryName(path));
            movie = MatchTitle(parent);
            return movie != null && pack.MovieTmdbIds.Contains(movie.TmdbId) ? movie : null;
        }

        public List<string> GetSearchTitles(Movie movie)
        {
            var collectionId = movie.MovieMetadata.Value.CollectionTmdbId;
            var collection = collectionId > 0 ? _collections.FindByTmdbId(collectionId) : null;
            return collection == null
                ? new List<string> { movie.Title }
                : new List<string> { GetCollectionTitle(collection.Title) };
        }

        private bool IsOrdinaryMovie(string title)
        {
            var movie = MatchTitle(title);
            var parsed = Parser.Parser.ParseMovieTitle(title);
            return movie != null && parsed != null && Parser.Parser.CleanMovieTitle(parsed.PrimaryMovieTitle) == Parser.Parser.CleanMovieTitle(movie.Title);
        }

        private Movie MatchTitle(string title)
        {
            var parsed = Parser.Parser.ParseMovieTitle(title);
            if (parsed == null)
            {
                return null;
            }

            try
            {
                return _parsing.Map(parsed, parsed.ImdbId, parsed.TmdbId)?.Movie;
            }
            catch (MultipleMoviesFoundException)
            {
                return null;
            }
        }

        private static string GetCollectionTitle(string title)
        {
            return CollectionSuffix.Replace(title ?? "", "").Trim();
        }
    }
}
