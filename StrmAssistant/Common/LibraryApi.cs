using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.MetadataEnhanceOptions;
using static StrmAssistant.Options.Utility;
using CollectionExtensions = System.Collections.Generic.CollectionExtensions;

namespace StrmAssistant.Common
{
    public class LibraryApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaMountManager _mediaMountManager;
        private readonly IUserManager _userManager;

        public static ExtraType[] IncludeExtraTypes =
        {
            ExtraType.AdditionalPart, ExtraType.BehindTheScenes, ExtraType.Clip, ExtraType.DeletedScene,
            ExtraType.Interview, ExtraType.Sample, ExtraType.Scene, ExtraType.ThemeSong, ExtraType.ThemeVideo,
            ExtraType.Trailer
        };

        public static MediaContainers[] ExcludeMediaContainers
        {
            get
            {
                return Plugin.Instance.GetPluginOptions()
                    .MediaInfoExtractOptions.ImageCaptureExcludeMediaContainers
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c =>
                        Enum.TryParse<MediaContainers>(c.Trim(), true, out var container)
                            ? container
                            : (MediaContainers?)null)
                    .Where(container => container.HasValue)
                    .Select(container => container.Value)
                    .Concat(new[] { MediaContainers.Iso })
                    .Distinct()
                    .ToArray();
            }
        }

        public static string[] ExcludeMediaExtensions
        {
            get
            {
                return Plugin.Instance.GetPluginOptions()
                    .MediaInfoExtractOptions.ImageCaptureExcludeMediaContainers
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(c =>
                    {
                        if (Enum.TryParse<MediaContainers>(c.Trim(), true, out var container))
                        {
                            var aliases = container.GetAliases();
                            return aliases?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? Array.Empty<string>();
                        }

                        return Array.Empty<string>();
                    })
                    .Concat(MediaContainers.Iso.GetAliases())
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct()
                    .ToArray();
            }
        }

        public static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        public static List<string> LibraryPathsInScope;
        public static Dictionary<User, bool> AllUsers = new Dictionary<User, bool>();
        public static string[] AdminOrderedViews = Array.Empty<string>();

        public LibraryApi(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem,
            IMediaMountManager mediaMountManager, IUserManager userManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _mediaMountManager = mediaMountManager;
            _userManager = userManager;

            UpdateLibraryPathsInScope();
            FetchUsers();
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds =
                GetValidLibraryIds(Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !ExcludedCollectionTypes.Contains(f.CollectionType) &&
                            (!validLibraryIds.Any() || validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void FetchUsers()
        {
            var userQuery = new UserQuery
            {
                IsDisabled = false,
            };
            var allUsers = _userManager.GetUserList(userQuery);

            foreach (var user in allUsers)
            {
                AllUsers[user] = _userManager.GetUserById(user.InternalId).Policy.IsAdministrator;
            }

            FetchAdminOrderedViews();
        }

        public void FetchAdminOrderedViews()
        {
            var firstAdmin = AllUsers.Where(kvp => kvp.Value).Select(u => u.Key).OrderBy(u => u.DateCreated)
                .FirstOrDefault();
            AdminOrderedViews = firstAdmin?.Configuration.OrderedViews ?? AdminOrderedViews;
        }

        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue) return false;

            if (item.Size == 0) return false;

            return item.GetMediaStreams().Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio);
        }

        public bool ImageCaptureEnabled(BaseItem item, LibraryOptions libraryOptions)
        {
            var typeName = item.ExtraType == null ? item.GetType().Name : item.DisplayParent.GetType().Name;
            var typeOptions = libraryOptions.GetTypeOptions(typeName);

            return typeOptions?.ImageFetchers?.Any(f => f == "Image Capture" || f == "Embedded Images") is true;
        }

        private List<VirtualFolderInfo> GetLibrariesWithImageCapture(List<VirtualFolderInfo> libraries)
        {
            var librariesWithImageCapture = libraries.Where(l => l.LibraryOptions.TypeOptions.Any(t =>
                    (t.ImageFetchers.Contains("Image Capture") &&
                     ((l.CollectionType == CollectionType.TvShows.ToString() && t.Type == nameof(Episode)) ||
                      (l.CollectionType == CollectionType.Movies.ToString() && t.Type == nameof(Movie)) ||
                      (l.CollectionType == CollectionType.HomeVideos.ToString() && t.Type == nameof(Video)) ||
                      (l.CollectionType == CollectionType.Music.ToString() && t.Type == nameof(MusicVideo)) ||
                      (l.CollectionType == CollectionType.MusicVideos.ToString() && t.Type == nameof(MusicVideo)) ||
                      (l.CollectionType is null && (t.Type == nameof(Episode) || t.Type == nameof(Movie))))) ||
                    (t.ImageFetchers.Contains("Embedded Images") &&
                     ((l.CollectionType == CollectionType.Music.ToString() && t.Type == nameof(Audio)) ||
                      (l.CollectionType == CollectionType.AudioBooks.ToString() && t.Type == nameof(Audio))))))
                .ToList();

            return librariesWithImageCapture;
        }

        public List<BaseItem> FetchExtractQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds == null || !libraryIds.Any() || libraryIds.Contains("-1");
            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var resultItems = new List<BaseItem>();

            if (IsCatchupTaskSelected(CatchupTask.MediaInfo))
            {
                if (includeFavorites) resultItems = ExpandFavorites(items, true, true, enableImageCapture);

                var incomingItems = items.Where(item => item is Video || item is Audio).ToList();

                if (libraryIds == null || !libraryIds.Any())
                {
                    resultItems = resultItems.Concat(incomingItems).ToList();
                }

                if (libraryIds != null && libraryIds.Any(id => id != "-1") && LibraryPathsInScope.Any())
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            if (IsCatchupTaskSelected(CatchupTask.IntroSkip))
            {
                var episodesIntroSkip = Plugin.ChapterApi.SeasonHasIntroCredits(items.OfType<Episode>().ToList());
                resultItems = resultItems.Concat(episodesIntroSkip).ToList();
            }

            resultItems = resultItems.Where(i => i.ExtraType is null).GroupBy(i => i.InternalId).Select(g => g.First())
                .ToList();

            var unprocessedItems = FilterUnprocessed(resultItems
                .Concat(includeExtra ? resultItems.SelectMany(f => f.GetExtras(IncludeExtraTypes)) : Enumerable.Empty<BaseItem>())
                .ToList());
            var orderedItems = OrderByDescending(unprocessedItems);

            return orderedItems;
        }

        public List<BaseItem> FetchPreExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id)).ToList();
            var librariesWithImageCapture = GetLibrariesWithImageCapture(libraries);

            _logger.Info("MediaInfoExtract - LibraryScope: " + (!libraryIds.Any()
                ? "ALL"
                : string.Join(", ",
                    libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(libraries.Select(l => l.Name))
                        : libraries.Select(l => l.Name).DefaultIfEmpty("NONE"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var favoritesWithExtra = Array.Empty<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = ExpandFavorites(favorites, false, true, enableImageCapture);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = false,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsMediaInfoQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations).Select(ls =>
                        ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                            ? ls
                            : ls + Path.DirectorySeparatorChar).ToArray();
                }

                var itemsMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                var itemsImageCaptureQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (enableImageCapture && librariesWithImageCapture.Any())
                {
                    itemsImageCaptureQuery.PathStartsWithAny =
                        librariesWithImageCapture.SelectMany(l => l.Locations).Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                                ? ls
                                : ls + Path.DirectorySeparatorChar).ToArray();

                    var itemsImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    items = itemsMediaInfo.Concat(itemsImageCapture).GroupBy(i => i.InternalId).Select(g => g.First())
                        .ToArray();
                }
                else
                {
                    items = itemsMediaInfo;
                }

                if (includeExtra)
                {
                    itemsMediaInfoQuery.ExtraTypes = IncludeExtraTypes;
                    var extrasMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                    if (enableImageCapture && librariesWithImageCapture.Any())
                    {
                        itemsImageCaptureQuery.ExtraTypes = IncludeExtraTypes;
                        var extrasImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery);
                        extras = extrasImageCapture.Concat(extrasMediaInfo).GroupBy(i => i.InternalId)
                            .Select(g => g.First()).ToArray();
                    }
                    else
                    {
                        extras = extrasMediaInfo;
                    }
                }
            }

            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();
            var filtered = FilterUnprocessed(combined);
            var results = OrderByDescending(filtered);

            return results;
        }

        public List<BaseItem> FetchPostExtractTaskItems(bool includeAudio)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .MediaInfoExtractOptions.LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id))
                .ToList();

            _logger.Info("MediaInfoExtract - LibraryScope: " + (!libraryIds.Any()
                ? "ALL"
                : string.Join(", ",
                    libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(libraries.Select(l => l.Name))
                        : libraries.Select(l => l.Name).DefaultIfEmpty("NONE"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var favoritesWithExtra = new List<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user, IsFavorite = true
                    }))
                    .GroupBy(i => i.InternalId)
                    .Select(g => g.First())
                    .ToList();

                var expanded = ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(HasMediaInfo)
                    .ToList();
            }

            var itemsWithExtras = new List<BaseItem>();
            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsQuery = new InternalItemsQuery
                {
                    HasPath = true, HasAudioStream = true,
                    MediaTypes = includeAudio ? new[] { MediaType.Video, MediaType.Audio } : new[] { MediaType.Video }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations)
                        .Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                        .ToArray();
                }

                itemsWithExtras = _libraryManager.GetItemList(itemsQuery).ToList();

                if (includeExtra)
                {
                    itemsQuery.ExtraTypes = IncludeExtraTypes;
                    itemsWithExtras = _libraryManager.GetItemList(itemsQuery).Concat(itemsWithExtras).ToList();
                }
            }

            var combined = favoritesWithExtra.Concat(itemsWithExtras)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();
            var results = OrderByDescending(combined);

            return results;
        }

        public List<BaseItem> FetchMissingStrmMediaInfoJsonItems(IDirectoryService directoryService)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .MediaInfoExtractOptions.LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id))
                .ToList();

            _logger.Info("MediaInfoJsonGapCheck - LibraryScope: " + (!libraryIds.Any()
                ? "ALL"
                : string.Join(", ",
                    libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(libraries.Select(l => l.Name))
                        : libraries.Select(l => l.Name).DefaultIfEmpty("NONE"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var favoritesWithExtra = new List<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true
                    }))
                    .GroupBy(i => i.InternalId)
                    .Select(g => g.First())
                    .ToList();

                var expanded = ExpandFavorites(favorites, false, false, false);
                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .ToList();
            }

            var itemsWithExtras = new List<BaseItem>();
            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations)
                        .Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                        .ToArray();
                }

                itemsWithExtras = _libraryManager.GetItemList(itemsQuery).ToList();

                if (includeExtra)
                {
                    itemsQuery.ExtraTypes = IncludeExtraTypes;
                    itemsWithExtras = _libraryManager.GetItemList(itemsQuery).Concat(itemsWithExtras).ToList();
                }
            }

            var results = favoritesWithExtra.Concat(itemsWithExtras)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .Where(i => i.IsShortcut || IsFileShortcut(i.Path))
                .Where(i => !Plugin.MediaInfoApi.MediaInfoJsonExists(i, directoryService))
                .ToList();

            results = OrderByDescending(results);

            _logger.Info("MediaInfoJsonGapCheck - Number of missing STRM media info json items: " + results.Count);

            return results;
        }

        public async Task<bool> EnsureMediaInfoJsonAsync(BaseItem taskItem, IDirectoryService directoryService,
            string source, CancellationToken cancellationToken)
        {
            if (Plugin.MediaInfoApi.MediaInfoJsonExists(taskItem, directoryService)) return false;

            if (!HasMediaInfo(taskItem))
            {
                var extractResult = await OrchestrateMediaInfoProcessAsync(taskItem, source, cancellationToken, true, true)
                    .ConfigureAwait(false);

                if (extractResult is null) return false;

                if (Plugin.MediaInfoApi.MediaInfoJsonExists(taskItem, directoryService)) return true;
            }

            return await Plugin.MediaInfoApi.SerializeMediaInfo(taskItem.InternalId, directoryService, false, source)
                .ConfigureAwait(false);
        }

        private static List<BaseItem> OrderByDescending(List<BaseItem> items)
        {
            var results = items.OrderBy(i => i.ExtraType is null ? 0 : 1)
                .ThenByDescending(i =>
                    i is Episode e ? GetPremiereDateOrDefault(e) :
                    i.ExtraType != null ? i.DateCreated : i.PremiereDate ?? i.DateCreated)
                .ThenByDescending(i => i.IndexNumber ?? int.MinValue)
                .ToList();
            return results;
        }

        private static DateTimeOffset GetPremiereDateOrDefault(Episode item)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);
            }

            return item.DateCreated;
        }

        public static bool IsPremiereDateInScope(Episode item, DateTimeOffset lookBackTime, bool includeNoPrem)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value > lookBackTime;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value > lookBackTime;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return year == lookBackTime.Year;
            }

            return includeNoPrem;
        }

        private List<BaseItem> FilterUnprocessed(List<BaseItem> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<BaseItem>();

            foreach (var item in items)
            {
                if (IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (item is Video)
                {
                    _logger.Debug("MediaInfoExtract - Item dropped: " + item.Name + " - " + item.Path); // video without audio
                }
            }

            _logger.Info("MediaInfoExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item, bool enableImageCapture)
        {
            if (item.MediaContainer.HasValue && ExcludeMediaContainers.Contains(item.MediaContainer.Value))
                return false;

            if (!item.IsShortcut && item.IsFileProtocol && !string.IsNullOrEmpty(item.Path))
            {
                var fileExtension = Path.GetExtension(item.Path).TrimStart('.');
                if (ExcludeMediaExtensions.Contains(fileExtension)) return false;
            }

            if (!HasMediaInfo(item)) return true;

            if (!enableImageCapture) return false;

            if (Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode ==
                PersistMediaInfoOption.Restore.ToString()) return false;

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (item)
            {
                case Video _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item, libraryOptions);
                case Audio _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item, libraryOptions) &&
                           item.GetMediaStreams().Any(i => i.Type == MediaStreamType.EmbeddedImage);
                default:
                    return false;
            }
        }

        public List<BaseItem> ExpandFavorites(List<BaseItem> items, bool filterNeeded, bool? preExtract,
            bool enableImageCapture)
        {
            var itemsMultiVersions = items.SelectMany(v =>
                    _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            PresentationUniqueKey = v.PresentationUniqueKey
                        }))
                .ToList();

            var videos = itemsMultiVersions.OfType<Video>().Cast<BaseItem>().ToList();

            var seriesIds = itemsMultiVersions.OfType<Series>().Select(s => s.InternalId)
                .Union(itemsMultiVersions.OfType<Episode>().Select(e => e.SeriesId)).ToArray();

            var episodes = Array.Empty<BaseItem>();
            if (seriesIds.Length > 0)
            {
                var episodesMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = !preExtract,
                    MediaTypes = new[] { MediaType.Video },
                    Recursive = true,
                    AncestorIds = seriesIds
                };
                var episodesMediaInfo = _libraryManager.GetItemList(episodesMediaInfoQuery);

                if (enableImageCapture)
                {
                    var episodesImageCaptureQuery = new InternalItemsQuery
                    {
                        HasPath = true,
                        MediaTypes = new[] { MediaType.Video },
                        Recursive = true,
                        AncestorIds = seriesIds
                    };
                    var episodesImageCapture = _libraryManager.GetItemList(episodesImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    episodes = episodesMediaInfo.Concat(episodesImageCapture).GroupBy(i => i.InternalId)
                        .Select(g => g.First()).ToArray();
                }
                else
                {
                    episodes = episodesMediaInfo;
                }
            }

            var combined = videos.Concat(episodes).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

            return filterNeeded ? FilterByFavorites(combined) : combined;
        }

        private List<BaseItem> FilterByFavorites(List<BaseItem> items)
        {
            var videos = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Video>().Where(i => i.IsFavoriteOrLiked(u)));

            var episodes = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Episode>()
                    .GroupBy(e => e.SeriesId)
                    .Where(g => g.Any(i => i.IsFavoriteOrLiked(u)) || g.First().Series.IsFavoriteOrLiked(u))
                    .SelectMany(g => g));

            var results = videos.Cast<BaseItem>()
                .Concat(episodes)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            return results;
        }

        public List<User> GetUsersByFavorites(BaseItem item)
        {
            var itemsMultiVersion = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = item.PresentationUniqueKey
            });

            var users = AllUsers.Select(e => e.Key)
                .Where(u => itemsMultiVersion.Any(i =>
                    (i is Movie || i is Series) && i.IsFavoriteOrLiked(u) || i is Episode e &&
                    (e.IsFavoriteOrLiked(u) || e.Series != null && e.Series.IsFavoriteOrLiked(u))))
                .ToList();

            return users;
        }

        public bool HasFileChanged(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && item.HasDateModifiedChanged(file.LastWriteTimeUtc))
                    return true;
            }

            return false;
        }

        public void UpdateDateModifiedLastSaved(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && file.LastWriteTimeUtc.ToUnixTimeSeconds() > 0L)
                {
                    item.DateModified = file.LastWriteTimeUtc;
                    _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.None, true, false, null, CancellationToken.None);
                }
            }
        }

        public async Task<bool?> OrchestrateMediaInfoProcessAsync(BaseItem taskItem, string source,
            CancellationToken cancellationToken, bool forceExtract = false, bool forcePersist = false)
        {
            var persistMediaInfoMode = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode;
            var persistMediaInfo =
                taskItem is Video && (forcePersist || persistMediaInfoMode != PersistMediaInfoOption.None.ToString());
            var mediaInfoRestoreMode =
                !forceExtract && persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var filePath = taskItem.Path;
            if (taskItem.IsShortcut)
            {
                filePath = await GetStrmMountPath(filePath).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(filePath)) return null;

            var fileExtension = Path.GetExtension(filePath).TrimStart('.');
            var extractSkip = !forceExtract && (mediaInfoRestoreMode || ExcludeMediaExtensions.Contains(fileExtension));

            var refreshOptions = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions();

            var directoryService = refreshOptions.DirectoryService;

            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                uri.Scheme == Uri.UriSchemeFile)
            {
                var file = directoryService.GetFile(filePath);
                if (file?.Exists != true) return null;
            }

            var collectionFolders = (BaseItem[])_libraryManager.GetCollectionFolders(taskItem);
            var libraryOptions = _libraryManager.GetLibraryOptions(taskItem);

            var dummyLibraryOptions = CopyLibraryOptions(libraryOptions);
            dummyLibraryOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
            dummyLibraryOptions.MetadataSavers = Array.Empty<string>();

            foreach (var option in dummyLibraryOptions.TypeOptions)
            {
                option.MetadataFetchers = Array.Empty<string>();
                option.ImageFetchers = Array.Empty<string>();
            }

            if (persistMediaInfo)
            {
                var deserializeResult =
                    await Plugin.MediaInfoApi
                        .DeserializeMediaInfo(taskItem, directoryService, source, mediaInfoRestoreMode)
                        .ConfigureAwait(false);

                if (deserializeResult)
                {
                    if (Plugin.SubtitleApi.HasExternalSubtitleChanged(taskItem, directoryService, true))
                    {
                        await Plugin.SubtitleApi.UpdateExternalSubtitles(taskItem, refreshOptions, false, true)
                            .ConfigureAwait(false);
                    }

                    return false;
                }
            }

            if (extractSkip) return null;

            taskItem.DateLastRefreshed = new DateTimeOffset();

            await _providerManager
                .RefreshSingleItem(taskItem, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                .ConfigureAwait(false);

            if (persistMediaInfo)
            {
                await Plugin.MediaInfoApi.SerializeMediaInfo(taskItem.InternalId, directoryService, true, source)
                    .ConfigureAwait(false);
            }

            return true;
        }

        public static LibraryOptions CopyLibraryOptions(LibraryOptions sourceOptions)
        {
            var targetOptions = new LibraryOptions
            {
                EnableArchiveMediaFiles = sourceOptions.EnableArchiveMediaFiles,
                EnablePhotos = sourceOptions.EnablePhotos,
                EnableRealtimeMonitor = sourceOptions.EnableRealtimeMonitor,
                EnableMarkerDetection = sourceOptions.EnableMarkerDetection,
                EnableMarkerDetectionDuringLibraryScan = sourceOptions.EnableMarkerDetectionDuringLibraryScan,
                IntroDetectionFingerprintLength = sourceOptions.IntroDetectionFingerprintLength,
                EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction,
                ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan,
                DownloadImagesInAdvance = sourceOptions.DownloadImagesInAdvance,
                CacheImages = sourceOptions.CacheImages,
                PathInfos =
                    sourceOptions.PathInfos?.Select(p => new MediaPathInfo
                        {
                            Path = p.Path, NetworkPath = p.NetworkPath, Username = p.Username, Password = p.Password
                        })
                        .ToArray() ?? Array.Empty<MediaPathInfo>(),
                IgnoreHiddenFiles = sourceOptions.IgnoreHiddenFiles,
                IgnoreFileExtensions =
                    sourceOptions.IgnoreFileExtensions?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLocalMetadata = sourceOptions.SaveLocalMetadata,
                SaveMetadataHidden = sourceOptions.SaveMetadataHidden,
                SaveLocalThumbnailSets = sourceOptions.SaveLocalThumbnailSets,
                ImportPlaylists = sourceOptions.ImportPlaylists,
                EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping,
                ShareEmbeddedMusicAlbumImages = sourceOptions.ShareEmbeddedMusicAlbumImages,
                EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles,
                EnableAudioResume = sourceOptions.EnableAudioResume,
                AutoGenerateChapters = sourceOptions.AutoGenerateChapters,
                AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays,
                PlaceholderMetadataRefreshIntervalDays = sourceOptions.PlaceholderMetadataRefreshIntervalDays,
                PreferredMetadataLanguage = sourceOptions.PreferredMetadataLanguage,
                PreferredImageLanguage = sourceOptions.PreferredImageLanguage,
                ContentType = sourceOptions.ContentType,
                MetadataCountryCode = sourceOptions.MetadataCountryCode,
                MetadataSavers = sourceOptions.MetadataSavers?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledLocalMetadataReaders =
                    sourceOptions.DisabledLocalMetadataReaders?.Clone() as string[] ?? Array.Empty<string>(),
                LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder?.Clone() as string[] ?? null,
                DisabledLyricsFetchers =
                    sourceOptions.DisabledLyricsFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLyricsWithMedia = sourceOptions.SaveLyricsWithMedia,
                LyricsDownloadMaxAgeDays = sourceOptions.LyricsDownloadMaxAgeDays,
                LyricsFetcherOrder = sourceOptions.LyricsFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                LyricsDownloadLanguages =
                    sourceOptions.LyricsDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledSubtitleFetchers =
                    sourceOptions.DisabledSubtitleFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleFetcherOrder =
                    sourceOptions.SubtitleFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches,
                SubtitleDownloadLanguages =
                    sourceOptions.SubtitleDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleDownloadMaxAgeDays = sourceOptions.SubtitleDownloadMaxAgeDays,
                RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch,
                SaveSubtitlesWithMedia = sourceOptions.SaveSubtitlesWithMedia,
                ForcedSubtitlesOnly = sourceOptions.ForcedSubtitlesOnly,
                HearingImpairedSubtitlesOnly = sourceOptions.HearingImpairedSubtitlesOnly,
                CollapseSingleItemFolders = sourceOptions.CollapseSingleItemFolders,
                EnableAdultMetadata = sourceOptions.EnableAdultMetadata,
                ImportCollections = sourceOptions.ImportCollections,
                MinCollectionItems = sourceOptions.MinCollectionItems,
                MusicFolderStructure = sourceOptions.MusicFolderStructure,
                MinResumePct = sourceOptions.MinResumePct,
                MaxResumePct = sourceOptions.MaxResumePct,
                MinResumeDurationSeconds = sourceOptions.MinResumeDurationSeconds,
                ThumbnailImagesIntervalSeconds = sourceOptions.ThumbnailImagesIntervalSeconds,
                SampleIgnoreSize = sourceOptions.SampleIgnoreSize,
                TypeOptions = sourceOptions.TypeOptions.Select(t => new TypeOptions
                    {
                        Type = t.Type,
                        MetadataFetchers = t.MetadataFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                        MetadataFetcherOrder = t.MetadataFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageFetchers = t.ImageFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageFetcherOrder = t.ImageFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageOptions = t.ImageOptions?.Select(i =>
                                new ImageOption { Type = i.Type, Limit = i.Limit, MinWidth = i.MinWidth })
                            .ToArray() ?? Array.Empty<ImageOption>()
                    })
                    .ToArray()
            };

            return targetOptions;
        }

        public static bool IsFileShortcut(string path)
        {
            return path != null && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetStrmMountPath(string strmPath)
        {
            using var mediaMount = await MountStrmPath(strmPath).ConfigureAwait(false);

            return GetMountedPath(mediaMount);
        }

        private async Task<IMediaMount> MountStrmPath(string strmPath)
        {
            var mountMethod = _mediaMountManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Mount") return false;
                    var parameters = m.GetParameters();
                    return parameters.Length == 3 &&
                           (parameters[0].ParameterType == typeof(string) ||
                            parameters[0].ParameterType == typeof(ReadOnlyMemory<char>));
                });

            if (mountMethod is null) return null;

            var parameters = mountMethod.GetParameters();
            var task = (Task)mountMethod.Invoke(_mediaMountManager, new[]
            {
                GetMountArgument(parameters[0].ParameterType, strmPath),
                GetMountArgument(parameters[1].ParameterType, null),
                CancellationToken.None
            });

            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task) as IMediaMount;
        }

        private static object GetMountArgument(Type parameterType, string value)
        {
            if (parameterType == typeof(string)) return value;
            if (parameterType == typeof(ReadOnlyMemory<char>)) return value?.AsMemory() ?? ReadOnlyMemory<char>.Empty;

            return value;
        }

        private static string GetMountedPath(IMediaMount mediaMount)
        {
            if (mediaMount is null) return null;

            var mediaMountType = mediaMount.GetType();
            if (mediaMountType.GetProperty("MountedPath")?.GetValue(mediaMount) is string mountedPath)
            {
                return mountedPath;
            }

            var mountedPathInfo = mediaMountType.GetProperty("MountedPathInfo")?.GetValue(mediaMount);
            return mountedPathInfo?.GetType().GetProperty("FullName")?.GetValue(mountedPathInfo) as string;
        }

        public BaseItem[] GetItemsByIds(long[] itemIds)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = itemIds });

            var dict = items.ToDictionary(i => i.InternalId, i => i);

            return itemIds.Select(id => CollectionExtensions.GetValueOrDefault(dict, id))
                .Where(item => item != null)
                .ToArray();
        }

        public List<CollectionFolder> GetMovieLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.Movies.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<CollectionFolder> GetSeriesLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.TvShows.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<Movie> FetchSplitMovieItems()
        {
            var libraries = GetMovieLibraries();

            if (!libraries.Any()) return new List<Movie>();

            _logger.Info("MergeMovie - Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

            var libraryIds = libraries.Select(l => l.InternalId).ToArray();

            var movieQuery = new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = libraryIds,
                GroupByPresentationUniqueKey = true,
                IncludeItemTypes = new[] { nameof(Movie) }
            };

            var altMovies = _libraryManager.GetItemList(movieQuery).Cast<Movie>()
                .Where(m => m.GetAlternateVersionIds().Count > 0).ToList();

            return altMovies;
        }

        public void EnsureLibraryEnabledAutomaticSeriesGrouping()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (!options.EnableAutomaticSeriesGrouping && long.TryParse(library.ItemId, out var itemId))
                {
                    options.EnableAutomaticSeriesGrouping = true;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        private FileSystemMetadata[] GetRelatedPaths(string basename, string folder)
        {
            var extensions = new List<string>
            {
                ".nfo",
                ".xml",
                ".srt",
                ".vtt",
                ".sub",
                ".idx",
                ".txt",
                ".edl",
                ".bif",
                ".smi",
                ".ttml",
                ".ass",
                ".json"
            };

            extensions.AddRange(BaseItem.SupportedImageExtensions);
            return _fileSystem.GetFiles(folder, extensions.ToArray(), false, false)
                .Where(i => !string.IsNullOrEmpty(i.FullName) && Path.GetFileNameWithoutExtension(i.FullName)
                    .StartsWith(basename, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public FileSystemMetadata[] GetDeletePaths(BaseItem item)
        {
            var basename = item.FileNameWithoutExtension;
            var folder = _fileSystem.GetDirectoryName(item.Path);
            var relatedFiles = GetRelatedPaths(basename, folder);

            return new[] { new FileSystemMetadata { FullName = item.Path, IsDirectory = item.IsFolder } }
                .Concat(relatedFiles)
                .ToArray();
        }

        public async Task RefreshEpisodeMetadata(Episode taskItem, CancellationToken cancellationToken)
        {
            var collectionFolders = (BaseItem[])_libraryManager.GetCollectionFolders(taskItem);
            var libraryOptions = _libraryManager.GetLibraryOptions(taskItem);
            var dummyLibraryOptions = CopyLibraryOptions(libraryOptions);
            var typeOption = dummyLibraryOptions.TypeOptions.FirstOrDefault(o => o.Type == nameof(Episode));

            if (typeOption != null)
            {
                if (typeOption.MetadataFetchers.Length == 0)
                {
                    typeOption.MetadataFetchers = new[] { "TheMovieDb" };
                }

                if (typeOption.ImageFetchers.Length == 0)
                {
                    typeOption.ImageFetchers = new[] { "TheMovieDb" };
                }
            }

            var refreshOptions = Plugin.MetadataApi.GetMetadataFullRefreshOptions();

            await _providerManager
                .RefreshSingleItem(taskItem, refreshOptions, collectionFolders, dummyLibraryOptions,
                    cancellationToken).ConfigureAwait(false);
        }

        private bool EpisodeNeedsRefresh(Episode item, DateTimeOffset lookBackTime,
            HashSet<EpisodeRefreshOption> episodeRefreshOptions, bool includeNoPrem)
        {
            var name = item.Name;
            var overview = item.Overview;
            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            var dimensionsMatch = item.Width > 0 && item.Height > 0 && item.Width == primaryImageInfo?.Width &&
                                  item.Height == primaryImageInfo?.Height;
            var hasProviderIds = item.Series != null && (item.Series.HasProviderId(MetadataProviders.Tmdb) ||
                                                         item.Series.HasProviderId(MetadataProviders.Imdb) ||
                                                         item.Series.HasProviderId(MetadataProviders.Tvdb));

            var needsRefresh = string.IsNullOrWhiteSpace(overview) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NoImage) &&
                                primaryImageInfo is null) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NonChineseOverview) &&
                                !IsChinese(overview)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.DefaultEpisodeName) &&
                                IsDefaultChineseEpisodeName(name)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.ReplaceCapturedImage) &&
                                dimensionsMatch);

            return needsRefresh && hasProviderIds && IsPremiereDateInScope(item, lookBackTime, includeNoPrem);
        }

        public List<Episode> FetchEpisodeRefreshTaskItems()
        {
            var lookBackDays = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshLookbackDays;
            _logger.Info("EpisodeRefresh - Lookback days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues(typeof(EpisodeRefreshOption))
                .Cast<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var itemsToRefresh = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) }, HasIndexNumber = true, IsLocked = false
                })
                .OfType<Episode>()
                .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, true))
                .OrderByDescending(GetPremiereDateOrDefault)
                .ToList();

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }

        public List<Episode> FetchEpisodeRefreshQueueItems(List<Episode> items)
        {
            const int lookBackDays = 90;
            _logger.Info("EpisodeRefresh - Lookback days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues(typeof(EpisodeRefreshOption))
                .Cast<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var groupedBySeason = items.GroupBy(i => i.Season);
            var itemsToRefresh = new List<Episode>();

            foreach (var group in groupedBySeason)
            {
                var season = group.Key;

                var episodes = season.GetEpisodes(new InternalItemsQuery
                    {
                        ExcludeItemIds = group.Select(e => e.InternalId).ToArray(),
                        IncludeItemTypes = new[] { nameof(Episode) },
                        HasIndexNumber = true,
                        IsLocked = false,
                        OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
                    })
                    .Items.OfType<Episode>()
                    .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, false));

                itemsToRefresh.AddRange(episodes);
            }

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }
    }
}
