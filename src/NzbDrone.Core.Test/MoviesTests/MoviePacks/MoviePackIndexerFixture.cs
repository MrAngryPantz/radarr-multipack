using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.FileList;
using NzbDrone.Core.Indexers.HDBits;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Nyaa;
using NzbDrone.Core.Indexers.PassThePopcorn;
using NzbDrone.Core.Indexers.TorrentPotato;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackIndexerFixture : CoreTest<NewznabRequestGenerator>
    {
        private MovieSearchCriteria _criteria;

        [SetUp]
        public void Setup()
        {
            _criteria = new MovieSearchCriteria
            {
                Movie = new Movie { Title = "The Matrix", Year = 1999, TmdbId = 603, ImdbId = "tt0133093" },
                SceneTitles = new List<string> { "The Matrix" },
                MoviePackSearch = true
            };
            Subject.Settings = new NewznabSettings { BaseUrl = "http://indexer.test", Categories = new[] { 2000 }, ApiKey = "test" };
        }

        [TestCase(true, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 2)]
        [TestCase(false, false, 0)]
        public void should_use_advertised_title_search_without_movie_id_or_year(bool generic, bool movie, int count)
        {
            Mocker.GetMock<INewznabCapabilitiesProvider>().Setup(s => s.GetCapabilities(It.IsAny<NewznabSettings>())).Returns(new NewznabCapabilities
            {
                SupportedSearchParameters = generic ? new[] { "q" } : null,
                SupportedMovieSearchParameters = movie ? new[] { "q", "imdbid", "tmdbid" } : new[] { "imdbid", "tmdbid" }
            });
            var chains = Subject.GetSearchRequests(_criteria).GetAllTiers().ToList();
            chains.Should().HaveCount(count);
            foreach (var request in chains.Select(c => c.First()))
            {
                request.Url.Query.Should().Contain("q=").And.NotContain("imdbid=").And.NotContain("tmdbid=").And.NotContain("1999");
            }

            if (count > 0)
            {
                chains[0].Take(3).Last().Url.Query.Should().Contain("offset=200");
            }
        }

        [TestCase("FileList")]
        [TestCase("HDBits")]
        [TestCase("Nyaa")]
        [TestCase("PassThePopcorn")]
        [TestCase("TorrentPotato")]
        public void should_search_native_indexers_by_collection_title(string name)
        {
            IIndexerRequestGenerator generator = name switch
            {
                "FileList" => new FileListRequestGenerator { Settings = new FileListSettings { BaseUrl = "http://indexer.test", Username = "test", Passkey = "test", Categories = new[] { 1 } } },
                "HDBits" => new HDBitsRequestGenerator { Settings = new HDBitsSettings { BaseUrl = "http://indexer.test", Username = "test", ApiKey = "test", Categories = new[] { 1 }, Codecs = new[] { 1 }, Mediums = new[] { 1 } } },
                "Nyaa" => new NyaaRequestGenerator { Settings = new NyaaSettings { BaseUrl = "http://indexer.test" } },
                "PassThePopcorn" => new PassThePopcornRequestGenerator(new PassThePopcornSettings { BaseUrl = "http://indexer.test", APIUser = "test", APIKey = "test" }),
                _ => new TorrentPotatoRequestGenerator { Settings = new TorrentPotatoSettings { BaseUrl = "http://indexer.test", Passkey = "test" } }
            };
            var request = generator.GetSearchRequests(_criteria).GetAllTiers().Single().First();
            var text = request.Url.FullUri + request.HttpRequest.ContentSummary;
            text.Should().Contain("Matrix").And.NotContain("0133093").And.NotContain("1999");
        }
    }
}
