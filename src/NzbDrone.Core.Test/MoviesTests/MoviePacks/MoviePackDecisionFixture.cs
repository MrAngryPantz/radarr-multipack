using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackDecisionFixture : CoreTest<DownloadDecisionMaker>
    {
        private MoviePack _pack;
        private List<ReleaseInfo> _reports;
        private Mock<IDownloadDecisionEngineSpecification> _spec;

        [SetUp]
        public void Setup()
        {
            _pack = new MoviePack
            {
                MovieTmdbIds = new List<int> { 101, 102 },
                Movies = new List<Movie>
                {
                    new Movie { Id = 1, TmdbId = 101, Title = "First", Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } },
                    new Movie { Id = 2, TmdbId = 102, Title = "Second", Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } }
                }
            };
            _reports = new List<ReleaseInfo> { new ReleaseInfo { Title = "Example.Collection.2000-2001.1080p.BluRay", Size = 3000000000 } };
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.Resolve(It.IsAny<string>())).Returns(_pack);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            _spec = new Mock<IDownloadDecisionEngineSpecification>();
            _spec.Setup(s => s.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Accept());
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new[] { _spec.Object });
        }

        [Test]
        public void should_apply_release_checks_to_every_missing_movie()
        {
            Subject.GetRssDecision(_reports).Single().Approved.Should().BeTrue();
            foreach (var movie in _pack.Movies)
            {
                _spec.Verify(s => s.IsSatisfiedBy(It.Is<RemoteMovie>(r => r.Movie.Id == movie.Id), It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            }

            _reports.Single().MoviePackTmdbIds.Should().Equal(101, 102);
        }

        [Test]
        public void should_reject_if_any_member_fails_its_profile_rules()
        {
            _spec.Setup(s => s.IsSatisfiedBy(It.Is<RemoteMovie>(r => r.Movie.Id == 2), It.IsAny<ReleaseDecisionInformation>()))
                .Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.QualityNotWanted, "Quality is not allowed"));
            var decision = Subject.GetRssDecision(_reports).Single();
            decision.Approved.Should().BeFalse();
            decision.Rejections.Single().Message.Should().Contain("Second");
        }

        [Test]
        public void should_keep_pack_eligible_when_another_member_already_has_a_file()
        {
            _pack.Movies[0].MovieFileId = 25;
            var decision = Subject.GetRssDecision(_reports).Single();
            decision.Approved.Should().BeTrue();
            decision.RemoteMovie.Movie.Id.Should().Be(2);
            _spec.Verify(s => s.IsSatisfiedBy(It.Is<RemoteMovie>(r => r.Movie.Id == 1), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        [Test]
        public void should_reject_complete_pack()
        {
            _pack.Movies.ForEach(m => m.MovieFileId = 25);
            Subject.GetRssDecision(_reports).Single().Rejections.Single().Reason.Should().Be(DownloadRejectionReason.MoviePackComplete);
        }

        [TestCase(false, false, true)]
        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        public void should_require_opt_in_disabled_upgrades_and_monitoring_for_every_movie(bool enabled, bool upgrade, bool monitored)
        {
            _pack.Movies[1].QualityProfile.AllowMoviePacks = enabled;
            _pack.Movies[1].QualityProfile.UpgradeAllowed = upgrade;
            _pack.Movies[1].Monitored = monitored;
            Subject.GetRssDecision(_reports).Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_reject_incomplete_metadata()
        {
            _pack.Error = "Incomplete movie list";
            Subject.GetRssDecision(_reports).Single().Rejections.Single().Reason.Should().Be(DownloadRejectionReason.MoviePackUnresolved);
        }

        [Test]
        public void should_accept_search_for_any_pack_member()
        {
            var decision = Subject.GetSearchDecision(_reports, new MovieSearchCriteria { Movie = _pack.Movies[1] }).Single();
            decision.Approved.Should().BeTrue();
            decision.RemoteMovie.MovieRequested.Should().BeTrue();
        }

        [Test]
        public void should_reject_pack_without_requested_movie()
        {
            Subject.GetSearchDecision(_reports, new MovieSearchCriteria { Movie = new Movie { Id = 7 } }).Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_use_per_movie_runtime_to_estimate_release_size()
        {
            _pack.Movies[0].MovieMetadata.Value.Runtime = 100;
            _pack.Movies[1].MovieMetadata.Value.Runtime = 200;
            var remote = new RemoteMovie { Movie = _pack.Movies[0], MoviePack = _pack };
            remote.GetMovieSize(3000).Should().Be(1000);
            remote.ForMovie(_pack.Movies[1]).GetMovieSize(3000).Should().Be(2000);
        }
    }
}
