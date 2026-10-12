using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MoviesTests.MoviePacks
{
    [TestFixture]
    public class MoviePackFolderFixture : CoreTest<DownloadedMovieImportService>
    {
        [Test]
        public void should_identify_each_file_in_a_folder_scan_and_keep_the_sources()
        {
            var path = @"C:\Downloads\Example Collection".AsOsAgnostic();
            var firstPath = Path.Combine(path, "First.2000.mkv");
            var secondPath = Path.Combine(path, "Second.2001.mkv");
            var unknown = Path.Combine(path, "unknown.mkv");
            var first = new Movie { Id = 1, Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } };
            var second = new Movie { Id = 2, Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } };
            var pack = new MoviePack { Movies = new List<Movie> { first, second }, MovieTmdbIds = new List<int> { 101, 102 } };
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(path)).Returns(true);
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(path, true)).Returns(new[] { firstPath, secondPath, unknown });
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.Resolve("Example Collection")).Returns(pack);
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.MatchFile(firstPath, pack)).Returns(first);
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.MatchFile(secondPath, pack)).Returns(second);
            Mocker.GetMock<IMakeImportDecision>().Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedMovieInfo>(), true)).Returns(new List<ImportDecision>());
            Mocker.GetMock<IImportApprovedMovie>().Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, It.IsAny<DownloadClientItem>(), ImportMode.Copy)).Returns(new List<ImportResult>());

            Subject.ProcessPath(path, ImportMode.Move, first).Should().ContainSingle(r => r.Result == ImportResultType.Rejected);

            Mocker.GetMock<IMakeImportDecision>().Verify(s => s.GetImportDecisions(It.Is<List<string>>(files => files.Count == 1 && files[0] == firstPath), first, It.Is<DownloadClientItem>(i => i.IsMoviePack), null, true), Times.Once());
            Mocker.GetMock<IMakeImportDecision>().Verify(s => s.GetImportDecisions(It.Is<List<string>>(files => files.Count == 1 && files[0] == secondPath), second, It.Is<DownloadClientItem>(i => i.IsMoviePack), null, true), Times.Once());
            Mocker.GetMock<IImportApprovedMovie>().Verify(s => s.Import(It.IsAny<List<ImportDecision>>(), true, It.IsAny<DownloadClientItem>(), ImportMode.Copy), Times.Exactly(2));
            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
