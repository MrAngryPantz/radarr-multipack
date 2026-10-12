using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackFileProfileFixture : CoreTest<MoviePackProfileSpecification>
    {
        private LocalMovie _file;
        private QualityDefinition _definition;

        [SetUp]
        public void Setup()
        {
            _file = new LocalMovie
            {
                Movie = new Movie
                {
                    MovieMetadata = new MovieMetadata { Runtime = 100 },
                    QualityProfile = new QualityProfile
                    {
                        Items = Qualities.QualityFixture.GetDefaultQualities(Quality.WEBDL720p),
                        Language = Language.English
                    }
                },
                Quality = new QualityModel(Quality.WEBDL720p),
                Languages = new List<Language> { Language.English },
                Size = 1000000
            };
            _definition = new QualityDefinition { Quality = Quality.WEBDL720p, MinSize = 0, MaxSize = 0 };
            Mocker.GetMock<IQualityDefinitionService>().Setup(s => s.Get(It.IsAny<Quality>())).Returns(_definition);
            Mocker.SetConstant(new QualityAllowedByProfileSpecification(TestLogger));
            Mocker.SetConstant(new LanguageSpecification(TestLogger));
            Mocker.SetConstant(new AcceptableSizeSpecification(Mocker.GetMock<IQualityDefinitionService>().Object, TestLogger));
            Mocker.SetConstant(new CustomFormatAllowedbyProfileSpecification(TestLogger));
        }

        [Test]
        public void should_accept_actual_file_that_meets_profile()
        {
            Subject.IsSatisfiedBy(_file, new DownloadClientItem { IsMoviePack = true }).Accepted.Should().BeTrue();
        }

        [TestCase("quality")]
        [TestCase("language")]
        [TestCase("custom format")]
        [TestCase("size")]
        public void should_reject_file_that_fails_its_own_profile(string failure)
        {
            if (failure == "quality")
            {
                _file.Quality = new QualityModel(Quality.CAM);
            }
            else if (failure == "language")
            {
                _file.Languages = new List<Language> { Language.French };
            }
            else if (failure == "custom format")
            {
                _file.CustomFormatScore = -1;
            }
            else
            {
                _definition.MaxSize = 0.001;
            }

            Subject.IsSatisfiedBy(_file, new DownloadClientItem { IsMoviePack = true }).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_preserve_ordinary_import_behavior()
        {
            _file.Quality = new QualityModel(Quality.CAM);
            Subject.IsSatisfiedBy(_file, new DownloadClientItem()).Accepted.Should().BeTrue();
        }
    }
}
