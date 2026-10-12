using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
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
    public class MoviePackImportFixture : CoreTest<MoviePackImportService>
    {
        private Movie _first;
        private Movie _second;
        private MoviePack _pack;
        private TrackedDownload _download;
        private string _source;
        private string _firstVideo;
        private string _secondVideo;

        [SetUp]
        public void Setup()
        {
            _source = @"C:\Downloads\Movie Pack".AsOsAgnostic();
            _firstVideo = Path.Combine(_source, "First.2000.mkv");
            _secondVideo = Path.Combine(_source, "Second.2001.mkv");
            _first = new Movie { Id = 1, TmdbId = 101, Title = "First", Path = @"C:\Movies\First".AsOsAgnostic(), Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } };
            _second = new Movie { Id = 2, TmdbId = 102, Title = "Second", Path = @"C:\Movies\Second".AsOsAgnostic(), Monitored = true, QualityProfile = new QualityProfile { AllowMoviePacks = true } };
            _pack = new MoviePack { Movies = new List<Movie> { _first, _second }, MovieTmdbIds = new List<int> { 101, 102 } };
            _download = new TrackedDownload { DownloadItem = new DownloadClientItem { OutputPath = new OsPath(_source), CanMoveFiles = true }, RemoteMovie = new RemoteMovie { Movie = _first, MoviePack = _pack } };
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.Refresh(_pack)).Returns(_pack);
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.MatchFile(_firstVideo, _pack)).Returns(_first);
            Mocker.GetMock<IMoviePackResolver>().Setup(s => s.MatchFile(_secondVideo, _pack)).Returns(_second);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_source)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFiles(_source, true)).Returns(new[] { _firstVideo, _secondVideo });
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(_source, true)).Returns(new[] { _firstVideo, _secondVideo });
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(It.IsAny<string>())).Returns(new List<MovieHistory>());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(It.IsAny<int>())).Returns(new List<MovieFile>());
            Mocker.GetMock<IDownloadedMovieImportService>().Setup(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>())).Returns(new List<ImportResult>());
        }

        private void HaveLibraryFile(Movie movie, bool exists = true)
        {
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(movie.Id)).Returns(new List<MovieFile> { new MovieFile { RelativePath = "movie.mkv" } });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(Path.Combine(movie.Path, "movie.mkv"))).Returns(exists);
        }

        [Test]
        public void should_import_each_movie_as_copy_even_when_client_allows_moves()
        {
            Subject.Import(_download);
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(_firstVideo, ImportMode.Copy, _first, It.Is<DownloadClientItem>(i => i.IsMoviePack)), Times.Once());
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(_secondVideo, ImportMode.Copy, _second, It.Is<DownloadClientItem>(i => i.IsMoviePack)), Times.Once());
        }

        [Test]
        public void should_keep_existing_movie_and_import_only_missing_member()
        {
            HaveLibraryFile(_first);
            Subject.Import(_download);
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(_firstVideo, It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(_secondVideo, ImportMode.Copy, _second, It.IsAny<DownloadClientItem>()), Times.Once());
        }

        [Test]
        public void should_not_complete_after_only_one_import()
        {
            HaveLibraryFile(_first);
            Subject.IsComplete(_download).Should().BeFalse();
        }

        [Test]
        public void should_require_real_files_not_just_database_rows()
        {
            HaveLibraryFile(_first);
            HaveLibraryFile(_second, false);
            Subject.IsComplete(_download).Should().BeFalse();
        }

        [Test]
        public void should_complete_when_every_expected_movie_has_a_file()
        {
            HaveLibraryFile(_first);
            HaveLibraryFile(_second);
            Subject.IsComplete(_download).Should().BeTrue();
        }

        [Test]
        public void should_hold_unknown_videos_even_after_expected_movies_are_present()
        {
            HaveLibraryFile(_first);
            HaveLibraryFile(_second);
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(_source, true)).Returns(new[] { _firstVideo, _secondVideo, Path.Combine(_source, "unknown.mkv") });
            Subject.Import(_download).Should().ContainSingle(r => r.Result == ImportResultType.Rejected);
            Subject.IsComplete(_download).Should().BeFalse();
        }

        [Test]
        public void should_hold_archives_for_review()
        {
            HaveLibraryFile(_first);
            HaveLibraryFile(_second);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFiles(_source, true)).Returns(new[] { Path.Combine(_source, "third.rar") });
            Subject.IsComplete(_download).Should().BeFalse();
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void should_respect_profile_and_monitoring_changes(bool monitored, bool enabled)
        {
            _second.Monitored = monitored;
            _second.QualityProfile.AllowMoviePacks = enabled;
            Subject.Import(_download).Should().ContainSingle(r => r.Result == ImportResultType.Rejected);
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(_secondVideo, It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [Test]
        public void should_not_import_or_complete_with_unresolved_membership()
        {
            _pack.Error = "Missing movie metadata";
            Subject.Import(_download).Should().ContainSingle(r => r.Result == ImportResultType.Rejected);
            Subject.IsComplete(_download).Should().BeFalse();
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }
    }
}
