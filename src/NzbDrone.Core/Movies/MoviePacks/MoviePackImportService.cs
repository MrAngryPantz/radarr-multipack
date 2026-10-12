using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies.MoviePacks
{
    public interface IMoviePackImportService
    {
        List<ImportResult> Import(TrackedDownload download);
        bool IsComplete(TrackedDownload download);
    }

    public class MoviePackImportService : IMoviePackImportService
    {
        private readonly IMoviePackResolver _resolver;
        private readonly IMediaFileService _files;
        private readonly IDiskProvider _disk;
        private readonly IDiskScanService _scan;
        private readonly IDownloadedMovieImportService _imports;
        private readonly IHistoryService _history;

        public MoviePackImportService(IMoviePackResolver resolver, IMediaFileService files, IDiskProvider disk, IDiskScanService scan, IDownloadedMovieImportService imports, IHistoryService history)
        {
            _resolver = resolver;
            _files = files;
            _disk = disk;
            _scan = scan;
            _imports = imports;
            _history = history;
        }

        public List<ImportResult> Import(TrackedDownload download)
        {
            var pack = _resolver.Refresh(download.RemoteMovie.MoviePack);
            var results = new List<ImportResult>();
            if (pack.Error != null)
            {
                results.Add(Rejected(null, pack.Error));
                return results;
            }

            var item = download.ImportItem ?? download.DownloadItem;
            item.IsMoviePack = true;
            foreach (var path in SourceVideos(item.OutputPath.FullPath))
            {
                var movie = _resolver.MatchFile(path, pack);
                if (movie == null)
                {
                    results.Add(Rejected(path, "This file does not identify an expected movie; manual review is required"));
                    continue;
                }

                if (HasLibraryFile(movie))
                {
                    continue;
                }

                if (!movie.Monitored || !movie.QualityProfile.AllowMoviePacks || movie.QualityProfile.UpgradeAllowed)
                {
                    results.Add(Rejected(path, $"Movie pack imports are not enabled for {movie.Title}"));
                    continue;
                }

                // Keep all source files until the whole pack has been accounted for.
                // This also applies to Usenet and clients which allow moving files.
                results.AddRange(_imports.ProcessPath(path, ImportMode.Copy, movie, item));
            }

            return results;
        }

        public bool IsComplete(TrackedDownload download)
        {
            var pack = _resolver.Refresh(download.RemoteMovie.MoviePack);
            if (pack.Error != null || pack.Movies.Count < 2 || pack.Movies.Any(m => !HasLibraryFile(m)))
            {
                return false;
            }

            var path = (download.ImportItem ?? download.DownloadItem).OutputPath.FullPath;
            if (path.IsNullOrWhiteSpace())
            {
                return false;
            }

            // A successful import of one file, or old history, is insufficient. Unknown
            // videos and archives keep the download available for the user's review.
            if (_disk.FolderExists(path))
            {
                if (_disk.GetFiles(path, true).Any(f => FileExtensions.ArchiveExtensions.Contains(Path.GetExtension(f))))
                {
                    return false;
                }
            }
            else if (!_disk.FileExists(path))
            {
                return false;
            }

            var imported = _history.FindByDownloadId(download.DownloadItem.DownloadId)
                .Where(h => h.EventType == MovieHistoryEventType.DownloadFolderImported && pack.Movies.Any(m => m.Id == h.MovieId)).ToList();
            return SourceVideos(path).All(file => _resolver.MatchFile(file, pack) != null || imported.Any(h =>
                h.Data.TryGetValue("droppedPath", out var source) && source.PathEquals(file)));
        }

        private bool HasLibraryFile(Movie movie)
        {
            return _files.GetFilesByMovie(movie.Id).Any(f => _disk.FileExists(Path.Combine(movie.Path, f.RelativePath)));
        }

        private IEnumerable<string> SourceVideos(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            if (_disk.FolderExists(path))
            {
                return _scan.GetVideoFiles(path);
            }

            return _disk.FileExists(path) && MediaFileExtensions.Extensions.Contains(Path.GetExtension(path))
                ? new[] { path }
                : Array.Empty<string>();
        }

        private static ImportResult Rejected(string path, string message)
        {
            return new ImportResult(new ImportDecision(new LocalMovie { Path = path }, new ImportRejection(ImportRejectionReason.UnknownMovie, message)), message);
        }
    }
}
