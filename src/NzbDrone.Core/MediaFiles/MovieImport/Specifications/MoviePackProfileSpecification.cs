using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    public class MoviePackProfileSpecification : IImportDecisionEngineSpecification
    {
        private readonly QualityAllowedByProfileSpecification _quality;
        private readonly LanguageSpecification _language;
        private readonly AcceptableSizeSpecification _size;
        private readonly CustomFormatAllowedbyProfileSpecification _formats;

        public MoviePackProfileSpecification(QualityAllowedByProfileSpecification quality, LanguageSpecification language, AcceptableSizeSpecification size, CustomFormatAllowedbyProfileSpecification formats)
        {
            _quality = quality;
            _language = language;
            _size = size;
            _formats = formats;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (downloadClientItem?.IsMoviePack != true || localMovie.ExistingFile)
            {
                return ImportSpecDecision.Accept();
            }

            // A pack's advertised quality and total size cannot describe every file.
            // Check the actual file against its own movie's profile before importing it.
            var remote = new RemoteMovie
            {
                Movie = localMovie.Movie,
                ParsedMovieInfo = new ParsedMovieInfo { Quality = localMovie.Quality, Languages = localMovie.Languages },
                Languages = localMovie.Languages,
                CustomFormats = localMovie.CustomFormats,
                CustomFormatScore = localMovie.CustomFormatScore,
                Release = new ReleaseInfo { Size = localMovie.Size }
            };

            foreach (var spec in new IDownloadDecisionEngineSpecification[] { _quality, _language, _size, _formats })
            {
                var decision = spec.IsSatisfiedBy(remote, null);
                if (!decision.Accepted)
                {
                    return ImportSpecDecision.Reject(ImportRejectionReason.MoviePackProfile, decision.Message);
                }
            }

            return ImportSpecDecision.Accept();
        }
    }
}
