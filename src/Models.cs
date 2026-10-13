using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public enum DownloadState { Queued, Running, Paused, Stopped, Complete, Error, Seeding, History, Skipped }
    public enum ScheduleMode { Manual, Once, Daily, Interval }
    public enum CompletionAction { None, Exit, Shutdown, Sleep, Hibernate }
    public enum ExistingFileAction { Rename, Overwrite, Ask, Skip }

    public sealed class DownloadItem
    {
        public string Id { get; set; }
        public string Url { get; set; }
        [ScriptIgnore] public string DirectUrl { get; set; }
        public string LinkText { get; set; }
        public string SourcePage { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public bool DirectoryExplicit { get; set; }
        public string WorkingDirectory { get; set; }
        public List<string> OwnedFiles { get; set; }
        public string Category { get; set; }
        public string QueueId { get; set; }
        public string EngineId { get; set; }
        public bool UseCurl { get; set; }
        public string Kind { get; set; }
        public string Referer { get; set; }
        public string UserAgent { get; set; }
        public string Method { get; set; }
        public string Body { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public long BytesPerSecond { get; set; }
        public DownloadState State { get; set; }
        public string Error { get; set; }
        public int Priority { get; set; }
        public int Connections { get; set; }
        public long SpeedLimit { get; set; }
        public DateTime? AddedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? HistoryAt { get; set; }
        public bool Enabled { get; set; }
        public bool ForceStart { get; set; }
        public bool Seed { get; set; }
        public string SelectedFiles { get; set; }
        public string MediaFormat { get; set; }
        public string MediaTitle { get; set; }
        public string ThumbnailUrl { get; set; }
        public string MediaQuality { get; set; }
        public double MediaDurationSeconds { get; set; }
        public long? MediaViewCount { get; set; }
        public List<MediaFileInformation> MediaFiles { get; set; }
        public string MediaContainer { get; set; }
        public string MediaSubtitleLanguage { get; set; }
        public string MediaAudioFormat { get; set; }
        public string MediaFileNamePrefix { get; set; }
        public int MediaAudioBitrate { get; set; }
        public int MediaHeight { get; set; }
        public bool MediaExactMatch { get; set; }
        public bool MediaOnlyAssets { get; set; }
        public bool? SaveThumbnail { get; set; }
        public ScheduleSettings Schedule { get; set; }
        public bool SaveSubtitles { get; set; }
        public ExistingFileAction? ConflictAction { get; set; }
        [ScriptIgnore] public string Phase { get; set; }
        [ScriptIgnore] public long MediaAudioBytes { get; set; }
        [ScriptIgnore] public long MediaAudioTotal { get; set; }
        [ScriptIgnore] public long MediaVideoBytes { get; set; }
        [ScriptIgnore] public long MediaVideoTotal { get; set; }
        [ScriptIgnore] public byte[] AnimationFrame { get; set; }
        [ScriptIgnore] public long UploadedBytes { get; set; }
        [ScriptIgnore] public long UploadBytesPerSecond { get; set; }
        [ScriptIgnore] public List<TorrentFileProgress> TorrentFiles { get; set; }
        public string OutputPath { get { return Path.Combine(Directory ?? "", FileName ?? ""); } }

        public DownloadItem()
        {
            Id = Guid.NewGuid().ToString("N");
            Url = LinkText = FileName = Directory = Category = QueueId = EngineId = "";
            SourcePage = "";
            DirectUrl = "";
            WorkingDirectory = "";
            OwnedFiles = new List<string>();
            Referer = UserAgent = Body = Error = SelectedFiles = MediaFormat = "";
            MediaContainer = MediaSubtitleLanguage = MediaAudioFormat = MediaFileNamePrefix = "";
            MediaTitle = ThumbnailUrl = MediaQuality = "";
            MediaFiles = new List<MediaFileInformation>();
            Phase = ""; TorrentFiles = new List<TorrentFileProgress>();
            Kind = "file";
            Method = "GET";
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            State = DownloadState.Queued;
            Connections = 8;
            AddedAt = DateTime.Now;
            Enabled = true;
            Seed = true;
        }

        public DownloadItem Copy()
        {
            DownloadItem copy = (DownloadItem)MemberwiseClone();
            copy.Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase);
            copy.OwnedFiles = new List<string>(OwnedFiles);
            copy.MediaFiles = MediaFiles.ConvertAll(info => info.Copy());
            copy.Schedule = Schedule == null ? null : Schedule.Copy();
            return copy;
        }

        public string DisplayUrl(bool direct) { return direct && DirectUrl.Length > 0 ? DirectUrl : Url; }
    }

    public sealed class TorrentFileProgress
    {
        public int Index { get; set; }
        public string Path { get; set; }
        public long TotalBytes { get; set; }
        public long CompletedBytes { get; set; }
        public bool Selected { get; set; }
    }

    public class ScheduleSettings
    {
        public ScheduleMode ScheduleMode { get; set; }
        public DateTime? StartAt { get; set; }
        public DateTime? StopAt { get; set; }
        public List<DayOfWeek> Days { get; set; }
        public int RepeatIntervalMinutes { get; set; }
        public int RetryCount { get; set; }
        public int RetryDelaySeconds { get; set; }
        public bool RenameExisting { get; set; }

        public ScheduleSettings()
        {
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
            RepeatIntervalMinutes = 60;
            RetryCount = 3;
            RetryDelaySeconds = 5;
        }

        public ScheduleSettings Copy()
        {
            ScheduleSettings copy = (ScheduleSettings)MemberwiseClone();
            copy.Days = new List<DayOfWeek>(Days);
            return copy;
        }
    }

    public sealed class QueueSettings : ScheduleSettings
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public int MaxConcurrent { get; set; }
        public CompletionAction CompletionAction { get; set; }
        public string CompletionCommand { get; set; }
        public string CompletionArguments { get; set; }

        public QueueSettings()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            Enabled = true;
            MaxConcurrent = 2;
            CompletionCommand = CompletionArguments = "";
        }

        public new QueueSettings Copy()
        {
            QueueSettings copy = (QueueSettings)MemberwiseClone();
            copy.Days = new List<DayOfWeek>(Days);
            return copy;
        }

        public override string ToString() { return Name; }
    }

    public sealed class SiteRule
    {
        public string HostPattern { get; set; }
        public string Proxy { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool SingleConnection { get; set; }

        public SiteRule() { HostPattern = Proxy = Username = Password = ""; }
        public SiteRule Copy() { return (SiteRule)MemberwiseClone(); }
    }

    public sealed class CategorySettings
    {
        public string DefaultId { get; set; }
        public string Name { get; set; }
        public string Directory { get; set; }
        public List<string> Extensions { get; set; }
        public List<string> Hosts { get; set; }
        public bool Enabled { get; set; }

        public CategorySettings()
        {
            DefaultId = Name = Directory = "";
            Extensions = new List<string>();
            Hosts = new List<string>();
            Enabled = true;
        }

        public static List<CategorySettings> Defaults(string language)
        {
            string[] ids = { "documents", "compressed", "programs", "images", "music", "video", "web", "other" };
            string[] names = language == "zh-CN" ? new[] { "文档", "压缩包", "程序", "图片", "音乐", "视频", "网页", "其他" } :
                new[] { "Documents", "Compressed", "Programs", "Images", "Music", "Video", "Web", "Other" };
            string[] extensions = {
                "doc,docx,dot,dotx,pdf,ppt,xls,xla,xll,xlc,xlm,xlt,xlw,djvu,mpp,hlp",
                "zip,rar,arj,z,gz,tgz,lzh,gzip,tar,xz,bz2,7z,lha,lz,ace,sea,sitx,apk,xpi,crx,rpm",
                "exe,bin,iso,msi,sys,dll,cab,msu,deb,dmg",
                "tif,tiff,jpg,jpeg,png,ico,gif,ttf,gfa,pic,msp,bmp,pcx,psd",
                "mp3,ogg,oga,aac,m4a,wma,wmv,wav,ra,ram,ac3,flac,mid",
                "avi,mpeg,mp4,flv,iflv,mpg,qt,mov,webm,mkv,asf,mpe,asx,f4v,3gp,3gpp,rmvb,m4v,m4s,m2ts,m1v,m2v,ts,hlv,ogv",
                "html,htm,css,js,jsp,json,xml,php,asp,aspx,cer,cfm,csr,eml,jnlp,shtml,mht,mhtm,mhtml,swf,url,webloc,xhtml,xul",
                "*"
            };
            var categories = new List<CategorySettings>();
            for (int i = 0; i < ids.Length; i++)
                categories.Add(new CategorySettings { DefaultId = ids[i], Name = names[i], Extensions = new List<string>(extensions[i].Split(',')) });
            return categories;
        }

        public CategorySettings Copy()
        {
            CategorySettings copy = (CategorySettings)MemberwiseClone();
            copy.Extensions = new List<string>(Extensions);
            copy.Hosts = new List<string>(Hosts);
            return copy;
        }
    }

    public sealed class MediaSettings
    {
        public int YouTubeConnections { get; set; }
        public string VideoFormat { get; set; }
        public string AudioFormat { get; set; }
        public int AudioBitrate { get; set; }
        public string AudioLanguage { get; set; }
        public string SubtitleLanguage { get; set; }
        public bool SaveSubtitles { get; set; }
        public bool SaveThumbnail { get; set; }
        public bool EmbedMetadata { get; set; }
        public bool EmbedThumbnail { get; set; }
        public bool EmbedSubtitles { get; set; }
        public bool ReadDownloadedInformation { get; set; }
        public bool AnimateDownload { get; set; }
        public bool HideAutoTranslatedAudio { get; set; }
        public int MinimumSizeKb { get; set; }
        public int MinimumDurationSeconds { get; set; }
        public string FilenameTemplate { get; set; }
        public bool ConvertWebPToJpeg { get; set; }
        public bool ConvertAvifToJpeg { get; set; }

        public MediaSettings()
        {
            YouTubeConnections = 8;
            VideoFormat = "bestvideo+bestaudio/best";
            AudioFormat = "mp3";
            AudioBitrate = 192;
            AudioLanguage = "";
            SubtitleLanguage = "en";
            FilenameTemplate = "%(title)s.%(ext)s";
            EmbedMetadata = true;
        }

        public MediaSettings Copy() { return (MediaSettings)MemberwiseClone(); }
    }

    public sealed class DebridAccount
    {
        public string Id { get; set; }
        public string Provider { get; set; }
        public string Name { get; set; }
        public string ApiToken { get; set; }
        public bool Enabled { get; set; }

        public DebridAccount()
        {
            Id = Guid.NewGuid().ToString("N");
            Provider = Name = ApiToken = "";
            Enabled = true;
        }

        public DebridAccount Copy() { return (DebridAccount)MemberwiseClone(); }
    }

    public sealed class AppSettings
    {
        public string DownloadDirectory { get; set; }
        public string TemporaryDirectory { get; set; }
        public Dictionary<string, string> FileTypeDirectories { get; set; }
        public int MaxConcurrent { get; set; }
        public int Connections { get; set; }
        public long SpeedLimit { get; set; }
        public int RetryCount { get; set; }
        public int RetryDelaySeconds { get; set; }
        public bool ClipboardEnabled { get; set; }
        public bool ClipboardHighSensitivity { get; set; }
        public int ConnectionTimeoutSeconds { get; set; }
        public bool FtpPassive { get; set; }
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool CloseToTray { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool CenterPopups { get; set; }
        public Dictionary<string, Point> PopupPositions { get; set; }
        public string AppearanceTheme { get; set; }
        public float UiFontSize { get; set; }
        public string TaskView { get; set; }
        public bool ShowLinkBasket { get; set; }
        public bool LinkBasketOnTop { get; set; }
        public int? LinkBasketLeft { get; set; }
        public int? LinkBasketTop { get; set; }
        public List<int> ColumnWidths { get; set; }
        public bool Notifications { get; set; }
        public bool NotifyStarted { get; set; }
        public bool AnimateIcon { get; set; }
        public bool CheckUpdates { get; set; }
        public int UpdateCheckDays { get; set; }
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string SkippedUpdateVersion { get; set; }
        public bool SoundsEnabled { get; set; }
        public bool SoundAddedEnabled { get; set; }
        public bool SoundFinishedEnabled { get; set; }
        public bool SoundErrorEnabled { get; set; }
        public bool SoundAllFinishedEnabled { get; set; }
        public string SoundAddedFile { get; set; }
        public string SoundFinishedFile { get; set; }
        public string SoundErrorFile { get; set; }
        public string SoundAllFinishedFile { get; set; }
        public bool ShowProgressWindow { get; set; }
        public bool PreDownloadInAddDialog { get; set; }
        public bool ShowDirectUrls { get; set; }
        public bool CloseProgressAfterDownload { get; set; }
        public int CloseProgressDelaySeconds { get; set; }
        public bool ShowCompleteDialog { get; set; }
        public bool ConfirmTaskRemoval { get; set; }
        public bool ConfirmFileDeletion { get; set; }
        public bool ShowShutdownWarning { get; set; }
        public bool SuggestBrowserExclusions { get; set; }
        public bool AutoStartOnLaunch { get; set; }
        public bool RemoveCompletedTasks { get; set; }
        public bool AutoStartDownloads { get; set; }
        public bool PreventSleep { get; set; }
        public bool UseServerTimestamp { get; set; }
        public bool CheckDuplicateDownloads { get; set; }
        public bool KeepHistory { get; set; }
        public int HistoryDays { get; set; }
        public int HistoryLimit { get; set; }
        public ExistingFileAction ExistingFileAction { get; set; }
        public ExistingFileAction ExistingBatchFileAction { get; set; }
        public string Language { get; set; }
        public bool CaptureEnabled { get; set; }
        public bool MediaEnabled { get; set; }
        public int MediaBoxRows { get; set; }
        public bool MediaBoxTopMost { get; set; }
        public int MediaBoxOpacity { get; set; }
        public bool MediaBoxShowThumbnails { get; set; }
        public bool MediaBoxShowDetails { get; set; }
        public List<string> ExcludedHosts { get; set; }
        public List<string> MediaExcludedHosts { get; set; }
        public List<string> Extensions { get; set; }
        public List<string> ExcludedExtensions { get; set; }
        public string Proxy { get; set; }
        public List<SiteRule> SiteRules { get; set; }
        public List<DebridAccount> DebridAccounts { get; set; }
        public List<CategorySettings> Categories { get; set; }
        public List<QueueSettings> Queues { get; set; }
        public MediaSettings Media { get; set; }
        public bool TorrentEnabled { get; set; }
        public bool CheckTorrentAssociation { get; set; }
        public bool SeedAfterDownload { get; set; }
        public bool DhtEnabled { get; set; }
        public bool LocalPeerDiscovery { get; set; }
        public bool UpnpEnabled { get; set; }
        public string ScanCommand { get; set; }
        public string ScanArguments { get; set; }
        public List<string> ScanExtensions { get; set; }
        public string CompletionCommand { get; set; }
        public string CompletionArguments { get; set; }
        public CompletionAction CompletionAction { get; set; }

        public AppSettings()
        {
            DownloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            TemporaryDirectory = Proxy = ScanCommand = ScanArguments = CompletionCommand = CompletionArguments = "";
            FileTypeDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MaxConcurrent = 3;
            Connections = 8;
            RetryCount = 3;
            RetryDelaySeconds = 5;
            CloseToTray = Notifications = PreventSleep = CheckDuplicateDownloads = KeepHistory = true;
            ConfirmTaskRemoval = ConfirmFileDeletion = ShowShutdownWarning = SuggestBrowserExclusions = true;
            SoundAddedEnabled = SoundFinishedEnabled = SoundErrorEnabled = SoundAllFinishedEnabled = true;
            SoundAddedFile = SoundFinishedFile = SoundErrorFile = SoundAllFinishedFile = "";
            ClipboardHighSensitivity = FtpPassive = true;
            LinkBasketOnTop = true;
            CenterPopups = true; PopupPositions = new Dictionary<string, Point>();
            AppearanceTheme = "system"; UiFontSize = 9F;
            TaskView = "table";
            ConnectionTimeoutSeconds = 30;
            HistoryDays = 90;
            UpdateCheckDays = 7; SkippedUpdateVersion = "";
            HistoryLimit = 1000;
            CloseProgressDelaySeconds = 3;
            Language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
            CaptureEnabled = MediaEnabled = TorrentEnabled = SeedAfterDownload = DhtEnabled = LocalPeerDiscovery = UpnpEnabled = true;
            MediaBoxRows = 5; MediaBoxOpacity = 100; MediaBoxShowThumbnails = MediaBoxShowDetails = true;
            ExcludedHosts = new List<string>();
            MediaExcludedHosts = new List<string>();
            Extensions = new List<string> { "zip", "7z", "rar", "exe", "msi", "pdf", "iso", "mp4", "mp3", "torrent" };
            ExcludedExtensions = new List<string>();
            ScanExtensions = new List<string>();
            ColumnWidths = new List<int> { 260, 80, 95, 90, 90, 90, 100 };
            SiteRules = new List<SiteRule>();
            DebridAccounts = new List<DebridAccount>();
            Categories = new List<CategorySettings>();
            Queues = new List<QueueSettings>();
            Media = new MediaSettings();
        }

        public AppSettings Copy()
        {
            AppSettings copy = (AppSettings)MemberwiseClone();
            copy.FileTypeDirectories = new Dictionary<string, string>(FileTypeDirectories, StringComparer.OrdinalIgnoreCase);
            copy.PopupPositions = new Dictionary<string, Point>(PopupPositions);
            copy.ExcludedHosts = new List<string>(ExcludedHosts);
            copy.MediaExcludedHosts = new List<string>(MediaExcludedHosts);
            copy.Extensions = new List<string>(Extensions);
            copy.ExcludedExtensions = new List<string>(ExcludedExtensions);
            copy.ScanExtensions = new List<string>(ScanExtensions);
            copy.ColumnWidths = new List<int>(ColumnWidths);
            copy.SiteRules = SiteRules.ConvertAll(delegate(SiteRule rule) { return rule.Copy(); });
            copy.DebridAccounts = DebridAccounts.ConvertAll(delegate(DebridAccount account) { return account.Copy(); });
            copy.Categories = Categories.ConvertAll(delegate(CategorySettings category) { return category.Copy(); });
            copy.Queues = Queues.ConvertAll(delegate(QueueSettings queue) { return queue.Copy(); });
            copy.Media = Media.Copy();
            return copy;
        }

        public static string FileType(string url, string fileName)
        {
            if (fileName.Length > 0)
                return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() : "";
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? Path.GetExtension(uri.LocalPath).TrimStart('.').ToLowerInvariant() : "";
        }

        public string RememberedDirectory(string url, string fileName)
        {
            string directory;
            return FileTypeDirectories.TryGetValue(FileType(url, fileName), out directory) ? directory : "";
        }
    }
}
