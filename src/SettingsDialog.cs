using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class SettingsDialog : Form
    {
        private AppSettings working;
        private readonly bool chinese;
        private readonly List<Action> saveActions = new List<Action>();
        private readonly TabControl tabs = new TabControl();
        private DataGridView sitesGrid;
        private DataGridView categoriesGrid;
        private ListBox queuesList;
        private readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left };

        public AppSettings Result { get; private set; }
        public event Action<AppSettings> ApplyRequested;

        public SettingsDialog(AppSettings settings)
        {
            working = settings.Copy();
            chinese = settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            Text = L("Settings", "设置");
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(750, 570);
            Size = new Size(860, 710);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tabs.Dock = DockStyle.Fill;
            tabs.Multiline = true;
            root.Controls.Add(tabs, 0, 0);
            BuildPages();
            root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(790, 0),
                Text = L("Basic defaults keep folders, proxies, site sign-ins, Debrid accounts, categories and queues. Changes take effect with Apply or OK.",
                    "基本重置保留目录、代理、站点登录、Debrid 账号、分类和队列。修改在点击“应用”或“确定”后生效。"), Margin = new Padding(3, 8, 3, 0) }, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = L("Cancel", "取消"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = L("OK", "确定"), AutoSize = true };
            var apply = new Button { Text = L("Apply", "应用"), AutoSize = true };
            var reset = new Button { Text = L("Restore basic defaults", "恢复基本默认"), AutoSize = true };
            save.Click += delegate { ApplyChanges(true); };
            apply.Click += delegate { ApplyChanges(false); };
            reset.Click += delegate { ResetBasic(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(apply);
            buttons.Controls.Add(reset);
            buttons.Controls.Add(status);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
            Appearance.Apply(this);
        }

        private string L(string english, string chineseText) { return chinese ? chineseText : english; }

        private void BuildPages()
        {
            while (tabs.TabPages.Count > 0)
            {
                TabPage page = tabs.TabPages[0];
                tabs.TabPages.RemoveAt(0);
                page.Dispose();
            }
            saveActions.Clear();
            sitesGrid = new DataGridView();
            categoriesGrid = new DataGridView();
            queuesList = new ListBox();
            BuildGeneral();
            BuildDownloads();
            BuildBrowser();
            BuildMedia();
            BuildSites();
            BuildCategories();
            BuildQueues();
            BuildTorrent();
            BuildAutomation();
            BuildSounds();
            BuildMessages();
        }

        private void BuildMessages()
        {
            var fields = AddPage(L("Messages", "提示"));
            BindCheck(fields, L("Confirm removing tasks", "移除任务前确认"), working.ConfirmTaskRemoval, value => working.ConfirmTaskRemoval = value);
            BindCheck(fields, L("Confirm deleting downloaded files", "删除下载文件前确认"), working.ConfirmFileDeletion, value => working.ConfirmFileDeletion = value);
            BindCheck(fields, L("Show countdown before shutting down after downloads", "下载完成后关机前显示倒计时"), working.ShowShutdownWarning, value => working.ShowShutdownWarning = value);
            BindCheck(fields, L("Suggest an exclusion after three browser download refusals", "连续三次拒绝浏览器下载后建议添加例外"), working.SuggestBrowserExclusions, value => working.SuggestBrowserExclusions = value);
        }

        private TableLayoutPanel AddPage(string title)
        {
            var page = new TabPage(title) { Padding = new Padding(12) };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 15, 10) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(fields);
            page.Controls.Add(scroll);
            tabs.TabPages.Add(page);
            return fields;
        }

        private static void AddRow(TableLayoutPanel fields, string text, Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 8) }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 5, 3, 5);
            fields.Controls.Add(control, 1, row);
        }

        private TextBox BindText(TableLayoutPanel fields, string label, string value, Action<string> save, bool multiline)
        {
            var box = new TextBox { Text = value, Multiline = multiline };
            if (multiline) { box.Height = 72; box.ScrollBars = ScrollBars.Vertical; box.AcceptsReturn = true; }
            AddRow(fields, label, box);
            saveActions.Add(delegate { save(box.Text); });
            return box;
        }

        private void BindCheck(TableLayoutPanel fields, string label, bool value, Action<bool> save)
        {
            var box = new CheckBox { Text = label, Checked = value, AutoSize = true };
            AddRow(fields, "", box);
            saveActions.Add(delegate { save(box.Checked); });
        }

        private void BindNumber(TableLayoutPanel fields, string label, decimal value, decimal minimum, decimal maximum, Action<decimal> save)
        {
            var box = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = Math.Max(minimum, Math.Min(maximum, value)), ThousandsSeparator = true };
            AddRow(fields, label, box);
            saveActions.Add(delegate { save(box.Value); });
        }

        private void BindChoice(TableLayoutPanel fields, string label, string[] choices, int index, Action<int> save)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(choices);
            box.SelectedIndex = index;
            AddRow(fields, label, box);
            saveActions.Add(delegate { save(box.SelectedIndex); });
        }

        private void BindPath(TableLayoutPanel fields, string label, string value, bool directory, Action<string> save)
        {
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            var browse = new Button { Text = L("Browse…", "浏览…"), AutoSize = true };
            var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(box, 0, 0);
            panel.Controls.Add(browse, 1, 0);
            browse.Click += delegate
            {
                if (directory)
                {
                    using (var dialog = new FolderBrowserDialog { SelectedPath = box.Text, ShowNewFolderButton = true })
                        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
                else
                {
                    using (var dialog = new OpenFileDialog { Filter = L("Programs|*.exe;*.bat;*.cmd|All files|*.*", "程序|*.exe;*.bat;*.cmd|所有文件|*.*"), CheckFileExists = true })
                        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
                }
            };
            AddRow(fields, label, panel);
            saveActions.Add(delegate { save(box.Text.Trim()); });
        }

        private void BindList(TableLayoutPanel fields, string label, List<string> values, Action<List<string>> save)
        {
            BindText(fields, label, String.Join(Environment.NewLine, values), delegate(string value) { save(SplitList(value)); }, true);
        }

        private static List<string> SplitList(string value)
        {
            var result = new List<string>();
            foreach (string item in value.Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (!result.Contains(item)) result.Add(item);
            return result;
        }

        private void BuildGeneral()
        {
            var fields = AddPage(L("General", "常规"));
            BindPath(fields, L("Download folder", "下载目录"), working.DownloadDirectory, true, delegate(string value) { working.DownloadDirectory = value; });
            BindPath(fields, L("Temporary folder", "临时目录"), working.TemporaryDirectory, true, delegate(string value) { working.TemporaryDirectory = value; });
            BindChoice(fields, L("Language (restart required)", "语言（重启后生效）"), new[] { "English", "简体中文" }, working.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 0, delegate(int value) { working.Language = value == 1 ? "zh-CN" : "en"; });
            BindChoice(fields, L("Theme", "主题"), new[] { L("System", "跟随系统"), L("Light", "浅色"), L("Dark", "深色") }, working.AppearanceTheme == "dark" ? 2 : working.AppearanceTheme == "light" ? 1 : 0, delegate(int value) { working.AppearanceTheme = value == 2 ? "dark" : value == 1 ? "light" : "system"; });
            BindNumber(fields, L("Interface font size (pt)", "界面字号（pt）"), (decimal)working.UiFontSize, 8, 20, delegate(decimal value) { working.UiFontSize = (float)value; });
            BindCheck(fields, L("Center download dialogs (otherwise remember position)", "下载窗口居中（关闭后记住窗口位置）"), working.CenterPopups, value => working.CenterPopups = value);
            BindCheck(fields, L("Start with Windows", "登录 Windows 时启动"), working.StartWithWindows, delegate(bool value) { working.StartWithWindows = value; });
            BindCheck(fields, L("Start minimized", "启动时最小化"), working.StartMinimized, delegate(bool value) { working.StartMinimized = value; });
            BindCheck(fields, L("Close to notification area", "关闭窗口时保留在通知区域"), working.CloseToTray, delegate(bool value) { working.CloseToTray = value; });
            BindCheck(fields, L("Notify when a download starts", "显示下载开始通知"), working.NotifyStarted, value => working.NotifyStarted = value);
            BindCheck(fields, L("Notify when a download completes", "显示下载完成通知"), working.Notifications, delegate(bool value) { working.Notifications = value; });
            BindCheck(fields, L("Animate the icon during transfers", "传输时显示图标动画"), working.AnimateIcon, value => working.AnimateIcon = value);
            BindCheck(fields, L("Monitor the clipboard for links", "监视剪贴板中的链接"), working.ClipboardEnabled, delegate(bool value) { working.ClipboardEnabled = value; });
            BindCheck(fields, L("Clipboard: capture web pages too (high sensitivity)", "剪贴板：同时捕获网页链接（高灵敏度）"), working.ClipboardHighSensitivity, delegate(bool value) { working.ClipboardHighSensitivity = value; });
            AddRow(fields, "", new Label { AutoSize = true, Text = L("Low sensitivity uses the file extensions on the Browser page and torrent links.", "低灵敏度只识别“浏览器”页设置的文件扩展名和种子链接。") });
            BindCheck(fields, L("Start new downloads automatically", "自动开始新建的下载"), working.AutoStartDownloads, delegate(bool value) { working.AutoStartDownloads = value; });
            BindCheck(fields, L("Start unfinished downloads when Qing starts", "程序启动时开始未完成任务"), working.AutoStartOnLaunch, delegate(bool value) { working.AutoStartOnLaunch = value; });
            BindCheck(fields, L("Prevent sleep while downloading", "下载时阻止计算机睡眠"), working.PreventSleep, delegate(bool value) { working.PreventSleep = value; });
            BindCheck(fields, L("Check for updates automatically", "自动检查更新"), working.CheckUpdates, value => working.CheckUpdates = value);
            BindNumber(fields, L("Check every (days)", "检查间隔（天）"), working.UpdateCheckDays, 1, 365, value => working.UpdateCheckDays = (int)value);
            var restoreUpdate = new Button { Text = L("Show skipped versions again", "恢复已跳过版本的提示"), AutoSize = true };
            restoreUpdate.Click += delegate { working.SkippedUpdateVersion = ""; working.LastUpdateCheckUtc = null; restoreUpdate.Text = L("Ready to apply", "待应用"); };
            AddRow(fields, "", restoreUpdate);
        }

        private void BuildDownloads()
        {
            var fields = AddPage(L("Downloads", "下载"));
            BindNumber(fields, L("Concurrent downloads", "同时下载数"), working.MaxConcurrent, 1, 128, delegate(decimal value) { working.MaxConcurrent = (int)value; });
            BindNumber(fields, L("Connections per download", "每个下载的连接数"), working.Connections, 1, 16, delegate(decimal value) { working.Connections = (int)value; });
            BindNumber(fields, L("Speed limit (KiB/s, 0 = none)", "限速（KiB/s，0 为不限速）"), working.SpeedLimit / 1024M, 0, 1000000, delegate(decimal value) { working.SpeedLimit = (long)value * 1024; });
            BindNumber(fields, L("Retries", "重试次数"), working.RetryCount, 0, 100, delegate(decimal value) { working.RetryCount = (int)value; });
            BindNumber(fields, L("Retry delay (seconds)", "重试等待（秒）"), working.RetryDelaySeconds, 1, 86400, delegate(decimal value) { working.RetryDelaySeconds = (int)value; });
            BindNumber(fields, L("Connection timeout (seconds)", "连接超时（秒）"), working.ConnectionTimeoutSeconds, 1, 600, delegate(decimal value) { working.ConnectionTimeoutSeconds = (int)value; });
            BindCheck(fields, L("Use passive FTP (PASV)", "FTP 使用被动模式（PASV）"), working.FtpPassive, delegate(bool value) { working.FtpPassive = value; });
            string[] actions = { L("Choose another filename", "自动重命名"), L("Overwrite", "覆盖"), L("Ask me", "询问"), L("Skip", "跳过") };
            BindChoice(fields, L("If a file already exists", "文件已存在时"), actions, (int)working.ExistingFileAction, delegate(int value) { working.ExistingFileAction = (ExistingFileAction)value; });
            BindChoice(fields, L("Existing files in batches", "批量下载遇到同名文件时"), actions, (int)working.ExistingBatchFileAction, delegate(int value) { working.ExistingBatchFileAction = (ExistingFileAction)value; });
            BindCheck(fields, L("Use the server's file timestamp", "使用服务器文件时间"), working.UseServerTimestamp, delegate(bool value) { working.UseServerTimestamp = value; });
            BindCheck(fields, L("Check for duplicate download links", "检查重复的下载链接"), working.CheckDuplicateDownloads, delegate(bool value) { working.CheckDuplicateDownloads = value; });
            BindCheck(fields, L("Show download progress window", "显示下载进度窗口"), working.ShowProgressWindow, delegate(bool value) { working.ShowProgressWindow = value; });
            BindCheck(fields, L("Pre-download ordinary HTTP(S) GET files in the add window", "在添加窗口中预下载普通 HTTP(S) GET 文件"), working.PreDownloadInAddDialog, delegate(bool value) { working.PreDownloadInAddDialog = value; });
            BindCheck(fields, L("Show resolved direct URLs", "显示已解析的下载直链"), working.ShowDirectUrls, delegate(bool value) { working.ShowDirectUrls = value; });
            BindCheck(fields, L("Close progress window after downloads finish", "下载完成后关闭进度窗口"), working.CloseProgressAfterDownload, delegate(bool value) { working.CloseProgressAfterDownload = value; });
            BindNumber(fields, L("Progress close delay (seconds)", "关闭进度窗口前等待（秒）"), working.CloseProgressDelaySeconds, 0, 3600, delegate(decimal value) { working.CloseProgressDelaySeconds = (int)value; });
            BindCheck(fields, L("Show download complete dialog", "显示下载完成窗口"), working.ShowCompleteDialog, delegate(bool value) { working.ShowCompleteDialog = value; });
            BindCheck(fields, L("Remove completed tasks; keep files", "下载完成后移除任务，保留文件"), working.RemoveCompletedTasks, delegate(bool value) { working.RemoveCompletedTasks = value; });
            BindCheck(fields, L("Keep download history", "保留下载历史"), working.KeepHistory, delegate(bool value) { working.KeepHistory = value; });
            BindNumber(fields, L("History retention (days)", "历史保留天数"), working.HistoryDays, 1, 3650, delegate(decimal value) { working.HistoryDays = (int)value; });
            BindNumber(fields, L("Maximum history entries", "历史记录上限"), working.HistoryLimit, 1, 1000000, delegate(decimal value) { working.HistoryLimit = (int)value; });
        }

        private void BuildBrowser()
        {
            var fields = AddPage(L("Browser", "浏览器"));
            BindCheck(fields, L("Capture browser downloads", "接管浏览器下载"), working.CaptureEnabled, delegate(bool value) { working.CaptureEnabled = value; });
            BindCheck(fields, L("Detect video and audio", "检测视频和音频"), working.MediaEnabled, delegate(bool value) { working.MediaEnabled = value; });
            BindList(fields, L("Capture file extensions", "接管的文件扩展名"), working.Extensions, delegate(List<string> value) { working.Extensions = value; });
            BindList(fields, L("Excluded file extensions", "排除的文件扩展名"), working.ExcludedExtensions, delegate(List<string> value) { working.ExcludedExtensions = value; });
            BindList(fields, L("Exclude websites from file capture", "普通文件捕获排除的网站"), working.ExcludedHosts, delegate(List<string> value) { working.ExcludedHosts = value; });
            BindList(fields, L("Exclude websites from media detection", "媒体检测排除的网站"), working.MediaExcludedHosts, delegate(List<string> value) { working.MediaExcludedHosts = value; });
            AddRow(fields, "", new Label { Text = L("Enter one item per line, or separate items with commas.", "每行填写一项，也可使用逗号分隔。"), AutoSize = true });
        }

        private void BuildMedia()
        {
            var fields = AddPage(L("Video and audio", "视频与音频"));
            BindNumber(fields, L("YouTube fragment concurrency", "YouTube 分片并发数"), working.Media.YouTubeConnections, 1, 64, delegate(decimal value) { working.Media.YouTubeConnections = (int)value; });
            AddRow(fields, "", new Label { AutoSize = true, Text = L("Applies to segmented YouTube media. Single-connection site rules take priority.", "仅用于 YouTube 分片媒体；网站的单连接规则优先。") });
            var formatValues = new List<string> { "bestvideo+bestaudio/best", "bestvideo[height<=1080]+bestaudio/best[height<=1080]", "bestvideo[height<=720]+bestaudio/best[height<=720]", "best", "bestaudio/best" };
            var formatLabels = new List<string> { L("Best available", "最佳画质"), "1080p", "720p", L("Best combined video and audio", "最佳单文件音视频"), L("Audio only", "仅音频") };
            int formatIndex = formatValues.IndexOf(working.Media.VideoFormat);
            if (formatIndex < 0) { formatIndex = formatValues.Count; formatValues.Add(working.Media.VideoFormat); formatLabels.Add(L("Current custom format", "当前自定义格式")); }
            BindChoice(fields, L("Default quality", "默认画质"), formatLabels.ToArray(), formatIndex, delegate(int value) { working.Media.VideoFormat = formatValues[value]; });
            var audioFormats = new List<string> { "mp3", "m4a", "opus", "wav", "flac", "best" };
            if (!audioFormats.Contains(working.Media.AudioFormat)) audioFormats.Add(working.Media.AudioFormat);
            BindChoice(fields, L("Audio output", "音频输出格式"), audioFormats.ToArray(), audioFormats.IndexOf(working.Media.AudioFormat), delegate(int value) { working.Media.AudioFormat = audioFormats[value]; });
            BindNumber(fields, L("Audio bitrate (kbps)", "音频码率（kbps）"), working.Media.AudioBitrate, 32, 512, delegate(decimal value) { working.Media.AudioBitrate = (int)value; });
            BindText(fields, L("Preferred audio language", "首选音轨语言"), working.Media.AudioLanguage, delegate(string value) { working.Media.AudioLanguage = value.Trim(); }, false);
            BindText(fields, L("Subtitle language", "字幕语言"), working.Media.SubtitleLanguage, delegate(string value) { working.Media.SubtitleLanguage = value.Trim(); }, false);
            BindCheck(fields, L("Save subtitles", "保存字幕"), working.Media.SaveSubtitles, delegate(bool value) { working.Media.SaveSubtitles = value; });
            BindCheck(fields, L("Save thumbnail", "保存缩略图"), working.Media.SaveThumbnail, delegate(bool value) { working.Media.SaveThumbnail = value; });
            BindCheck(fields, L("Embed title and other metadata", "嵌入标题等媒体信息"), working.Media.EmbedMetadata, delegate(bool value) { working.Media.EmbedMetadata = value; });
            BindCheck(fields, L("Embed cover image", "嵌入封面"), working.Media.EmbedThumbnail, delegate(bool value) { working.Media.EmbedThumbnail = value; });
            BindCheck(fields, L("Embed subtitles", "嵌入字幕"), working.Media.EmbedSubtitles, delegate(bool value) { working.Media.EmbedSubtitles = value; });
            BindCheck(fields, L("Read media information after downloading", "下载后读取媒体文件信息"), working.Media.ReadDownloadedInformation, delegate(bool value) { working.Media.ReadDownloadedInformation = value; });
            BindCheck(fields, L("Animate video in the progress window (new downloads)", "在进度窗显示下载中视频画面（新任务生效）"), working.Media.AnimateDownload, value => working.Media.AnimateDownload = value);
            BindCheck(fields, L("Hide automatically translated audio", "隐藏自动翻译的音轨"), working.Media.HideAutoTranslatedAudio, delegate(bool value) { working.Media.HideAutoTranslatedAudio = value; });
            BindNumber(fields, L("Minimum media size (KiB)", "媒体大小下限（KiB）"), working.Media.MinimumSizeKb, 0, 100000000, delegate(decimal value) { working.Media.MinimumSizeKb = (int)value; });
            BindNumber(fields, L("Minimum duration (seconds)", "时长下限（秒）"), working.Media.MinimumDurationSeconds, 0, 86400, delegate(decimal value) { working.Media.MinimumDurationSeconds = (int)value; });
            BindText(fields, L("Media filename pattern", "媒体文件名格式"), working.Media.FilenameTemplate, delegate(string value) { working.Media.FilenameTemplate = value; }, false);
            AddRow(fields, "", new Label { Text = L("Examples: %(title)s.%(ext)s or %(title)s [%(id)s].%(ext)s", "示例：%(title)s.%(ext)s 或 %(title)s [%(id)s].%(ext)s"), AutoSize = true });
            BindCheck(fields, L("Convert downloaded WebP images to JPEG", "下载后将 WebP 图片转换为 JPEG"), working.Media.ConvertWebPToJpeg, delegate(bool value) { working.Media.ConvertWebPToJpeg = value; });
            BindCheck(fields, L("Convert downloaded AVIF images to JPEG", "下载后将 AVIF 图片转换为 JPEG"), working.Media.ConvertAvifToJpeg, delegate(bool value) { working.Media.ConvertAvifToJpeg = value; });
        }

        private void BuildSites()
        {
            var page = new TabPage(L("Network and sites", "网络与站点")) { Padding = new Padding(12) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            BindText(fields, L("Proxy URL", "代理地址"), working.Proxy, delegate(string value) { working.Proxy = value.Trim(); }, false);
            root.Controls.Add(fields, 0, 0);
            root.Controls.Add(new Label { Text = L("Site-specific proxy and sign-in. Use host names such as example.com or *.example.com. Select a row and press Delete to remove it.", "可按站点设置代理和登录信息。域名示例：example.com 或 *.example.com。选中整行后按 Delete 删除。"), AutoSize = true, Margin = new Padding(3, 10, 3, 10) }, 0, 1);
            SetupGrid(sitesGrid);
            sitesGrid.Columns.Add("Host", L("Domain", "域名"));
            sitesGrid.Columns.Add("Proxy", L("Proxy URL", "代理地址"));
            sitesGrid.Columns.Add("Username", L("Username", "用户名"));
            sitesGrid.Columns.Add("Password", L("Password", "密码"));
            sitesGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Single", HeaderText = L("One connection", "单连接") });
            foreach (var rule in working.SiteRules) sitesGrid.Rows.Add(rule.HostPattern, rule.Proxy, rule.Username, rule.Password, rule.SingleConnection);
            sitesGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs args)
            {
                if (args.ColumnIndex == 3 && args.Value != null && args.Value.ToString().Length > 0) { args.Value = "••••••••"; args.FormattingApplied = true; }
            };
            sitesGrid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs args)
            {
                var editor = args.Control as TextBox;
                if (editor != null) editor.UseSystemPasswordChar = sitesGrid.CurrentCell.ColumnIndex == 3;
            };
            root.Controls.Add(sitesGrid, 0, 2);
            page.Controls.Add(root);
            tabs.TabPages.Add(page);
        }

        private void BuildCategories()
        {
            var page = new TabPage(L("Categories", "分类")) { Padding = new Padding(12) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = L("Match file extensions (* = all). Websites, when specified, further restrict the match to those domains and subdomains. Separate entries with commas. Select a row and press Delete to remove it.", "按扩展名匹配（* 表示全部类型）；填写网站后，仅匹配这些域名及子域名中的文件。多项使用逗号分隔。选中整行后按 Delete 删除。"), AutoSize = true, Margin = new Padding(3, 3, 3, 10) }, 0, 0);
            SetupGrid(categoriesGrid);
            categoriesGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = L("Enabled", "启用"), FillWeight = 40 });
            categoriesGrid.Columns.Add("Name", L("Name", "名称"));
            categoriesGrid.Columns.Add("Extensions", L("Extensions", "扩展名"));
            categoriesGrid.Columns.Add("Hosts", L("Websites", "网站域名"));
            categoriesGrid.Columns.Add("Directory", L("Folder", "目录"));
            foreach (var category in working.Categories) AddCategoryRow(category);
            categoriesGrid.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs args) { args.Row.Cells[0].Value = true; };
            root.Controls.Add(categoriesGrid, 0, 1);
            var browse = new Button { Text = L("Choose folder for selected category…", "选择当前分类的目录…"), AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            browse.Click += delegate
            {
                if (categoriesGrid.CurrentRow == null || categoriesGrid.CurrentRow.IsNewRow) return;
                using (var dialog = new FolderBrowserDialog { SelectedPath = CellText(categoriesGrid.CurrentRow, 4), ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) categoriesGrid.CurrentRow.Cells[4].Value = dialog.SelectedPath;
            };
            var restore = new Button { Text = L("Restore selected default extensions", "恢复所选默认扩展名"), AutoSize = true };
            restore.Click += delegate
            {
                DataGridViewRow row = categoriesGrid.CurrentRow;
                if (row == null || row.IsNewRow) return;
                foreach (CategorySettings category in CategorySettings.Defaults(working.Language))
                    if (category.DefaultId == Convert.ToString(row.Tag)) row.Cells[2].Value = String.Join(", ", category.Extensions);
            };
            categoriesGrid.CurrentCellChanged += delegate { restore.Enabled = categoriesGrid.CurrentRow != null && !categoriesGrid.CurrentRow.IsNewRow && !String.IsNullOrEmpty(Convert.ToString(categoriesGrid.CurrentRow.Tag)); };
            restore.Enabled = categoriesGrid.CurrentRow != null && !String.IsNullOrEmpty(Convert.ToString(categoriesGrid.CurrentRow.Tag));
            var addDefaults = new Button { Text = L("Add missing default categories", "添加缺少的默认分类"), AutoSize = true };
            addDefaults.Click += delegate
            {
                foreach (CategorySettings category in CategorySettings.Defaults(working.Language))
                {
                    bool exists = false;
                    foreach (DataGridViewRow row in categoriesGrid.Rows)
                        if (!row.IsNewRow && (Convert.ToString(row.Tag) == category.DefaultId || String.Equals(CellText(row, 1), category.Name, StringComparison.OrdinalIgnoreCase))) { exists = true; break; }
                    if (!exists) AddCategoryRow(category);
                }
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            actions.Controls.Add(browse); actions.Controls.Add(restore); actions.Controls.Add(addDefaults);
            root.Controls.Add(actions, 0, 2);
            page.Controls.Add(root);
            tabs.TabPages.Add(page);
        }

        private void AddCategoryRow(CategorySettings category)
        {
            int index = categoriesGrid.Rows.Add(category.Enabled, category.Name, String.Join(", ", category.Extensions), String.Join(", ", category.Hosts), category.Directory);
            categoriesGrid.Rows[index].Tag = category.DefaultId;
        }

        private static void SetupGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AllowUserToAddRows = true;
            grid.AllowUserToDeleteRows = true;
            grid.MultiSelect = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.RowHeadersWidth = 28;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        }

        private void BuildQueues()
        {
            var page = new TabPage(L("Queues", "队列")) { Padding = new Padding(12) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            queuesList.Dock = DockStyle.Fill;
            queuesList.IntegralHeight = false;
            root.Controls.Add(queuesList, 0, 0);
            RefreshQueues();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var add = new Button { Text = L("Add…", "添加…"), AutoSize = true };
            var edit = new Button { Text = L("Edit…", "编辑…"), AutoSize = true };
            var remove = new Button { Text = L("Remove", "删除"), AutoSize = true };
            add.Click += delegate
            {
                using (var dialog = new QueueDialog(new QueueSettings { Name = L("New queue", "新队列") }, working.Language))
                    if (dialog.ShowDialog(this) == DialogResult.OK) { working.Queues.Add(dialog.Result); RefreshQueues(); queuesList.SelectedIndex = working.Queues.Count - 1; }
            };
            edit.Click += delegate { EditQueue(); };
            queuesList.DoubleClick += delegate { EditQueue(); };
            remove.Click += delegate
            {
                if (queuesList.SelectedIndex >= 0) { working.Queues.RemoveAt(queuesList.SelectedIndex); RefreshQueues(); }
            };
            buttons.Controls.Add(add);
            buttons.Controls.Add(edit);
            buttons.Controls.Add(remove);
            root.Controls.Add(buttons, 0, 1);
            page.Controls.Add(root);
            tabs.TabPages.Add(page);
        }

        private void RefreshQueues()
        {
            queuesList.DataSource = null;
            queuesList.DataSource = working.Queues;
        }

        private void EditQueue()
        {
            int index = queuesList.SelectedIndex;
            if (index < 0) return;
            using (var dialog = new QueueDialog(working.Queues[index], working.Language))
                if (dialog.ShowDialog(this) == DialogResult.OK) { working.Queues[index] = dialog.Result; RefreshQueues(); queuesList.SelectedIndex = index; }
        }

        private void BuildTorrent()
        {
            var fields = AddPage(L("BitTorrent", "BT 下载"));
            BindCheck(fields, L("Enable BitTorrent downloads", "启用 BT 下载"), working.TorrentEnabled, delegate(bool value) { working.TorrentEnabled = value; });
            BindCheck(fields, L("Check .torrent association on startup", "启动时检查 .torrent 文件关联"), working.CheckTorrentAssociation, value => working.CheckTorrentAssociation = value);
            BindCheck(fields, L("Continue seeding after download", "下载后继续做种"), working.SeedAfterDownload, delegate(bool value) { working.SeedAfterDownload = value; });
            BindCheck(fields, L("Discover peers through DHT", "通过 DHT 发现节点"), working.DhtEnabled, delegate(bool value) { working.DhtEnabled = value; });
            BindCheck(fields, L("Discover local peers", "发现局域网节点"), working.LocalPeerDiscovery, delegate(bool value) { working.LocalPeerDiscovery = value; });
            BindCheck(fields, L("Request port mapping with UPnP", "使用 UPnP 映射端口"), working.UpnpEnabled, delegate(bool value) { working.UpnpEnabled = value; });
        }

        private void BuildAutomation()
        {
            var fields = AddPage(L("After download", "下载后操作"));
            BindPath(fields, L("Virus scanner", "病毒扫描程序"), working.ScanCommand, false, delegate(string value) { working.ScanCommand = value; });
            BindText(fields, L("Scanner arguments", "扫描参数"), working.ScanArguments, delegate(string value) { working.ScanArguments = value; }, false);
            BindList(fields, L("Scan these extensions", "扫描的文件扩展名"), working.ScanExtensions, delegate(List<string> value) { working.ScanExtensions = value; });
            BindPath(fields, L("Run command", "执行程序"), working.CompletionCommand, false, delegate(string value) { working.CompletionCommand = value; });
            BindText(fields, L("Command arguments", "程序参数"), working.CompletionArguments, delegate(string value) { working.CompletionArguments = value; }, false);
            AddRow(fields, "", new Label { Text = L("Scanner arguments support {file}. The completion command runs once when all downloads finish.", "扫描参数中的 {file} 表示文件路径；完成命令在全部下载完成后执行一次。"), AutoSize = true });
            BindChoice(fields, L("When all downloads finish", "全部下载完成后"), new[] { L("Do nothing", "不执行操作"), L("Exit application", "退出程序"), L("Shut down computer", "关闭计算机"), L("Sleep", "睡眠"), L("Hibernate", "休眠") }, (int)working.CompletionAction, delegate(int value) { working.CompletionAction = (CompletionAction)value; });
        }

        private void BuildSounds()
        {
            var fields = AddPage(L("Sounds", "声音"));
            BindCheck(fields, L("Enable sounds", "启用声音"), working.SoundsEnabled, value => working.SoundsEnabled = value);
            AddRow(fields, "", new Label { AutoSize = true, Text = L("Leave the file blank to use a Windows system sound. Preview plays even when sounds are disabled.", "文件留空时使用 Windows 系统声音；试听不受声音开关影响。") });
            BindSound(fields, L("Download added", "添加下载"), DownloadSound.Added, working.SoundAddedEnabled, working.SoundAddedFile, (enabled, file) => { working.SoundAddedEnabled = enabled; working.SoundAddedFile = file; });
            BindSound(fields, L("Download finished", "下载完成"), DownloadSound.Finished, working.SoundFinishedEnabled, working.SoundFinishedFile, (enabled, file) => { working.SoundFinishedEnabled = enabled; working.SoundFinishedFile = file; });
            BindSound(fields, L("Download failed", "下载失败"), DownloadSound.Error, working.SoundErrorEnabled, working.SoundErrorFile, (enabled, file) => { working.SoundErrorEnabled = enabled; working.SoundErrorFile = file; });
            BindSound(fields, L("All downloads finished", "全部下载完成"), DownloadSound.AllFinished, working.SoundAllFinishedEnabled, working.SoundAllFinishedFile, (enabled, file) => { working.SoundAllFinishedEnabled = enabled; working.SoundAllFinishedFile = file; });
        }

        private void BindSound(TableLayoutPanel fields, string label, DownloadSound sound, bool enabled, string file, Action<bool, string> save)
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 4 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var check = new CheckBox { Checked = enabled, AutoSize = true, AccessibleName = label };
            var path = new TextBox { Text = file, Dock = DockStyle.Fill, AccessibleName = label + L(" sound file", "声音文件") };
            var browse = new Button { Text = L("Browse…", "浏览…"), AutoSize = true };
            var preview = new Button { Text = L("Preview", "试听"), AutoSize = true };
            browse.Click += delegate
            {
                using (var dialog = new OpenFileDialog { Filter = L("Wave sounds|*.wav", "WAV 声音|*.wav"), CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
            };
            preview.Click += delegate
            {
                if (!SoundAlerts.Preview(path.Text.Trim(), sound)) MessageBox.Show(this, L("The sound could not be played. Choose a readable WAV file.", "无法播放此声音，请选择可读取的 WAV 文件。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            row.Controls.Add(check, 0, 0); row.Controls.Add(path, 1, 0); row.Controls.Add(browse, 2, 0); row.Controls.Add(preview, 3, 0);
            AddRow(fields, label, row);
            saveActions.Add(delegate { save(check.Checked, path.Text.Trim()); });
        }

        private static string CellText(DataGridViewRow row, int column)
        {
            return Convert.ToString(row.Cells[column].Value).Trim();
        }

        private bool ReadWorking()
        {
            sitesGrid.EndEdit();
            categoriesGrid.EndEdit();
            var sites = new List<SiteRule>();
            foreach (DataGridViewRow row in sitesGrid.Rows)
            {
                if (row.IsNewRow) continue;
                string host = CellText(row, 0);
                if (host.Length == 0)
                {
                    MessageBox.Show(this, L("Enter a domain for each site rule.", "请为每条站点规则填写域名。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                sites.Add(new SiteRule { HostPattern = host, Proxy = CellText(row, 1), Username = Convert.ToString(row.Cells[2].Value), Password = Convert.ToString(row.Cells[3].Value), SingleConnection = Convert.ToBoolean(row.Cells[4].Value) });
            }
            var categories = new List<CategorySettings>();
            foreach (DataGridViewRow row in categoriesGrid.Rows)
            {
                if (row.IsNewRow) continue;
                string name = CellText(row, 1);
                if (name.Length == 0)
                {
                    MessageBox.Show(this, L("Enter a name for each category.", "请为每个分类填写名称。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                categories.Add(new CategorySettings { DefaultId = Convert.ToString(row.Tag), Enabled = Convert.ToBoolean(row.Cells[0].Value), Name = name, Extensions = SplitList(CellText(row, 2)), Hosts = SplitList(CellText(row, 3)), Directory = CellText(row, 4) });
            }
            foreach (var save in saveActions) save();
            if (String.IsNullOrWhiteSpace(working.DownloadDirectory))
            {
                MessageBox.Show(this, L("Choose a download folder.", "请选择下载目录。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            working.SiteRules = sites;
            working.Categories = categories;
            return true;
        }

        private void ApplyChanges(bool close)
        {
            if (!ReadWorking()) return;
            AppSettings candidate = working.Copy();
            try
            {
                if (ApplyRequested != null) ApplyRequested(candidate.Copy());
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Result = candidate;
            status.Text = L("Applied", "已应用");
            if (close) { DialogResult = DialogResult.OK; Close(); }
            else { Appearance.Configure(candidate); Appearance.Apply(this); }
        }

        private void ResetBasic()
        {
            if (!ReadWorking()) return;
            int selected = tabs.SelectedIndex;
            working = new AppSettings {
                DownloadDirectory = working.DownloadDirectory, TemporaryDirectory = working.TemporaryDirectory, FileTypeDirectories = working.FileTypeDirectories,
                Proxy = working.Proxy, SiteRules = working.SiteRules, DebridAccounts = working.DebridAccounts,
                Categories = working.Categories, Queues = working.Queues
            };
            BuildPages();
            tabs.SelectedIndex = selected;
            status.Text = L("Defaults ready to apply", "默认值待应用");
            Appearance.Apply(this);
        }
    }
}
