using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Collections;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackResolverFixture : CoreTest<MoviePackResolver>
    {
        private List<Movie> _movies;
        private MovieCollection _collection;

        [SetUp]
        public void Setup()
        {
            _movies = new List<Movie>
            {
                Movie(1, "The Matrix", 1999),
                Movie(2, "The Matrix Reloaded", 2003),
                Movie(3, "The Matrix Revolutions", 2003)
            };
            _collection = new MovieCollection { TmdbId = 2344, LastInfoSync = DateTime.UtcNow, Title = "The Matrix Collection", Movies = _movies.Select(m => m.MovieMetadata.Value).ToList() };
            Mocker.GetMock<IMovieMetadataService>().Setup(s => s.GetMoviesByCollectionTmdbId(2344)).Returns(() => _collection.Movies);
            Mocker.GetMock<IMovieCollectionService>().Setup(s => s.GetAllCollections()).Returns(new List<MovieCollection> { _collection });
            Mocker.GetMock<IMovieService>().Setup(s => s.FindByTmdbId(It.IsAny<int>())).Returns<int>(id => _movies.SingleOrDefault(m => m.TmdbId == id));
            Mocker.GetMock<IMovieService>()
                .Setup(s => s.FindByTitle(It.IsAny<List<string>>(), It.IsAny<int?>(), It.IsAny<List<string>>(), It.IsAny<List<Movie>>()))
                .Returns<List<string>, int?, List<string>, List<Movie>>((titles, year, other, candidates) => _movies.SingleOrDefault(m =>
                    titles.Any(t => Parser.Parser.CleanMovieTitle(t) == Parser.Parser.CleanMovieTitle(m.Title)) && (!year.HasValue || m.Year == year)));
            Mocker.SetConstant<IParsingService>(Mocker.Resolve<ParsingService>());
        }

        private static Movie Movie(int id, string title, int year)
        {
            return new Movie { Id = id, TmdbId = id + 100, Title = title, Year = year, Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } };
        }

        [TestCase("The.Matrix.Trilogy.1999-2003.1080p.BluRay")]
        [TestCase("Matrix Collection (1999-2003) 1080p")]
        [TestCase("The_Matrix_Complete_Collection_1080p")]
        [TestCase("Matrix 3 Movies Collection 1080p")]
        public void should_resolve_collection_with_clear_membership(string title)
        {
            var pack = Subject.Resolve(title);
            pack.Error.Should().BeNull();
            pack.MovieTmdbIds.Should().BeEquivalentTo(new[] { 101, 102, 103 });
        }

        [TestCase("The.Matrix.1999.1080p.BluRay")]
        [TestCase("The.Matrix.Reloaded.2003.1080p")]
        public void should_leave_single_movie_releases_unchanged(string title)
        {
            Subject.Resolve(title).Should().BeNull();
        }

        [TestCase("Matrix Duology 1080p")]
        [TestCase("Matrix 4 Movies Collection 1080p")]
        [TestCase("Matrix Reloaded Collection 1080p")]
        [TestCase("Unknown Collection 1080p")]
        public void should_reject_ambiguous_or_inconsistent_pack_names(string title)
        {
            Subject.Resolve(title).Error.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void should_not_guess_first_three_when_collection_has_grown()
        {
            _collection.Movies.Add(new MovieMetadata { TmdbId = 104, Title = "The Matrix Resurrections", Year = 2021 });
            Subject.Resolve("Matrix Trilogy 1080p").Error.Should().NotBeNull();
            Subject.Resolve("Matrix Trilogy 1999-2003 1080p").Error.Should().BeNull();
        }

        [Test]
        public void should_require_collection_refresh_before_using_its_membership()
        {
            _collection.LastInfoSync = null;
            Subject.Resolve("Matrix Collection").Error.Should().NotBeNull();
        }

        [Test]
        public void should_check_named_count_after_collection_marker()
        {
            Subject.Resolve("Matrix Collection Duology 1080p").Error.Should().NotBeNull();
        }

        [Test]
        public void should_require_all_members_in_library_and_recover_when_added()
        {
            var movie = _movies.Last();
            _movies.Remove(movie);
            var pack = Subject.Resolve("Matrix Collection 1080p");
            pack.Error.Should().NotBeNull();
            _movies.Add(movie);
            Subject.Refresh(pack).Error.Should().BeNull();
        }

        [Test]
        public void should_resolve_explicit_list()
        {
            var pack = Subject.Resolve("The Matrix (1999) + The Matrix Reloaded (2003) 1080p BluRay");
            pack.Error.Should().BeNull();
            pack.MovieTmdbIds.Should().BeEquivalentTo(new[] { 101, 102 });
        }

        [TestCase("The Matrix (1999) + Missing Film (2000) 1080p")]
        [TestCase("The Matrix (1999) + The Matrix (1999) 1080p")]
        public void should_reject_incomplete_or_duplicated_lists(string title)
        {
            Subject.Resolve(title).Error.Should().NotBeNull();
        }

        [Test]
        public void should_restore_saved_membership_despite_collection_changes_and_renamed_download()
        {
            _collection.Movies.Clear();
            var history = new List<MovieHistory> { new MovieHistory { Date = DateTime.UtcNow, Data = new Dictionary<string, string> { [MoviePack.HistoryKey] = "101,102,103" } } };
            Subject.Restore("renamed download", history).MovieTmdbIds.Should().Equal(101, 102, 103);
        }

        [TestCase("101,102,101")]
        [TestCase("101,unknown")]
        [TestCase("101,-102")]
        [TestCase("101")]
        public void should_hold_corrupt_saved_membership(string ids)
        {
            var history = new List<MovieHistory> { new MovieHistory { Data = new Dictionary<string, string> { [MoviePack.HistoryKey] = ids } } };
            Subject.Restore("Matrix Collection", history).Error.Should().NotBeNull();
        }

        [Test]
        public void should_match_each_file_independently()
        {
            var pack = Subject.Resolve("Matrix Collection");
            Subject.MatchFile(Path.Combine("Matrix Collection", "The.Matrix.Reloaded.2003.1080p.mkv"), pack).Id.Should().Be(2);
            Subject.MatchFile(Path.Combine("The Matrix (1999)", "Unknown.Movie.2003.mkv"), pack).Should().BeNull();
            Subject.MatchFile(Path.Combine("Matrix Collection", "unknown.mkv"), pack).Should().BeNull();
        }

        [Test]
        public void should_allow_obfuscated_file_only_inside_an_individual_movie_folder()
        {
            var pack = Subject.Resolve("Matrix Collection");
            Subject.MatchFile(Path.Combine("The Matrix Reloaded (2003)", "a8ec56d0d9b240ffc654fe680e87d3e1.mkv"), pack).Id.Should().Be(2);
        }

        [Test]
        public void should_reject_known_movie_outside_the_saved_pack()
        {
            var pack = Subject.Resolve("Matrix Collection");
            _movies.Add(Movie(4, "Another Film", 2010));
            Subject.MatchFile(Path.Combine("The Matrix (1999)", "Another.Film.2010.mkv"), pack).Should().BeNull();
        }

        [Test]
        public void should_not_treat_movie_named_collection_as_a_pack()
        {
            _movies.Add(Movie(4, "The Collection", 2012));
            Subject.Resolve("The.Collection.2012.1080p.BluRay").Should().BeNull();
        }
    }
}
