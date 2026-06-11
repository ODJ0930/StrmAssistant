using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class VideoThumbnailApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        private readonly object _thumbnailGenerator;
        private readonly MethodInfo _refreshThumbnailImages;

        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4936 = new Version("4.9.0.36");

        public VideoThumbnailApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IImageExtractionManager imageExtractionManager, IItemRepository itemRepository,
            IMediaMountManager mediaMountManager, IServerApplicationPaths applicationPaths,
            ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var thumbnailGenerator = embyProviders.GetType("Emby.Providers.MediaInfo.ThumbnailGenerator");
                var thumbnailGeneratorConstructor = thumbnailGenerator.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IImageExtractionManager),
                        typeof(IItemRepository), typeof(IMediaMountManager), typeof(IServerApplicationPaths),
                        typeof(ILibraryMonitor), typeof(IFfmpegManager)
                    }, null);
                _thumbnailGenerator = thumbnailGeneratorConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, imageExtractionManager, itemRepository, mediaMountManager,
                    applicationPaths, libraryMonitor, ffmpegManager
                });
                _refreshThumbnailImages = thumbnailGenerator.GetMethod("RefreshThumbnailImages",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_thumbnailGenerator is null || _refreshThumbnailImages is null)
            {
                _logger.Warn($"{nameof(VideoThumbnailApi)} Init Failed");
            }
        }

        public Task<bool> RefreshThumbnailImages(Video item, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken)
        {
            var mediaSource = AppVer >= Ver4936
                ? item.GetMediaSources(false, false, libraryOptions).FirstOrDefault()
                : null;

            var methodParameters = _refreshThumbnailImages.GetParameters();
            object[] parameters;
            switch (methodParameters.Length)
            {
                case 10:
                    parameters = new object[]
                    {
                        item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                        extractImages, saveChapters, cancellationToken
                    };
                    break;
                case 9:
                    parameters = new object[]
                    {
                        item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                        saveChapters, cancellationToken
                    };
                    break;
                default:
                    parameters = new object[]
                    {
                        item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                        cancellationToken
                    };
                    break;
            }

            return (Task<bool>)_refreshThumbnailImages.Invoke(_thumbnailGenerator, parameters);
        }

        public List<Video> FetchExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions
                .LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithVideoThumbnail = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableChapterImageExtraction)
                .ToList();
            var librariesSelected = librariesWithVideoThumbnail.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("VideoThumbnailExtract - LibraryScope: " + (!librariesWithVideoThumbnail.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var librariesWithVideoThumbnailPaths = librariesWithVideoThumbnail.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();
            var librariesSelectedPaths = librariesSelected.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();

            var favoritesWithExtra = Array.Empty<BaseItem>();

            if (libraryIds.Contains("-1") && librariesWithVideoThumbnailPaths.Any())
            {
                var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true,
                        PathStartsWithAny = librariesWithVideoThumbnailPaths
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded
                    .Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(LibraryApi.IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(i => Plugin.LibraryApi.HasMediaInfo(i) && !i.HasImage(ImageType.Chapter))
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() && librariesWithVideoThumbnailPaths.Any() ||
                libraryIds.Any(id => id != "-1") && librariesSelectedPaths.Any())
            {
                var videoThumbnailQuery = new InternalItemsQuery
                {
                    MediaTypes = new[] { MediaType.Video },
                    HasAudioStream = true,
                    HasChapterImages = false,
                    PathStartsWithAny = !libraryIds.Any() ? librariesWithVideoThumbnailPaths : librariesSelectedPaths
                };

                items = _libraryManager.GetItemList(videoThumbnailQuery);

                if (includeExtra)
                {
                    videoThumbnailQuery.ExtraTypes = LibraryApi.IncludeExtraTypes;
                    extras = _libraryManager.GetItemList(videoThumbnailQuery);
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).Where(i => isModSupported || !i.IsShortcut).OfType<Video>().ToList();

            return combined;
        }
    }
}
