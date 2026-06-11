using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                        typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    }, null);
                _audioFingerprintManager = audioFingerprintManagerConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                    jsonSerializer, serverApplicationHost
                });
                _createTitleFingerprint = audioFingerprintManager.GetMethod("CreateTitleFingerprint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService),
                        typeof(CancellationToken)
                    }, null);
                _getAllFingerprintFilesForSeason = audioFingerprintManager.GetMethod("GetAllFingerprintFilesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _updateSequencesForSeason = audioFingerprintManager.GetMethod("UpdateSequencesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                PatchTimeout(Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null ||
                _getAllFingerprintFilesForSeason is null || _updateSequencesForSeason is null || _timeoutMs is null)
            {
                _logger.Warn($"{nameof(FingerprintApi)} Init Failed");
            }
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            return (Task<Tuple<string, bool>>)_createTitleFingerprint.Invoke(_audioFingerprintManager,
                new object[] { item, libraryOptions, directoryService, cancellationToken });
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            var directoryService = new DirectoryService(_logger, _fileSystem);

            return CreateTitleFingerprint(item, directoryService, cancellationToken);
        }

        private async Task<object> GetAllFingerprintFilesForSeason(Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var task = (Task)_getAllFingerprintFilesForSeason.Invoke(_audioFingerprintManager,
                new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        private void UpdateSequencesForSeason(Season season, object seasonFingerprintInfo, Episode episode,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var parameters = _updateSequencesForSeason.GetParameters();
            var args = parameters.Length >= 6
                ? new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken }
                : new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService };

            _updateSequencesForSeason.Invoke(_audioFingerprintManager, args);
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds = GetValidLibraryIds(Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u,
                    IsFavorite = true,
                    IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                }))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();

            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();

            return result;
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().IntroSkipOptions.LibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;

            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites)
                {
                    resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>()
                        .ToList();
                }

                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();

            var unprocessedItems = FilterUnprocessed(resultItems);

            return unprocessedItems;
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<Episode>();

            foreach (var item in items)
            {
                if (Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (IsExtractNeeded(item))
                {
                    results.Add(item);
                }
            }

            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) &&
                   string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
                HasAudioStream = false,
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>().ToList();

            return items;
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithMarkerDetection = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            f.LibraryOptions.EnableMarkerDetection)
                .ToList();
            var librariesSelected = librariesWithMarkerDetection.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("IntroFingerprintExtract - LibraryScope: " + (!librariesWithMarkerDetection.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var introDetectionFingerprintMinutes =
                Plugin.Instance.GetPluginOptions().IntroSkipOptions.IntroDetectionFingerprintMinutes;
            _logger.Info("Intro Detection Fingerprint Length (Minutes): " + introDetectionFingerprintMinutes);

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };

            if (libraryIds.All(i => i == "-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>().ToList();

            return items;
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();
            
            var currentLength = Plugin.Instance.GetPluginOptions().IntroSkipOptions.IntroDetectionFingerprintMinutes;

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (options.IntroDetectionFingerprintLength != currentLength &&
                    long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = currentLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

#nullable enable
        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            var introDetectionFingerprintMinutes =
                Plugin.Instance.GetPluginOptions().IntroSkipOptions.IntroDetectionFingerprintMinutes;

            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };
            var allEpisodes = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToArray();

            episodeQuery.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var episodesWithoutMarkers = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToList();

            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(season,
                allEpisodes, libraryOptions, directoryService, cancellationToken).ConfigureAwait(false);

            double total = episodesWithoutMarkers.Count;
            var index = 0;

            foreach (var episode in episodesWithoutMarkers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSequencesForSeason(season, seasonFingerprintInfo, episode, libraryOptions, directoryService,
                    cancellationToken);

                index++;
                progress?.Report(index / total);
            }

            progress?.Report(1.0);
        }
#nullable restore
    }
}
