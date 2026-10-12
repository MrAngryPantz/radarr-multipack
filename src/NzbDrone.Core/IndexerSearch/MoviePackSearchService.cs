using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.IndexerSearch
{
    public interface IMoviePackSearchService
    {
        Task<IList<ReleaseInfo>> Search(IIndexer indexer, MovieSearchCriteria criteria);
    }

    public class MoviePackSearchService : IMoviePackSearchService,
        IHandle<ProviderUpdatedEvent<IIndexer>>, IHandle<ProviderDeletedEvent<IIndexer>>
    {
        private readonly ICached<Lazy<Task<IList<ReleaseInfo>>>> _cache;
        private readonly IIndexerStatusService _status;
        private readonly IMoviePackResolver _resolver;

        public MoviePackSearchService(ICacheManager cacheManager, IIndexerStatusService status, IMoviePackResolver resolver)
        {
            _cache = cacheManager.GetCache<Lazy<Task<IList<ReleaseInfo>>>>(GetType());
            _status = status;
            _resolver = resolver;
        }

        public async Task<IList<ReleaseInfo>> Search(IIndexer indexer, MovieSearchCriteria criteria)
        {
            if (!indexer.SupportsSearch || _status.GetBlockedProviders().Any(i => i.ProviderId == indexer.Definition.Id))
            {
                return Array.Empty<ReleaseInfo>();
            }

            var search = new MovieSearchCriteria
            {
                Movie = criteria.Movie,
                MoviePackSearch = true,
                SceneTitles = _resolver.GetSearchTitles(criteria.Movie).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList(),
                UserInvokedSearch = criteria.UserInvokedSearch,
                InteractiveSearch = criteria.InteractiveSearch
            };
            var key = new { IndexerId = indexer.Definition.Id, search.SceneTitles }.ToJson();
            try
            {
                var releases = await _cache.Get(key, () => new Lazy<Task<IList<ReleaseInfo>>>(() => indexer.Fetch(search)), TimeSpan.FromMinutes(1)).Value;

                // Resolve against fresh library metadata, even when sharing a cached query.
                return releases.Where(r => _resolver.Resolve(r.Title) != null).ToList();
            }
            catch
            {
                _cache.Remove(key);
                throw;
            }
        }

        public void Handle(ProviderUpdatedEvent<IIndexer> message)
        {
            _cache.Clear();
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            _cache.Clear();
        }
    }
}
