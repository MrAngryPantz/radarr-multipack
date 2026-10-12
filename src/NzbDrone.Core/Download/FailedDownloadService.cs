using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.MoviePacks;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IFailedDownloadService
    {
        void MarkAsFailed(int historyId, bool skipRedownload = false);
        void MarkAsFailed(TrackedDownload trackedDownload, bool skipRedownload = false);
        void Check(TrackedDownload trackedDownload);
        void ProcessFailed(TrackedDownload trackedDownload);
    }

    public class FailedDownloadService : IFailedDownloadService
    {
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;

        public FailedDownloadService(IHistoryService historyService,
                                     IEventAggregator eventAggregator)
        {
            _historyService = historyService;
            _eventAggregator = eventAggregator;
        }

        public void MarkAsFailed(int historyId, bool skipRedownload = false)
        {
            var history = _historyService.Get(historyId);

            var downloadId = history.DownloadId;

            if (downloadId.IsNullOrWhiteSpace())
            {
                PublishDownloadFailedEvent(history, "Manually marked as failed", skipRedownload: skipRedownload);

                return;
            }

            var histories = history.Data.ContainsKey(MoviePack.HistoryKey) ? GetGrabbedHistory(downloadId) : new List<MovieHistory> { history };
            PublishFailures(histories, "Manually marked as failed", null, skipRedownload);
        }

        public void MarkAsFailed(TrackedDownload trackedDownload, bool skipRedownload = false)
        {
            var history = GetGrabbedHistory(trackedDownload.DownloadItem.DownloadId);

            if (history.Any())
            {
                PublishFailures(trackedDownload.RemoteMovie?.MoviePack != null ? history : history.Take(1).ToList(), "Manually marked as failed", trackedDownload, skipRedownload);
            }
        }

        public void Check(TrackedDownload trackedDownload)
        {
            // Only process tracked downloads that are still downloading or import is blocked (if they fail after attempting to be processed)
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            if (trackedDownload.DownloadItem.IsEncrypted ||
                trackedDownload.DownloadItem.Status == DownloadItemStatus.Failed)
            {
                var grabbedItems = GetGrabbedHistory(trackedDownload.DownloadItem.DownloadId);

                if (grabbedItems.Empty())
                {
                    trackedDownload.Warn(trackedDownload.DownloadItem.IsEncrypted ? "Download is encrypted and wasn't grabbed by Radarr, skipping automatic download handling" : "Download has failed wasn't grabbed by Radarr, skipping automatic download handling");
                    return;
                }

                trackedDownload.State = TrackedDownloadState.FailedPending;
            }
        }

        public void ProcessFailed(TrackedDownload trackedDownload)
        {
            if (trackedDownload.State != TrackedDownloadState.FailedPending)
            {
                return;
            }

            var grabbedItems = GetGrabbedHistory(trackedDownload.DownloadItem.DownloadId);

            if (grabbedItems.Empty())
            {
                return;
            }

            var failure = "Failed download detected";

            if (trackedDownload.DownloadItem.IsEncrypted)
            {
                failure = "Encrypted download detected";
            }
            else if (trackedDownload.DownloadItem.Status == DownloadItemStatus.Failed && trackedDownload.DownloadItem.Message.IsNotNullOrWhiteSpace())
            {
                failure = trackedDownload.DownloadItem.Message;
            }

            trackedDownload.State = TrackedDownloadState.Failed;
            PublishFailures(trackedDownload.RemoteMovie?.MoviePack != null ? grabbedItems : grabbedItems.Take(1).ToList(), failure, trackedDownload, false);
        }

        private void PublishFailures(List<MovieHistory> histories, string message, TrackedDownload trackedDownload, bool skipRedownload)
        {
            var members = histories.DistinctBy(h => h.MovieId).ToList();
            var ids = members.Count > 1 ? members.Select(h => h.MovieId).ToList() : null;
            for (var index = 0; index < members.Count; index++)
            {
                // Blocklist every member before a single replacement search can run.
                PublishDownloadFailedEvent(members[index], message, trackedDownload, skipRedownload || index < members.Count - 1, ids);
            }
        }

        private void PublishDownloadFailedEvent(MovieHistory historyItem, string message, TrackedDownload trackedDownload = null, bool skipRedownload = false, List<int> moviePackMovieIds = null)
        {
            Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

            var downloadFailedEvent = new DownloadFailedEvent
            {
                MovieId = historyItem.MovieId,
                MoviePackMovieIds = moviePackMovieIds,
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                DownloadClient = historyItem.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT),
                DownloadId = historyItem.DownloadId,
                Message = message,
                Data = historyItem.Data,
                TrackedDownload = trackedDownload,
                Languages = historyItem.Languages,
                SkipRedownload = skipRedownload,
                ReleaseSource = releaseSource,
            };

            _eventAggregator.PublishEvent(downloadFailedEvent);
        }

        private List<MovieHistory> GetGrabbedHistory(string downloadId)
        {
            // Sort by date so items are always in the same order
            return _historyService.Find(downloadId, MovieHistoryEventType.Grabbed)
                .OrderByDescending(h => h.Date)
                .ToList();
        }
    }
}
