using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDownloadDecisionEngineSpecification> _specifications;
        private readonly IParsingService _parsingService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly IMoviePackResolver _moviePacks;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDownloadDecisionEngineSpecification> specifications,
                                     IParsingService parsingService,
                                     ICustomFormatCalculationService formatCalculator,
                                     IRemoteMovieAggregationService aggregationService,
                                     IMoviePackResolver moviePacks,
                                     Logger logger)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _formatCalculator = formatCalculator;
            _aggregationService = aggregationService;
            _moviePacks = moviePacks;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetDecisions(reports, pushedRelease).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetDecisions(List<ReleaseInfo> reports, bool pushedRelease = false, SearchCriteriaBase searchCriteria = null)
        {
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            var reportNumber = 1;

            foreach (var report in reports)
            {
                DownloadDecision decision = null;
                _logger.ProgressTrace("Processing release {0}/{1}", reportNumber, reports.Count);
                _logger.Debug("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    var parsedMovieInfo = Parser.Parser.ParseMovieTitle(report.Title);
                    var pack = report.MoviePackTmdbIds != null
                        ? _moviePacks.Refresh(new MoviePack { MovieTmdbIds = report.MoviePackTmdbIds })
                        : _moviePacks.Resolve(report.Title);
                    if (pack != null)
                    {
                        decision = GetPackDecision(report, pack, parsedMovieInfo, pushedRelease, searchCriteria);
                    }

                    if (decision == null && parsedMovieInfo != null && !parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                    {
                        var remoteMovie = _parsingService.Map(parsedMovieInfo, report.ImdbId.ToString(), report.TmdbId, searchCriteria);
                        remoteMovie.Release = report;
                        remoteMovie.ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria);

                        if (remoteMovie.Movie == null)
                        {
                            decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnknownMovie, pushedRelease ? "Unknown Movie. Unable to match to existing movie in Library using release title." : "Unknown Movie. Unable to match to correct movie using release title."));
                        }
                        else
                        {
                            _aggregationService.Augment(remoteMovie);

                            remoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(remoteMovie, remoteMovie.Release.Size);
                            remoteMovie.CustomFormatScore = remoteMovie?.Movie?.QualityProfile?.CalculateCustomFormatScore(remoteMovie.CustomFormats) ?? 0;

                            _logger.Trace("Custom Format Score of '{0}' [{1}] calculated for '{2}'", remoteMovie.CustomFormatScore, remoteMovie.CustomFormats?.ConcatToString(), report.Title);

                            remoteMovie.DownloadAllowed = remoteMovie.Movie != null;
                            decision = GetDecisionForReport(remoteMovie, new ReleaseDecisionInformation(pushedRelease, searchCriteria));
                        }
                    }

                    if (decision == null && searchCriteria != null)
                    {
                        if (parsedMovieInfo == null)
                        {
                            parsedMovieInfo = new ParsedMovieInfo
                            {
                                Languages = LanguageParser.ParseLanguages(report.Title),
                                Quality = QualityParser.ParseQuality(report.Title)
                            };
                        }

                        if (parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                        {
                            var remoteMovie = new RemoteMovie
                            {
                                Release = report,
                                ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria),
                                ParsedMovieInfo = parsedMovieInfo,
                                Languages = parsedMovieInfo.Languages,
                            };

                            decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnableToParse, "Unable to parse release"));
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't process release.");

                    var remoteMovie = new RemoteMovie { Release = report, ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria) };
                    decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.Error, "Unexpected error processing release"));
                }

                reportNumber++;

                if (decision != null)
                {
                    if (decision.Rejections.Any())
                    {
                        _logger.Debug("Release '{0}' from '{1}' rejected for the following reasons: {2}", report.Title, report.Indexer, string.Join(", ", decision.Rejections));
                    }
                    else
                    {
                        _logger.Debug("Release '{0}' from '{1}' accepted", report.Title, report.Indexer);
                    }

                    yield return decision;
                }
            }
        }

        private DownloadDecision GetPackDecision(ReleaseInfo report, MoviePack pack, ParsedMovieInfo parsed, bool pushedRelease, SearchCriteriaBase searchCriteria)
        {
            var remote = new RemoteMovie
            {
                Release = report,
                MoviePack = pack,
                Movie = pack.Movies.FirstOrDefault(m => !m.HasFile) ?? pack.Movies.FirstOrDefault() ?? searchCriteria?.Movie,
                MovieMatchType = MovieMatchType.Title,
                ParsedMovieInfo = parsed ?? new ParsedMovieInfo
                {
                    ReleaseTitle = report.Title,
                    Quality = QualityParser.ParseQuality(report.Title),
                    Languages = LanguageParser.ParseLanguages(report.Title)
                },
                ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria)
            };
            remote.Languages = remote.ParsedMovieInfo.Languages;

            if (pack.Error.IsNotNullOrWhiteSpace() || pack.Movies.Count < 2 || pack.MovieTmdbIds.Count != pack.Movies.Count)
            {
                return new DownloadDecision(remote, new DownloadRejection(DownloadRejectionReason.MoviePackUnresolved, pack.Error ?? "Unable to identify every movie in the pack"));
            }

            if (pack.Movies.Any(m => m.QualityProfile?.AllowMoviePacks != true || m.QualityProfile.UpgradeAllowed))
            {
                return new DownloadDecision(remote, new DownloadRejection(DownloadRejectionReason.MoviePackNotAllowed, "Every movie in the pack must allow movie packs with upgrades disabled"));
            }

            if (pack.Movies.Any(m => !m.Monitored))
            {
                return new DownloadDecision(remote, new DownloadRejection(DownloadRejectionReason.MovieNotMonitored, "Every movie in the pack must be monitored"));
            }

            if (searchCriteria != null && !pack.Movies.Any(m => m.Id == searchCriteria.Movie.Id))
            {
                return new DownloadDecision(remote, new DownloadRejection(DownloadRejectionReason.WrongMovie, "The pack does not contain the requested movie"));
            }

            var missing = pack.Movies.Where(m => !m.HasFile).ToList();
            if (missing.Count == 0)
            {
                return new DownloadDecision(remote, new DownloadRejection(DownloadRejectionReason.MoviePackComplete, "All movies in the pack already have files"));
            }

            report.MoviePackTmdbIds = pack.MovieTmdbIds.ToList();
            remote.MovieRequested = searchCriteria?.Movie != null && pack.Movies.Any(m => m.Id == searchCriteria.Movie.Id);
            var rejections = new List<DownloadRejection>();
            foreach (var movie in missing)
            {
                var member = remote.ForMovie(movie);
                _aggregationService.Augment(member);
                member.CustomFormats = _formatCalculator.ParseCustomFormat(member, report.Size);
                member.CustomFormatScore = movie.QualityProfile.CalculateCustomFormatScore(member.CustomFormats);
                member.DownloadAllowed = true;

                var decision = GetDecisionForReport(member, new ReleaseDecisionInformation(pushedRelease, searchCriteria));
                rejections.AddRange(decision.Rejections.Select(r => new DownloadRejection(r.Reason, $"{movie.Title}: {r.Message}", r.Type)));

                if (movie.Id == remote.Movie.Id)
                {
                    remote = member;
                }
            }

            return new DownloadDecision(remote, rejections.ToArray());
        }

        private DownloadDecision GetDecisionForReport(RemoteMovie remoteMovie, ReleaseDecisionInformation information)
        {
            var reasons = Array.Empty<DownloadRejection>();

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteMovie, information))
                                        .Where(c => c != null)
                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteMovie, reasons.ToArray());
        }

        private DownloadRejection EvaluateSpec(IDownloadDecisionEngineSpecification spec, RemoteMovie remoteMovie, ReleaseDecisionInformation information)
        {
            try
            {
                var result = spec.IsSatisfiedBy(remoteMovie, information);

                if (!result.Accepted)
                {
                    return new DownloadRejection(result.Reason, result.Message, spec.Type);
                }
            }
            catch (NotImplementedException)
            {
                _logger.Trace("Spec " + spec.GetType().Name + " does not care about movies.");
            }
            catch (Exception e)
            {
                e.Data.Add("report", remoteMovie.Release.ToJson());
                e.Data.Add("parsed", remoteMovie.ParsedMovieInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}, with spec: {1}", remoteMovie.Release.Title, spec.GetType().Name);
                return new DownloadRejection(DownloadRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private ReleaseSourceType GetReleaseSource(bool pushedRelease, SearchCriteriaBase searchCriteria = null)
        {
            if (searchCriteria == null)
            {
                return pushedRelease ? ReleaseSourceType.ReleasePush : ReleaseSourceType.Rss;
            }

            if (searchCriteria.InteractiveSearch)
            {
                return ReleaseSourceType.InteractiveSearch;
            }
            else if (searchCriteria.UserInvokedSearch)
            {
                return ReleaseSourceType.UserInvokedSearch;
            }
            else
            {
                return ReleaseSourceType.Search;
            }
        }
    }
}
