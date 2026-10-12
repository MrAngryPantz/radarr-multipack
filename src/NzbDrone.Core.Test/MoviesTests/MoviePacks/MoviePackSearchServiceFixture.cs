using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    public class MoviePackSearchServiceFixture : CoreTest<MoviePackSearchService>
    {
        private Mock<IIndexer> _indexer;
        private MovieSearchCriteria _criteria;
        private List<ReleaseInfo> _releases;

        [SetUp]
        public void SetUp()
        {
            Mocker.GetMock<IIndexerStatusService>().Setup(s => s.GetBlockedProviders()).Returns(new List<IndexerStatus>());
            _indexer = Mocker.GetMock<IIndexer>();
            _indexer.SetupGet(i => i.SupportsSearch).Returns(true);
            _indexer.SetupGet(i => i.Definition).Returns(new IndexerDefinition { Id = 1 });
            _criteria = new MovieSearchCriteria { Movie = new Movie { Id = 1 }, SceneTitles = new List<string> { "Example" } };
            _releases = new List<ReleaseInfo>
            {
                new ReleaseInfo { Guid = "pack", Title = "Example Collection" },
                new ReleaseInfo { Guid = "movie", Title = "Example Arrival 2000" }
            };
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.GetSearchTitles(It.IsAny<Movie>())).Returns(new List<string> { "Example" });
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.Resolve("Example Collection")).Returns(new MoviePack());
            _indexer.Setup(i => i.Fetch(It.IsAny<MovieSearchCriteria>())).ReturnsAsync(_releases);
        }

        [Test]
        public async Task should_only_add_pack_results_from_broad_search()
        {
            var releases = await Subject.Search(_indexer.Object, _criteria);

            releases.Select(r => r.Guid).Should().Equal("pack");
        }

        [Test]
        public async Task should_reuse_collection_query_across_movies()
        {
            await Subject.Search(_indexer.Object, _criteria);
            _criteria.Movie = new Movie { Id = 2 };
            await Subject.Search(_indexer.Object, _criteria);

            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
        }

        [Test]
        public async Task should_share_in_flight_pack_query()
        {
            var pending = new TaskCompletionSource<IList<ReleaseInfo>>();
            _indexer.Setup(i => i.Fetch(It.IsAny<MovieSearchCriteria>())).Returns(pending.Task);
            var first = Subject.Search(_indexer.Object, _criteria);
            var second = Subject.Search(_indexer.Object, _criteria);
            pending.SetResult(_releases);
            await Task.WhenAll(first, second);

            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
        }

        [Test]
        public async Task should_retry_after_failed_query()
        {
            _indexer.SetupSequence(i => i.Fetch(It.IsAny<MovieSearchCriteria>()))
                .ThrowsAsync(new InvalidOperationException("Test failure"))
                .ReturnsAsync(_releases);

            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.Search(_indexer.Object, _criteria));
            var releases = await Subject.Search(_indexer.Object, _criteria);

            releases.Should().ContainSingle();
            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
        }

        [Test]
        public async Task should_refresh_after_indexer_settings_change()
        {
            await Subject.Search(_indexer.Object, _criteria);
            Subject.Handle(new ProviderUpdatedEvent<IIndexer>(_indexer.Object.Definition));
            await Subject.Search(_indexer.Object, _criteria);

            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
        }

        [Test]
        public async Task should_keep_scene_alias_queries_separate()
        {
            await Subject.Search(_indexer.Object, _criteria);
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.GetSearchTitles(It.IsAny<Movie>())).Returns(new List<string> { "Different Collection" });
            await Subject.Search(_indexer.Object, _criteria);

            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
        }

        [Test]
        public async Task should_not_send_extra_queries_after_indexer_rate_limit()
        {
            Mocker.GetMock<IIndexerStatusService>().Setup(s => s.GetBlockedProviders())
                .Returns(new List<IndexerStatus> { new IndexerStatus { ProviderId = 1, DisabledTill = DateTime.UtcNow.AddHours(1) } });

            var releases = await Subject.Search(_indexer.Object, _criteria);

            releases.Should().BeEmpty();
            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Never());
        }

        [Test]
        public async Task should_not_query_rss_only_indexers()
        {
            _indexer.SetupGet(i => i.SupportsSearch).Returns(false);

            var releases = await Subject.Search(_indexer.Object, _criteria);

            releases.Should().BeEmpty();
            _indexer.Verify(i => i.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Never());
        }
    }
}
