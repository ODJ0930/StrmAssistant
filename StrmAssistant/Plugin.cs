using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.IntroSkip;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using StrmAssistant.ScheduledTask;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.MetadataEnhanceOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant
{
    public class Plugin: BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public static LibraryApi LibraryApi { get; private set; }
        public static MediaInfoApi MediaInfoApi { get; private set; }
        public static ChapterApi ChapterApi { get; private set; }
        public static FingerprintApi FingerprintApi { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static SubtitleApi SubtitleApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }
        public static MetadataApi MetadataApi { get; private set; }
        public static VideoThumbnailApi VideoThumbnailApi { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;

        private bool _currentSuppressOnOptionsSaved;
        private int _currentMaxConcurrentCount;
        private int _currentTier2ConcurrentCount;
        private bool _currentPersistMediaInfo;
        private bool _currentMediaInfoRestoreMode;
        private bool _currentCatchupMode;
        private bool _currentEnableIntroSkip;
        private bool _currentUnlockIntroSkip;
        private bool _currentMergeMultiVersion;

        private static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, ILibraryManager libraryManager, ISessionManager sessionManager,
            IItemRepository itemRepository, INotificationManager notificationManager, ILibraryMonitor libraryMonitor,
            IMediaSourceManager mediaSourceManager, IMediaMountManager mediaMountManager,
            IProviderManager providerManager, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IUserManager userManager, IUserDataManager userDataManager,
            IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager, ITaskManager taskManager,
            IImageExtractionManager imageExtractionManager, IServerApplicationPaths serverApplicationPaths) : base(applicationHost)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;

            _currentMaxConcurrentCount = GetOptions().GeneralOptions.MaxConcurrentCount;
            _currentPersistMediaInfo = GetOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                                       PersistMediaInfoOption.None.ToString();
            _currentMediaInfoRestoreMode = GetOptions().MediaInfoExtractOptions.PersistMediaInfoMode ==
                                           PersistMediaInfoOption.Restore.ToString();
            _currentCatchupMode = GetOptions().GeneralOptions.CatchupMode;
            _currentEnableIntroSkip = GetOptions().IntroSkipOptions.EnableIntroSkip;
            _currentUnlockIntroSkip = GetOptions().IntroSkipOptions.UnlockIntroSkip;
            _currentMergeMultiVersion = GetOptions().ExperienceEnhanceOptions.MergeMultiVersion;
            InitializeOptionCache();

            if (GetOptions().AboutOptions.DebugMode)
            {
                DebugMode = true;
                GetOptions().AboutOptions.DebugMode = false;
                SavePluginOptionsSuppress();
            }
            else if (Debugger.IsAttached)
            {
                DebugMode = true;
            }

            LibraryApi = new LibraryApi(libraryManager, providerManager, fileSystem, mediaMountManager, userManager);
            MediaInfoApi = new MediaInfoApi(libraryManager, fileSystem, providerManager, mediaSourceManager,
                itemRepository, jsonSerializer, libraryMonitor);
            ChapterApi = new ChapterApi(libraryManager, itemRepository);
            FingerprintApi = new FingerprintApi(libraryManager, fileSystem, applicationPaths, ffmpegManager,
                mediaEncoder, mediaMountManager, jsonSerializer, itemRepository, serverApplicationHost);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, userManager, sessionManager);
            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            SubtitleApi = new SubtitleApi(libraryManager, fileSystem, mediaProbeManager, localizationManager,
                itemRepository);
            MetadataApi = new MetadataApi(libraryManager, fileSystem, configurationManager, localizationManager,
                jsonSerializer, httpClient);
            VideoThumbnailApi = new VideoThumbnailApi(libraryManager, fileSystem, imageExtractionManager, itemRepository,
                mediaMountManager, serverApplicationPaths, libraryMonitor, ffmpegManager);
            ShortcutMenuHelper.Initialize(configurationManager);

            if (_currentCatchupMode) QueueManager.Initialize();
            if (_currentEnableIntroSkip) PlaySessionMonitor.Initialize();

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _userManager.UserCreated += OnUserCreated;
            _userManager.UserDeleted += OnUserDeleted;
            _userManager.UserConfigurationUpdated += OnUserConfigurationUpdated;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            if (_libraryManager.IsScanRunning) return;

            if (_currentMergeMultiVersion && e.Argument.Item.IsTopParent)
            {
                var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

                if (library != null && (library.CollectionType == CollectionType.Movies.ToString() ||
                                        library.CollectionType is null))
                {
                    MergeMultiVersionTask.CurrentScanLibrary.Value = library;

                    var mergeMoviesTask = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                        t.ScheduledTask is MergeMultiVersionTask);

                    if (mergeMoviesTask != null)
                    {
                        _ = _taskManager.Execute(mergeMoviesTask, new TaskOptions());
                    }
                }
            }
        }

        private void OnUserCreated(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserDeleted(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }
        
        private void OnUserConfigurationUpdated(object sender, GenericEventArgs<User> e)
        {
            if (e.Argument.Policy.IsAdministrator) LibraryApi.FetchAdminOrderedViews();
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e)
        {
            var library = e.Argument.Item1;

            if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
            {
                LibraryApi.UpdateLibraryPathsInScope();

                if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                {
                    PlaySessionMonitor.UpdateLibraryPathsInScope();
                    FingerprintApi.UpdateLibraryPathsInScope();

                    if (_currentUnlockIntroSkip)
                        FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

                    if (_currentMergeMultiVersion)
                        LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping();
                }
            }
        }

        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var deserializeResult = false;

                if (_currentPersistMediaInfo && e.Item is Video)
                {
                    deserializeResult = LibraryApi.HasMediaInfo(e.Item);

                    var directoryService = new DirectoryService(Logger, _fileSystem);

                    if (!deserializeResult)
                    {
                        deserializeResult = await MediaInfoApi.DeserializeMediaInfo(e.Item, directoryService,
                            "OnItemAdded Restore", true).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = MediaInfoApi.SerializeMediaInfo(e.Item.InternalId, directoryService, true,
                            "OnItemAdded Overwrite").ConfigureAwait(false);
                    }
                }

                if (_currentCatchupMode && (e.Item is Video || e.Item is Audio))
                {
                    if (_currentUnlockIntroSkip && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                        e.Item is Episode && FingerprintApi.IsLibraryInScope(e.Item) &&
                        (!deserializeResult || FingerprintApi.IsExtractNeeded(e.Item)))
                    {
                        QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                    }
                    else
                    {
                        if (IsCatchupTaskSelected(CatchupTask.MediaInfo) && !deserializeResult)
                        {
                            QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                        }

                        if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                            e.Item is Episode && PlaySessionMonitor.IsLibraryInScope(e.Item))
                        {
                            if (!deserializeResult)
                            {
                                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                            }
                            else if (e.Item is Episode episode && ChapterApi.SeasonHasIntroCredits(episode))
                            {
                                QueueManager.IntroSkipItemQueue.Enqueue(episode);
                            }
                        }
                    }

                    if (IsCatchupTaskSelected(CatchupTask.EpisodeRefresh) && e.Item is Episode ep &&
                        LibraryApi.IsPremiereDateInScope(ep, DateTimeOffset.UtcNow.AddDays(-90), false))
                    {
                        QueueManager.EpisodeRefreshItemQueue.Enqueue(ep);
                    }
                }

                if (e.Item is Movie || e.Item is Series || e.Item is Episode)
                {
                    NotificationApi.FavoritesUpdateSendNotification(e.Item);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (!_currentMediaInfoRestoreMode && e.Item is Video)
            {
                var directoryService = new DirectoryService(Logger, _fileSystem);
                MediaInfoApi.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
            }
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.UserData.IsFavorite)
            {
                if (_currentUnlockIntroSkip && _currentCatchupMode && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                    FingerprintApi.IsLibraryInScope(e.Item) && (e.Item is Episode || e.Item is Series))
                {
                    QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                }
                else if (_currentCatchupMode && IsCatchupTaskSelected(CatchupTask.MediaInfo))
                {
                    QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                }
            }
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "\u63d0\u53d6\u5a92\u4f53\u4fe1\u606f\u5e76\u589e\u5f3a\u7247\u5934\u8df3\u8fc7";

        public override Guid Id => _id;

        public sealed override string Name => "Strm Assistant";

        public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        public string UserAgent => $"{Name}/{CurrentVersion}";

        public CultureInfo DefaultUICulture => new CultureInfo("zh-CN");

        public bool DebugMode;

        public bool IsModSupported => false;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Properties.thumb.png");
        }

        public PluginOptions GetPluginOptions()
        {
            return GetOptions();
        }

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SaveOptions(GetOptions());
        }

        protected override bool OnOptionsSaving(PluginOptions options)
        {
            if (string.IsNullOrEmpty(options.GeneralOptions.CatchupTaskScope))
            {
                options.GeneralOptions.CatchupTaskScope = CatchupTask.MediaInfo.ToString();
            }
            else if (!options.IntroSkipOptions.UnlockIntroSkip)
            {
                var taskScope = options.GeneralOptions.CatchupTaskScope;
                var selectedTasks = taskScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != CatchupTask.Fingerprint.ToString())
                    .ToList();
                options.GeneralOptions.CatchupTaskScope = string.Join(",", selectedTasks);
            }

            options.MediaInfoExtractOptions.LibraryScope = string.Join(",",
                options.MediaInfoExtractOptions.LibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.MediaInfoExtractOptions.LibraryList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            options.IntroSkipOptions.LibraryScope = string.Join(",",
                options.IntroSkipOptions.LibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.IntroSkipOptions.LibraryList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            options.IntroSkipOptions.UserScope = string.Join(",",
                options.IntroSkipOptions.UserScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.IntroSkipOptions.UserList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            options.IntroSkipOptions.MarkerEnabledLibraryScope =
                options.IntroSkipOptions.MarkerEnabledLibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("-1") == true
                    ? "-1"
                    : string.Join(",",
                        options.IntroSkipOptions.MarkerEnabledLibraryScope
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(v => options.IntroSkipOptions.MarkerEnabledLibraryList.Any(option =>
                                option.Value == v)) ?? Enumerable.Empty<string>());

            return base.OnOptionsSaving(options);
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            var suppress = _currentSuppressOnOptionsSaved;

            if (_currentCatchupMode != options.GeneralOptions.CatchupMode)
            {
                _currentCatchupMode = options.GeneralOptions.CatchupMode;
                if (options.GeneralOptions.CatchupMode)
                {
                    QueueManager.Initialize();
                }
                else
                {
                    QueueManager.Dispose();
                }
            }
            UpdateCatchupScope();

            if (!suppress)
            {
                Logger.Info("CatchupMode is set to {0}", options.GeneralOptions.CatchupMode);
                var catchupTaskScope = GetSelectedCatchupTaskDescription();
                Logger.Info("CatchupTaskScope is set to {0}", string.IsNullOrEmpty(catchupTaskScope) ? "EMPTY" : catchupTaskScope);
            }

            if (!suppress)
            {
                Logger.Info("IncludeExtra is set to {0}", options.MediaInfoExtractOptions.IncludeExtra);
                Logger.Info("MaxConcurrentCount is set to {0}", options.GeneralOptions.MaxConcurrentCount);
                Logger.Info("CooldownDurationSeconds is set to {0}", options.GeneralOptions.CooldownDurationSeconds);
                Logger.Info("Tier2 MaxConcurrentCount is set to {0}", options.GeneralOptions.Tier2MaxConcurrentCount);

                var libraryScope = string.Join(", ",
                    options.MediaInfoExtractOptions.LibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v =>
                            options.MediaInfoExtractOptions.LibraryList.FirstOrDefault(option => option.Value == v)
                                ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("MediaInfoExtract - LibraryScope is set to {0}",
                    string.IsNullOrEmpty(libraryScope) ? "ALL" : libraryScope);
            }
            if (_currentMaxConcurrentCount != options.GeneralOptions.MaxConcurrentCount)
            {
                _currentMaxConcurrentCount = options.GeneralOptions.MaxConcurrentCount;

                QueueManager.UpdateMasterSemaphore(_currentMaxConcurrentCount);

                FingerprintApi.PatchTimeout(_currentMaxConcurrentCount);
            }
            if (_currentTier2ConcurrentCount != options.GeneralOptions.Tier2MaxConcurrentCount)
            {
                _currentTier2ConcurrentCount = options.GeneralOptions.Tier2MaxConcurrentCount;

                QueueManager.UpdateTier2Semaphore(_currentTier2ConcurrentCount);
            }
            LibraryApi.UpdateLibraryPathsInScope();

            if (!suppress)
            {
                Logger.Info("PersistMediaInfoMode is set to {0}", options.MediaInfoExtractOptions.PersistMediaInfoMode);
                Logger.Info("MediaInfoJsonRootFolder is set to {0}",
                    !string.IsNullOrEmpty(options.MediaInfoExtractOptions.MediaInfoJsonRootFolder)
                        ? options.MediaInfoExtractOptions.MediaInfoJsonRootFolder
                        : "EMPTY");
            }

            _currentPersistMediaInfo = options.MediaInfoExtractOptions.PersistMediaInfoMode !=
                                       PersistMediaInfoOption.None.ToString();
            _currentMediaInfoRestoreMode = options.MediaInfoExtractOptions.PersistMediaInfoMode ==
                                           PersistMediaInfoOption.Restore.ToString();

            if (!suppress)
            {
                Logger.Info("EnableIntroSkip is set to {0}", options.IntroSkipOptions.EnableIntroSkip);
                Logger.Info("MaxIntroDurationSeconds is set to {0}", options.IntroSkipOptions.MaxIntroDurationSeconds);
                Logger.Info("MaxCreditsDurationSeconds is set to {0}", options.IntroSkipOptions.MaxCreditsDurationSeconds);
                Logger.Info("MinOpeningPlotDurationSeconds is set to {0}", options.IntroSkipOptions.MinOpeningPlotDurationSeconds);
            }
            if (_currentEnableIntroSkip != options.IntroSkipOptions.EnableIntroSkip)
            {
                _currentEnableIntroSkip = options.IntroSkipOptions.EnableIntroSkip;
                if (options.IntroSkipOptions.EnableIntroSkip)
                {
                    PlaySessionMonitor.Initialize();
                }
                else
                {
                    PlaySessionMonitor.Dispose();
                }
            }

            UpdateIntroSkipPreferences();

            if (!suppress)
            {
                var intoSkipLibraryScope = string.Join(", ",
                    options.IntroSkipOptions.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.IntroSkipOptions.LibraryList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("IntroSkip - LibraryScope is set to {0}",
                        string.IsNullOrEmpty(intoSkipLibraryScope) ? "ALL" : intoSkipLibraryScope);

                var introSkipUserScope = string.Join(", ",
                    options.IntroSkipOptions.UserScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.IntroSkipOptions.UserList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("IntroSkip - UserScope is set to {0}",
                        string.IsNullOrEmpty(introSkipUserScope) ? "ALL" : introSkipUserScope);

                Logger.Info("IntroSkip - ClientScope is set to {0}", options.IntroSkipOptions.ClientScope);
                var introSkipPreferences = GetSelectedIntroSkipPreferenceDescription();

                Logger.Info("IntroSkipPreferences is set to {0}",
                    string.IsNullOrEmpty(introSkipPreferences) ? "EMPTY" : introSkipPreferences);
            }
            PlaySessionMonitor.UpdateLibraryPathsInScope();
            PlaySessionMonitor.UpdateUsersInScope();
            PlaySessionMonitor.UpdateClientInScope();
            
            if (!suppress)
            {
                Logger.Info("UnlockIntroSkip is set to {0}", options.IntroSkipOptions.UnlockIntroSkip);
                var markerEnabledLibraryScope = string.Join(", ",
                    options.IntroSkipOptions.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v =>
                            options.IntroSkipOptions.MarkerEnabledLibraryList
                                .FirstOrDefault(option => option.Value == v)?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("MarkerEnabledLibraryScope is set to {0}",
                    string.IsNullOrEmpty(markerEnabledLibraryScope)
                        ? options.IntroSkipOptions.MarkerEnabledLibraryList.Any(o => o.Value != "-1") ? "ALL" : "EMPTY"
                        : markerEnabledLibraryScope);
                Logger.Info("IntroDetectionFingerprintMinutes is set to {0}",
                    options.IntroSkipOptions.IntroDetectionFingerprintMinutes);
            }
            _currentUnlockIntroSkip = options.IntroSkipOptions.UnlockIntroSkip;
            FingerprintApi.UpdateLibraryPathsInScope();
            FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

            if (!suppress)
            {
                Logger.Info("MergeMultiVersion is set to {0}", options.ExperienceEnhanceOptions.MergeMultiVersion);
                Logger.Info("MergeMoviesPreference is set to {0}",
                    EnumExtensions.GetDescription(options.ExperienceEnhanceOptions.MergeMoviesPreference));
            }
            _currentMergeMultiVersion = options.ExperienceEnhanceOptions.MergeMultiVersion;

            if (suppress) _currentSuppressOnOptionsSaved = false;

            base.OnOptionsSaved(options);
        }

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            options.Disclaimer.Clear();
            options.Disclaimer.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerButtonText,
                    Icon = IconNames.privacy_tip,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant#%E5%A3%B0%E6%98%8E"
                });

            if (options.ShowConflictPluginLoadedStatus)
            {
                options.ConflictPluginLoadedStatus.Caption = Resources
                    .PluginOptions_IncompatibleMessage_Please_uninstall_the_conflict_plugin_Strm_Extract;
                options.ConflictPluginLoadedStatus.StatusText = string.Empty;
                options.ConflictPluginLoadedStatus.Status = ItemStatus.Warning;
            }
            else
            {
                options.ConflictPluginLoadedStatus.Caption = string.Empty;
                options.ConflictPluginLoadedStatus.StatusText = string.Empty;
                options.ConflictPluginLoadedStatus.Status = ItemStatus.None;
            }

            var libraries = _libraryManager.GetVirtualFolders();

            var list = new List<EditorSelectOption>();
            var listShow = new List<EditorSelectOption>();
            var listMarkerEnabled = new List<EditorSelectOption>();

            list.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            listMarkerEnabled.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            foreach (var item in libraries)
            {
                if (ExcludedCollectionTypes.Contains(item.CollectionType))
                {
                    continue;
                }

                var selectOption = new EditorSelectOption
                {
                    Value = item.ItemId,
                    Name = item.Name,
                    IsEnabled = true,
                };

                list.Add(selectOption);

                if (item.CollectionType == "tvshows" || item.CollectionType is null)
                {
                    listShow.Add(selectOption);

                    if (item.LibraryOptions.EnableMarkerDetection)
                    {
                        listMarkerEnabled.Add(selectOption);
                    }
                }
            }

            options.MediaInfoExtractOptions.LibraryList = list;
            options.IntroSkipOptions.LibraryList = listShow;
            options.IntroSkipOptions.MarkerEnabledLibraryList = listMarkerEnabled;

            var catchTaskList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(CatchupTask)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                catchTaskList.Add(selectOption);
            }

            options.GeneralOptions.CatchupTaskList = catchTaskList;

            var persistOptionList = new List<EditorRadioOption>
            {
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Default,
                    PrimaryText = Resources.MediaInfoExtractOptions_PersistMediaInfo_Persist_MediaInfo,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_PersistMediaInfo_Persist_media_info_in_JSON_file__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Restore,
                    PrimaryText = Resources.MediaInfoExtractOptions_MediaInfoRestoreMode_MediaInfo_Restore_Mode,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_MediaInfoRestoreMode_Only_restore_media_info__chapters__and_video_thumbnails_from_JSON_or_BIF__skipping_extraction__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.None,
                    PrimaryText = Resources.PersistMediaInfoOption_None_None
                }
            };

            options.MediaInfoExtractOptions.PersistMediaInfoOptionList = persistOptionList;

            var episodeRefreshOptionList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(EpisodeRefreshOption)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                episodeRefreshOptionList.Add(selectOption);
            }

            options.MetadataEnhanceOptions.EpisodeRefreshOptionList = episodeRefreshOptionList;

            var introSkipPreferenceList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(IntroSkipPreference)))
            {
                var selectPreference = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                introSkipPreferenceList.Add(selectPreference);
            }

            options.IntroSkipOptions.IntroSkipPreferenceList = introSkipPreferenceList;

            options.AboutOptions.VersionInfoList.Clear();
            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = GetVersionHash(),
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular
                });

            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant",
                });

            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Wiki_Link,
                    Icon = IconNames.menu_book,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant/wiki",
                });

            var allUsers = LibraryApi.AllUsers;
            var userList = new List<EditorSelectOption>();
            foreach (var user in allUsers)
            {
                var selectOption = new EditorSelectOption
                {
                    Value = user.Key.InternalId.ToString(),
                    Name = (user.Value ? "\ud83d\udc51" : "\ud83d\udc64") + user.Key.Name,
                    IsEnabled = true,
                };

                userList.Add(selectOption);
            }

            options.IntroSkipOptions.UserList = userList;

            return base.OnBeforeShowUI(options);
        }

        protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
        {
            pageInfo.Name = Name;
            pageInfo.DisplayName =
                Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant", DefaultUICulture);
            pageInfo.EnableInMainMenu = true;
            pageInfo.MenuIcon = "video_settings";

            base.OnCreatePageInfo(pageInfo);
        }

        private static string GetVersionHash()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            var fullVersion = assembly.GetName().Version?.ToString();

            if (informationalVersion != null)
            {
                var parts = informationalVersion.Split('+');
                var shortCommitHash = parts.Length > 1 ? parts[1].Substring(0, 7) : "n/a";
                return $"{fullVersion}+{shortCommitHash}";
            }

            return fullVersion;
        }
    }
}
