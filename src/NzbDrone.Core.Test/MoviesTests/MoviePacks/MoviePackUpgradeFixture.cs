using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackUpgradeFixture : CoreTest<UpgradableSpecification>
    {
        [TestCase("quality")]
        [TestCase("proper")]
        [TestCase("format")]
        public void should_disable_every_kind_of_upgrade(string kind)
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred" };
            var profile = new QualityProfile
            {
                AllowMoviePacks = true,
                UpgradeAllowed = true,
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = 100 } }
            };
            var existing = new QualityModel(Quality.HDTV720p);
            var candidate = kind == "quality" ? new QualityModel(Quality.Bluray1080p) : new QualityModel(Quality.HDTV720p, new Revision(2));
            var formats = kind == "format" ? new List<CustomFormat> { format } : new List<CustomFormat>();
            Subject.IsUpgradable(profile, existing, new List<CustomFormat>(), candidate, formats).Should().Be(UpgradeableRejectReason.UpgradesNotAllowed);
            Subject.IsUpgradeAllowed(profile, existing, new List<CustomFormat>(), candidate, formats).Should().BeFalse();
            Subject.QualityCutoffNotMet(profile, existing, candidate).Should().BeFalse();
            Subject.CutoffNotMet(profile, existing, new List<CustomFormat>(), candidate).Should().BeFalse();
        }
    }
}
