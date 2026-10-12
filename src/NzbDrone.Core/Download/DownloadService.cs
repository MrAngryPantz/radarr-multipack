using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Download
{
    public interface IDownloadService
    {
        Task DownloadReport(RemoteMovie remoteMovie, int? downloadClientId);
    }

    public class DownloadService : IDownloadService
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ISeedConfigProvider _seedConfigProvider;
        private readonly Logger _logger;
        private readonly IMoviePackResolver _moviePacks;
        private readonly IQueueService _queue;
        private static readonly SemaphoreSlim PackGrabLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<int, DateTime> RecentPackGrabs = new Dictionary<int, DateTime>();

        public DownloadService(IProvideDownloadClient downloadClientProvider,
                               IDownloadClientStatusService downloadClientStatusService,
                               IIndexerFactory indexerFactory,
                               IIndexerStatusService indexerStatusService,
                               IRateLimitService rateLimitService,
                               IEventAggregator eventAggregator,
                               ISeedConfigProvider seedConfigProvider,
                               IMoviePackResolver moviePacks,
                               IQueueService queue,
                               Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _downloadClientStatusService = downloadClientStatusService;
            _indexerFactory = indexerFactory;
            _indexerStatusService = indexerStatusService;
            _rateLimitService = rateLimitService;
            _eventAggregator = eventAggregator;
            _seedConfigProvider = seedConfigProvider;
            _logger = logger;
            _moviePacks = moviePacks;
            _queue = queue;
        }

        public async Task DownloadReport(RemoteMovie remoteMovie, int? downloadClientId)
        {
            var filterBlockedClients = remoteMovie.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var tags = remoteMovie.Movie?.Tags;

            var downloadClient = downloadClientId.HasValue
                ? _downloadClientProvider.Get(downloadClientId.Value)
                : _downloadClientProvider.GetDownloadClient(remoteMovie.Release.DownloadProtocol, remoteMovie.Release.IndexerId, filterBlockedClients, tags);

            if (remoteMovie.MoviePack == null)
            {
                await DownloadReport(remoteMovie, downloadClient);
                return;
            }

            await PackGrabLock.WaitAsync();
            try
            {
                var pack = _moviePacks.Refresh(remoteMovie.MoviePack);
                if (pack.Error != null || pack.Movies.Any(m => !m.Monitored || !m.QualityProfile.AllowMoviePacks || m.QualityProfile.UpgradeAllowed) || pack.Movies.All(m => m.HasFile))
                {
                    throw new DownloadClientRejectedReleaseException(remoteMovie.Release, "Movie pack is no longer eligible for download");
                }

                var missing = pack.Movies.Where(m => !m.HasFile).ToList();
                if (missing.Any(m => RecentPackGrabs.TryGetValue(m.Id, out var date) && date > DateTime.UtcNow.AddMinutes(-5)) ||
                    _queue.GetQueue().Any(q => q.RemoteMovie != null && q.RemoteMovie.GetMovies().Any(m => missing.Any(w => w.Id == m.Id))))
                {
                    throw new DownloadClientRejectedReleaseException(remoteMovie.Release, "A movie in this pack is already queued for download");
                }

                if (downloadClient != null && !downloadClientId.HasValue && missing.Any(m =>
                    _downloadClientProvider.GetDownloadClient(remoteMovie.Release.DownloadProtocol, remoteMovie.Release.IndexerId, filterBlockedClients, m.Tags)?.Definition.Id != downloadClient.Definition.Id))
                {
                    throw new DownloadClientRejectedReleaseException(remoteMovie.Release, "Movies in this pack use different download clients");
                }

                remoteMovie.Movie = missing.First();
                await DownloadReport(remoteMovie, downloadClient);
                foreach (var id in RecentPackGrabs.Where(kv => kv.Value <= DateTime.UtcNow.AddMinutes(-5)).Select(kv => kv.Key).ToList())
                {
                    RecentPackGrabs.Remove(id);
                }

                foreach (var movie in missing)
                {
                    RecentPackGrabs[movie.Id] = DateTime.UtcNow;
                }
            }
            finally
            {
                PackGrabLock.Release();
            }
        }

        private async Task DownloadReport(RemoteMovie remoteMovie, IDownloadClient downloadClient)
        {
            Ensure.That(remoteMovie.Movie, () => remoteMovie.Movie).IsNotNull();

            var downloadTitle = remoteMovie.Release.Title;

            if (downloadClient == null)
            {
                throw new DownloadClientUnavailableException($"{remoteMovie.Release.DownloadProtocol} Download client isn't configured yet");
            }

            // Get the seed configuration for this release.
            remoteMovie.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteMovie);

            // Limit grabs to 2 per second.
            if (remoteMovie.Release.DownloadUrl.IsNotNullOrWhiteSpace() && !remoteMovie.Release.DownloadUrl.StartsWith("magnet:"))
            {
                var url = new HttpUri(remoteMovie.Release.DownloadUrl);
                await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            }

            IIndexer indexer = null;

            if (remoteMovie.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteMovie.Release.IndexerId));
            }

            string downloadClientId;
            try
            {
                downloadClientId = await downloadClient.Download(remoteMovie, indexer);
                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                _indexerStatusService.RecordSuccess(remoteMovie.Release.IndexerId);
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Trace("Release {0} no longer available on indexer.", remoteMovie);
                throw;
            }
            catch (ReleaseBlockedException)
            {
                _logger.Trace("Release {0} previously added to blocklist, not sending to download client again.", remoteMovie);
                throw;
            }
            catch (DownloadClientRejectedReleaseException)
            {
                _logger.Trace("Release {0} rejected by download client, possible duplicate.", remoteMovie);
                throw;
            }
            catch (ReleaseDownloadException ex)
            {
                if (ex.InnerException is TooManyRequestsException http429)
                {
                    _indexerStatusService.RecordFailure(remoteMovie.Release.IndexerId, http429.RetryAfter);
                }
                else
                {
                    _indexerStatusService.RecordFailure(remoteMovie.Release.IndexerId);
                }

                throw;
            }

            foreach (var movie in remoteMovie.GetMovies())
            {
                var movieGrabbedEvent = new MovieGrabbedEvent(remoteMovie.ForMovie(movie))
                {
                    DownloadClient = downloadClient.Name,
                    DownloadClientId = downloadClient.Definition.Id,
                    DownloadClientName = downloadClient.Definition.Name,
                    DownloadId = downloadClientId.IsNotNullOrWhiteSpace() ? downloadClientId : null
                };

                _eventAggregator.PublishEvent(movieGrabbedEvent);
            }

            _logger.ProgressInfo("Report for {0} ({1}) sent to {2} from indexer {3}. {4}", remoteMovie.Movie.Title, remoteMovie.Movie.Year, downloadClient.Definition.Name, remoteMovie.Release.Indexer, downloadTitle);
        }
    }
}
