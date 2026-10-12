using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveMovieFiles _movieFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMoveMovieFiles movieFileMover,
                                       IDiskProvider diskProvider,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieFileMover = movieFileMover;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false)
        {
            _logger.Trace("Upgrading movie file.");

            var moveFileResult = new MovieFileMoveResult();

            var existingFile = localMovie.Movie.MovieFileId > 0 ? localMovie.Movie.MovieFile : null;

            if ((localMovie.Movie.QualityProfile?.AllowMoviePacks == true || localMovie.DownloadItem?.IsMoviePack == true) &&
                (existingFile != null || _mediaFileService.GetFilesByMovie(localMovie.Movie.Id).Any()))
            {
                throw new IOException("Existing movie file retained; movie packs do not replace library files");
            }

            var rootFolder = _diskProvider.GetParentFolder(localMovie.Movie.Path);

            // If there are existing movie files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFile != null && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            if (existingFile != null)
            {
                var movieFilePath = Path.Combine(localMovie.Movie.Path, existingFile.RelativePath);
                var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(movieFilePath));
                string recycleBinPath = null;

                if (_diskProvider.FileExists(movieFilePath))
                {
                    _logger.Debug("Removing existing movie file: {0}", existingFile);
                    recycleBinPath = _recycleBinProvider.DeleteFile(movieFilePath, subfolder);
                }
                else
                {
                    _logger.Warn("Existing movie file missing from disk: {0}", movieFilePath);
                }

                moveFileResult.OldFiles.Add(new DeletedMovieFile(existingFile, recycleBinPath));
                _mediaFileService.Delete(existingFile, DeleteMediaFileReason.Upgrade);
            }

            localMovie.OldFiles = moveFileResult.OldFiles;

            if (copyOnly)
            {
                moveFileResult.MovieFile = _movieFileMover.CopyMovieFile(movieFile, localMovie);
            }
            else
            {
                moveFileResult.MovieFile = _movieFileMover.MoveMovieFile(movieFile, localMovie);
            }

            return moveFileResult;
        }
    }
}
