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

namespace StrmAssistant.ScheduledTask
{
    public class CheckMissingMediaInfoTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public CheckMissingMediaInfoTask(IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MediaInfoJsonGapCheck - Scheduled Task Execute");

            await Task.Yield();
            progress.Report(0);

            var directoryService = new DirectoryService(_logger, _fileSystem);
            var items = Plugin.LibraryApi.FetchMissingStrmMediaInfoJsonItems(directoryService);

            double total = items.Count;
            var current = 0;
            var success = 0;
            var skip = 0;

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("MediaInfoJsonGapCheck - Scheduled Task Cancelled");
                    return;
                }

                try
                {
                    var result = await Plugin.LibraryApi
                        .EnsureMediaInfoJsonAsync(item, directoryService, Name, cancellationToken)
                        .ConfigureAwait(false);

                    if (result)
                    {
                        success++;
                        _logger.Info("MediaInfoJsonGapCheck - Item processed: " + item.Name + " - " + item.Path);
                    }
                    else
                    {
                        skip++;
                        _logger.Info("MediaInfoJsonGapCheck - Item skipped: " + item.Name + " - " + item.Path);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("MediaInfoJsonGapCheck - Item cancelled: " + item.Name + " - " + item.Path);
                }
                catch (Exception e)
                {
                    skip++;
                    _logger.Error("MediaInfoJsonGapCheck - Item failed: " + item.Name + " - " + item.Path);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
                finally
                {
                    current++;
                    progress.Report(total > 0 ? current / total * 100 : 100);
                    _logger.Info("MediaInfoJsonGapCheck - Progress " + current + "/" + total + ": " + item.Path);
                }
            }

            progress.Report(100.0);
            _logger.Info("MediaInfoJsonGapCheck - Number of items processed: " + success);
            _logger.Info("MediaInfoJsonGapCheck - Number of items skipped: " + skip);
            _logger.Info("MediaInfoJsonGapCheck - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "CheckMissingMediaInfoTask";

        public string Description =>
            "\u68c0\u67e5\u5df2\u6709 STRM \u6587\u4ef6\u662f\u5426\u7f3a\u5c11 MediaInfo JSON\uff0c\u5e76\u9010\u4e00\u8865\u9f50\u3002";

        public string Name =>
            "\u68c0\u67e5\u8865\u6f0f\u7f3a\u5931\u5a92\u4f53\u4fe1\u606f";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
