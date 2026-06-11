using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.MediaInfoExtractOptions;

namespace StrmAssistant.ScheduledTask
{
    public class ExtractVideoThumbnailTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public ExtractVideoThumbnailTask(IFileSystem fileSystem, ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("VideoThumbnailExtract - Scheduled Task Execute");

            var maxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? Plugin.Instance.GetPluginOptions().GeneralOptions.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            var persistMediaInfoMode = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var items = Plugin.VideoThumbnailApi.FetchExtractTaskItems();
            _logger.Info($"VideoThumbnailExtract - Number of items: {items.Count}");

            if (items.Count > 0) IsRunning = true;

            var directoryService = new DirectoryService(_logger, _fileSystem);

            double total = items.Count;
            var index = 0;
            var current = 0;
            var skip = 0;

            var tasks = new List<Task>();

            foreach (var item in items)
            {
                try
                {
                    await QueueManager.MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.MasterSemaphore.Release();
                    _logger.Info("VideoThumbnailExtract - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    var result = false;

                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("VideoThumbnailExtract - Scheduled Task Cancelled");
                            return;
                        }

                        var chapters = _itemRepository.GetChapters(taskItem);

                        var thumbnailResult = false;

                        if (persistMediaInfo)
                        {
                            thumbnailResult = await Plugin.MediaInfoApi.DeserializeChapterInfo(taskItem, chapters,
                                directoryService, "VideoThumbnailExtract Task").ConfigureAwait(false);
                        }

                        if (!thumbnailResult)
                        {
                            if (!mediaInfoRestoreMode)
                            {
                                var libraryOptions = _libraryManager.GetLibraryOptions(taskItem);
                                result = await Plugin.VideoThumbnailApi
                                    .RefreshThumbnailImages(taskItem, libraryOptions, directoryService, chapters, true,
                                        true, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                Interlocked.Increment(ref skip);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info($"VideoThumbnailExtract - Item cancelled: {taskItem.Name} - {taskItem.Path}");
                    }
                    catch (Exception e)
                    {
                        _logger.Error($"VideoThumbnailExtract - Item failed: {taskItem.Name} - {taskItem.Path}");
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        if (result && cooldownSeconds.HasValue)
                        {
                            try
                            {
                                await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        QueueManager.MasterSemaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);

                        if (!mediaInfoRestoreMode)
                        {
                            _logger.Info(
                                $"VideoThumbnailExtract - Progress {currentCount}/{total} - Task {taskIndex}: {taskItem.Path}");
                        }
                    }
                }, cancellationToken);
                tasks.Add(task);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (items.Count > 0) IsRunning = false;

            progress.Report(100.0);
            _logger.Info($"VideoThumbnailExtract - Number of items skipped: {skip}");
            _logger.Info("VideoThumbnailExtract - Scheduled Task Complete");
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "VideoThumbnailExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractVideoThumbnailTask_Description_Extract_video_thumbnail_preview",
            Plugin.Instance.DefaultUICulture);

        public string Name => "\u63d0\u53d6\u89c6\u9891\u7f29\u7565\u56fe";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
