using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class MediaDialog : Form
    {
        private readonly AppSettings settings;
        private MediaInfo inspectedInfo;
        private readonly bool chinese;
        private readonly TextBox urlBox = new TextBox();
        private readonly TextBox directoryBox = new TextBox();
        private readonly ComboBox formatBox = new ComboBox();
        private readonly ComboBox videoTrack = new ComboBox();
        private readonly ComboBox audioTrack = new ComboBox();
        private readonly ComboBox container = new ComboBox();
        private readonly ComboBox audioFormat = new ComboBox();
        private readonly ComboBox audioBitrate = new ComboBox();
        private readonly ComboBox subtitleLanguage = new ComboBox();
        private readonly ComboBox queueBox = new ComboBox();
        private readonly CheckBox videoBox = new CheckBox();
        private readonly CheckBox audioBox = new CheckBox();
        private readonly CheckBox thumbnailBox = new CheckBox();
        private readonly CheckBox subtitlesBox = new CheckBox();
        private readonly CheckBox exactBox = new CheckBox();
        private readonly CheckBox prefixBox = new CheckBox();
        private readonly CheckBox rangeBox = new CheckBox();
        private readonly CheckBox startBox = new CheckBox();
        private readonly NumericUpDown rangeStart = new NumericUpDown { Minimum = 1, Maximum = 1000000, Value = 1, Width = 75 };
        private readonly NumericUpDown rangeEnd = new NumericUpDown { Minimum = 1, Maximum = 1000000, Value = 1, Width = 75 };
        private readonly NumericUpDown connections = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 8, Width = 75 };
        private readonly Label connectionsLabel = new Label();
        private readonly ListView entries = new ListView();
        private readonly Label status = new Label();
        private readonly Label formatHint = new Label();
        private readonly Label audioBitrateHint = new Label();
        private readonly Button inspectButton = new Button();
        private readonly Button addButton = new Button();
        private readonly List<MediaFormatInfo> videoFormats = new List<MediaFormatInfo>();
        private readonly List<MediaFormatInfo> audioFormats = new List<MediaFormatInfo>();
        private readonly int[] heights = { 0, 0, 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320 };
        private CancellationTokenSource inspection;
        private bool playlist;
        private bool? connectionsForYouTube;
        private int maximumIndex;

        public List<DownloadItem> Result { get; private set; }
        public MediaDialog(AppSettings settings) : this(settings, "") { }

        public MediaDialog(AppSettings settings, string initialUrl)
        {
            this.settings = settings.Copy(); chinese = settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            Text = L("Add video or audio", "添加视频或音频"); Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font; StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(800, 680); Size = new Size(1020, 820); ShowInTaskbar = false; MinimizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
            for (int i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
            var address = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            address.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); address.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); address.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            address.Controls.Add(new Label { Text = L("URL / embed code", "地址 / 嵌入代码"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            urlBox.Multiline = true; urlBox.ScrollBars = ScrollBars.Vertical; urlBox.Height = 52;
            urlBox.Text = initialUrl; urlBox.Dock = DockStyle.Fill; address.Controls.Add(urlBox, 1, 0);
            inspectButton.Text = L("Analyze", "解析"); inspectButton.AutoSize = true; inspectButton.Click += Inspect; address.Controls.Add(inspectButton, 2, 0); root.Controls.Add(address, 0, 0);

            var destination = Panel();
            var path = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            directoryBox.Text = settings.DownloadDirectory; directoryBox.Dock = DockStyle.Fill;
            var browse = new Button { Text = L("Browse…", "浏览…"), AutoSize = true };
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { SelectedPath = directoryBox.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) directoryBox.Text = dialog.SelectedPath; };
            path.Controls.Add(directoryBox, 0, 0); path.Controls.Add(browse, 1, 0); AddRow(destination, L("Save to", "保存到"), path);
            queueBox.DropDownStyle = ComboBoxStyle.DropDownList; queueBox.Items.Add(L("Default queue", "默认队列"));
            foreach (QueueSettings queue in settings.Queues) queueBox.Items.Add(queue); queueBox.SelectedIndex = 0;
            startBox.Text = L("Start after adding", "添加后开始下载"); startBox.Checked = settings.AutoStartDownloads; startBox.AutoSize = true;
            var queueRow = Flow(queueBox, startBox); queueBox.Width = 260; AddRow(destination, L("Queue", "队列"), queueRow); root.Controls.Add(destination, 0, 1);
            connectionsLabel.AutoSize = true; connectionsLabel.Anchor = AnchorStyles.Left;
            AddRow(destination, L("Fragments", "分片"), Flow(connections, connectionsLabel));

            var tabs = new TabControl { Dock = DockStyle.Top, Height = 235 };
            var contentPage = new TabPage(L("Download content", "下载内容")) { Padding = new Padding(8) };
            var formatsPage = new TabPage(L("Video and audio tracks", "视频和音频轨道")) { Padding = new Padding(8) };
            var content = Panel(); var tracks = Panel(); contentPage.Controls.Add(content); formatsPage.Controls.Add(tracks); tabs.TabPages.Add(contentPage); tabs.TabPages.Add(formatsPage);
            videoBox.Text = L("Video", "视频"); videoBox.Checked = true;
            audioBox.Text = L("Separate audio file", "独立音频文件"); thumbnailBox.Text = L("Thumbnail", "封面"); thumbnailBox.Checked = settings.Media.SaveThumbnail;
            subtitlesBox.Text = L("Subtitles", "字幕"); subtitlesBox.Checked = settings.Media.SaveSubtitles;
            foreach (CheckBox check in new[] { videoBox, audioBox, thumbnailBox, subtitlesBox }) { check.AutoSize = true; check.CheckedChanged += delegate { UpdateOptions(); }; }
            AddRow(content, L("Save", "保存内容"), Flow(videoBox, audioBox, thumbnailBox, subtitlesBox));
            audioFormat.DropDownStyle = ComboBoxStyle.DropDownList; audioFormat.Items.AddRange(new[] { L("Original audio", "原始音频"), "AAC", "MP3" }); audioFormat.SelectedIndex = settings.Media.AudioFormat == "mp3" ? 2 : settings.Media.AudioFormat == "aac" ? 1 : 0;
            audioBitrate.DropDownStyle = ComboBoxStyle.DropDownList;
            audioFormat.SelectedIndexChanged += delegate { UpdateOptions(); };
            AddRow(content, L("Audio export", "音频输出"), Flow(audioFormat, audioBitrate, new Label { Text = "kbps", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }));
            audioBitrateHint.AutoSize = true; audioBitrateHint.MaximumSize = new Size(820, 60); AddRow(content, "", audioBitrateHint);
            subtitleLanguage.Text = settings.Media.SubtitleLanguage; AddRow(content, L("Subtitle language", "字幕语言"), subtitleLanguage);
            prefixBox.Text = L("Prefix filenames with original playlist numbers", "文件名前加原播放列表序号"); prefixBox.AutoSize = true; AddRow(content, "", prefixBox);
            var contentHint = new Label { Text = L("Video and audio create separate tasks. Uncheck both to save only thumbnails or subtitles.", "同时选择视频和音频会创建独立任务。两者均不选时可仅保存封面或字幕。"), AutoSize = true, MaximumSize = new Size(820, 50) }; AddRow(content, "", contentHint);

            formatBox.DropDownStyle = videoTrack.DropDownStyle = audioTrack.DropDownStyle = container.DropDownStyle = ComboBoxStyle.DropDownList;
            formatBox.Items.Add(L("Default quality", "默认画质")); formatBox.Items.Add(L("Best available", "最佳画质")); foreach (int height in heights.Skip(2)) formatBox.Items.Add(height + "p"); formatBox.SelectedIndex = 0;
            exactBox.Text = L("Exact height; skip unavailable entries", "精确高度；跳过缺少该画质的条目"); exactBox.AutoSize = true;
            AddRow(tracks, L("Quality", "画质"), Flow(formatBox, exactBox));
            AddRow(tracks, L("Video track", "视频轨道"), videoTrack); AddRow(tracks, L("Audio track", "音频轨道"), audioTrack);
            container.Items.AddRange(new[] { L("Automatic container", "自动容器"), "mp4", "mkv", "webm" }); container.SelectedIndex = 0; AddRow(tracks, L("Container", "容器"), container);
            formatHint.AutoSize = true; formatHint.MaximumSize = new Size(820, 45); AddRow(tracks, "", formatHint);
            formatBox.SelectedIndexChanged += delegate { UpdateOptions(); }; videoTrack.SelectedIndexChanged += delegate { UpdateOptions(); }; audioTrack.SelectedIndexChanged += delegate { UpdateOptions(); };
            exactBox.CheckedChanged += delegate { UpdateOptions(); }; ResetTracks(); root.Controls.Add(tabs, 0, 2);

            status.Text = L("Paste a media or playlist URL, or Facebook embed code, then choose Analyze.", "粘贴媒体或播放列表地址，或 Facebook 嵌入代码，然后点击解析。"); status.AutoSize = true; status.MaximumSize = new Size(940, 50); status.Margin = new Padding(3, 6, 3, 6); root.Controls.Add(status, 0, 3);
            entries.Dock = DockStyle.Fill; entries.MinimumSize = new Size(0, 100); entries.View = View.Details; entries.CheckBoxes = true; entries.FullRowSelect = true; entries.HideSelection = false;
            entries.Columns.Add(L("No.", "序号"), 65); entries.Columns.Add(L("Title", "标题"), 440); entries.Columns.Add(L("Address", "地址"), 390);
            entries.ItemChecked += delegate { UpdateAddButton(); }; root.Controls.Add(entries, 0, 4);
            var all = new Button { Text = L("Select all", "全选"), AutoSize = true }; var none = new Button { Text = L("Clear selection", "全不选"), AutoSize = true };
            all.Click += delegate { foreach (ListViewItem row in entries.Items) row.Checked = true; }; none.Click += delegate { foreach (ListViewItem row in entries.Items) row.Checked = false; };
            rangeBox.Text = L("Within original positions", "仅原序号范围内"); rangeBox.AutoSize = true;
            rangeBox.CheckedChanged += delegate { UpdateOptions(); }; rangeStart.ValueChanged += delegate { UpdateAddButton(); }; rangeEnd.ValueChanged += delegate { UpdateAddButton(); };
            root.Controls.Add(Flow(all, none, rangeBox, rangeStart, new Label { Text = "—", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, rangeEnd), 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
            var cancel = new Button { Text = L("Cancel", "取消"), AutoSize = true, DialogResult = DialogResult.Cancel };
            addButton.AutoSize = true; addButton.Click += AddSelected; buttons.Controls.Add(cancel); buttons.Controls.Add(addButton); root.Controls.Add(buttons, 0, 6);
            Controls.Add(root); CancelButton = cancel; AcceptButton = inspectButton;
            Action fitOptions = delegate { tabs.Height = Math.Min((int)(235 * Font.SizeInPoints / 9F), Math.Max(180, (int)(ClientSize.Height * 3F / Font.SizeInPoints))); entries.MinimumSize = new Size(0, 100); };
            ClientSizeChanged += delegate { fitOptions(); }; FontChanged += delegate { fitOptions(); };
            urlBox.TextChanged += delegate { if (inspection == null) { inspectedInfo = null; entries.Items.Clear(); playlist = false; ResetTracks(); UpdateConnections(urlBox.Text.Trim()); UpdateOptions(); AcceptButton = inspectButton; } };
            FormClosing += delegate { if (inspection != null) inspection.Cancel(); }; UpdateOptions();
            UpdateConnections(initialUrl);
            Appearance.Apply(this);
            fitOptions();
        }

        private static TableLayoutPanel Panel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoScroll = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return panel;
        }
        private static FlowLayoutPanel Flow(params Control[] controls) { var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; panel.Controls.AddRange(controls); return panel; }
        private static void AddRow(TableLayoutPanel panel, string label, Control control)
        {
            int row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 8, 5) }, 0, row);
            control.Dock = DockStyle.Fill; panel.Controls.Add(control, 1, row);
        }
        private string L(string en, string zh) { return chinese ? zh : en; }
        private void ResetTracks()
        {
            videoFormats.Clear(); audioFormats.Clear(); videoTrack.Items.Clear(); audioTrack.Items.Clear();
            videoTrack.Items.Add(L("Automatic video track", "自动选择视频轨道"));
            audioTrack.Items.Add(L("Best audio track", "最佳音频轨道")); audioTrack.Items.Add(L("Keep video as supplied (no extra audio)", "保留视频原样（不追加音轨）"));
            videoTrack.SelectedIndex = 0; audioTrack.SelectedIndex = 0;
        }
        private void ShowMedia(MediaInfo info, string url)
        {
            inspectedInfo = info;
            UpdateConnections(url);
            entries.Items.Clear(); ResetTracks();
            foreach (MediaFormatInfo format in info.Formats)
            {
                string label = format.Extension + " · " + format.Resolution + " · " + format.VideoCodec + " / " + format.AudioCodec + " · " + format.Language + " [" + format.Id + "]";
                if (format.VideoCodec != "none" && !String.IsNullOrEmpty(format.VideoCodec)) { videoFormats.Add(format); videoTrack.Items.Add(label); }
                else if (format.AudioCodec != "none" && !String.IsNullOrEmpty(format.AudioCodec)) { audioFormats.Add(format); audioTrack.Items.Add(label + (format.AudioBitrate > 0 ? " · " + format.AudioBitrate.ToString("0.#") + " kbps" : "")); }
            }
            string language = subtitleLanguage.Text; subtitleLanguage.Items.Clear(); foreach (string value in info.SubtitleLanguages) subtitleLanguage.Items.Add(value); subtitleLanguage.Text = language;
            playlist = info.Entries.Count > 0; var source = playlist ? info.Entries : new List<MediaEntryInfo> { new MediaEntryInfo { Title = info.Title, Url = url, Index = 1 } };
            maximumIndex = 0; int fallbackIndex = 0;
            foreach (MediaEntryInfo entry in source)
            {
                fallbackIndex++; if (entry.Index <= 0) entry.Index = fallbackIndex; maximumIndex = Math.Max(maximumIndex, entry.Index);
                var row = new ListViewItem(entry.Index.ToString(CultureInfo.InvariantCulture)) { Tag = entry, Checked = true };
                row.SubItems.Add(String.IsNullOrEmpty(entry.Title) ? entry.Url : entry.Title); row.SubItems.Add(entry.Url); entries.Items.Add(row);
            }
            rangeEnd.Value = Math.Max(1, maximumIndex); rangeStart.Value = 1;
            status.Text = info.Title + " · " + entries.Items.Count + L(" item(s)", " 项"); UpdateOptions();
        }
        private async void Inspect(object sender, EventArgs args)
        {
            if (inspection != null) { inspection.Cancel(); return; }
            List<string> urls = MediaInput.ExtractUrls(urlBox.Text);
            if (urls.Count == 0) { status.Text = L("Enter an HTTP(S) URL or Facebook video embed code.", "请输入 HTTP(S) 地址或 Facebook 视频嵌入代码。"); return; }
            if (urls.Count > 1) { status.Text = L("The code contains multiple video URLs. Paste one video embed at a time.", "代码包含多个视频地址，请每次粘贴一个视频的嵌入代码。"); return; }
            string url = urls[0]; urlBox.Text = url;
            var cancellation = new CancellationTokenSource(); inspection = cancellation; entries.Items.Clear(); ResetTracks();
            urlBox.Enabled = false; inspectButton.Text = L("Stop", "停止"); status.Text = L("Analyzing media…", "正在解析媒体…"); UpdateAddButton();
            try
            {
                MediaInfo info = await MediaTools.InspectAsync(new DownloadItem { Url = url, Kind = "media" }, settings, cancellation.Token);
                if (IsDisposed || Disposing || cancellation.IsCancellationRequested) return;
                ShowMedia(info, url); AcceptButton = addButton;
            }
            catch (OperationCanceledException) { if (!IsDisposed && !Disposing) status.Text = L("Analysis stopped.", "解析已停止。"); }
            catch (Exception error) { if (!IsDisposed && !Disposing) status.Text = L("Unable to analyze: ", "无法解析：") + error.Message; }
            finally
            {
                inspection = null; cancellation.Dispose();
                if (!IsDisposed && !Disposing) { urlBox.Enabled = true; inspectButton.Text = L("Analyze", "解析"); UpdateOptions(); }
            }
        }
        private void UpdateOptions()
        {
            bool automatic = videoTrack.SelectedIndex <= 0;
            videoTrack.Enabled = videoBox.Checked; formatBox.Enabled = videoBox.Checked && automatic;
            exactBox.Enabled = formatBox.Enabled && formatBox.SelectedIndex > 1;
            container.Enabled = videoBox.Checked; audioTrack.Enabled = videoBox.Checked || audioBox.Checked;
            audioFormat.Enabled = audioBox.Checked; audioBitrate.Enabled = audioBox.Checked && audioFormat.SelectedIndex > 0;
            UpdateAudioBitrates();
            subtitleLanguage.Enabled = subtitlesBox.Checked; prefixBox.Enabled = playlist; rangeBox.Enabled = playlist;
            rangeStart.Enabled = rangeEnd.Enabled = playlist && rangeBox.Checked;
            formatHint.Text = L("Fallback prefers the selected height or lower, then the best available. Explicit track IDs keep their selected codecs; containers do not transcode them.", "回退优先所选高度或更低画质，再选可用最佳画质。指定轨道保留所选编码；容器选择不会转码。");
            if (videoTrack.SelectedIndex > 0 && videoFormats.Count >= videoTrack.SelectedIndex && videoFormats[videoTrack.SelectedIndex - 1].AudioCodec != "none")
                formatHint.Text = L("This video already contains audio; an additional audio track is not merged. Separate audio export still uses the audio selection.", "该视频已含音频，不再追加音轨。独立音频输出仍使用所选音轨。");
            UpdateAddButton();
        }
        private void UpdateConnections(string url)
        {
            bool youtube = MediaTools.IsYouTubeUrl(url);
            connections.Maximum = youtube ? 64 : 16;
            if (connectionsForYouTube != youtube) connections.Value = Math.Max(1, Math.Min((int)connections.Maximum, youtube ? settings.Media.YouTubeConnections : settings.Connections));
            connectionsForYouTube = youtube;
            connectionsLabel.Text = youtube ? L("YouTube concurrent fragments (1–64)", "YouTube 并发分片（1–64）") : L("Concurrent fragments (1–16)", "并发分片（1–16）");
        }
        private void UpdateAudioBitrates()
        {
            int previous = audioBitrate.SelectedItem is int ? (int)audioBitrate.SelectedItem : settings.Media.AudioBitrate;
            bool mp3 = audioFormat.SelectedIndex == 2;
            double sourceRate = 0;
            if (!playlist && inspectedInfo != null)
            {
                if (audioTrack.SelectedIndex >= 2 && audioTrack.SelectedIndex - 2 < audioFormats.Count) sourceRate = audioFormats[audioTrack.SelectedIndex - 2].AudioBitrate;
                else if (audioFormats.Count == 1) sourceRate = audioFormats[0].AudioBitrate;
                else if (audioFormats.Count == 0)
                {
                    var audible = inspectedInfo.Formats.Where(format => !String.IsNullOrEmpty(format.AudioCodec) && format.AudioCodec != "none").ToList();
                    if (audible.Count == 1) sourceRate = audible[0].AudioBitrate;
                }
            }
            int[] rates = mp3 ? new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 } : new[] { 64, 96, 128, 192, 320 };
            audioBitrate.Items.Clear();
            foreach (int rate in rates) if (!mp3 || sourceRate <= 0 || rate <= sourceRate) audioBitrate.Items.Add(rate);
            if (audioBitrate.Items.Count > 0)
            {
                audioBitrate.SelectedIndex = 0;
                foreach (int rate in audioBitrate.Items) if (rate <= previous) audioBitrate.SelectedItem = rate;
            }
            audioBitrateHint.Visible = audioBox.Checked && mp3;
            audioBitrateHint.Text = playlist ? L("Playlist MP3 bitrate is an independent re-encoding choice.", "播放列表的 MP3 码率可独立选择重编码。")
                : sourceRate <= 0 ? L("Source audio bitrate is unknown for this selection.", "无法确定当前所选音轨的源码率。")
                : L("Source audio bitrate: ", "源音频码率：") + sourceRate.ToString("0.##", CultureInfo.InvariantCulture) + " kbps. "
                    + (audioBitrate.Items.Count == 0 ? L("No standard MP3 rate fits; choose original audio or another track.", "没有不超出源码率的标准 MP3 值，请选择原始音频或其他音轨。") : L("MP3 choices do not exceed the source.", "MP3 可选值不高于源码率。"));
        }
        private bool InRange(ListViewItem row)
        {
            int index = ((MediaEntryInfo)row.Tag).Index;
            return !playlist || !rangeBox.Checked || index >= rangeStart.Value && index <= rangeEnd.Value;
        }
        private void UpdateAddButton()
        {
            int count = entries.CheckedItems.Cast<ListViewItem>().Count(InRange);
            bool content = videoBox.Checked || audioBox.Checked || thumbnailBox.Checked || subtitlesBox.Checked;
            addButton.Enabled = inspection == null && count > 0 && content && (!audioBox.Checked || audioFormat.SelectedIndex == 0 || audioBitrate.SelectedItem != null);
            addButton.Text = L("Add selected", "添加所选项") + " (" + count + ")";
        }
        private string VideoSelector()
        {
            string audio = audioTrack.SelectedIndex >= 2 ? audioFormats[audioTrack.SelectedIndex - 2].Id : audioTrack.SelectedIndex == 1 ? "" : "bestaudio";
            if (videoTrack.SelectedIndex > 0)
            {
                MediaFormatInfo video = videoFormats[videoTrack.SelectedIndex - 1];
                return video.Id + (video.AudioCodec == "none" && audio.Length > 0 ? "+" + audio : "");
            }
            string filter = formatBox.SelectedIndex > 1 ? "[height" + (exactBox.Checked ? "=" : "<=") + heights[formatBox.SelectedIndex] + "]" : "";
            if (audioTrack.SelectedIndex >= 2) return "bestvideo" + filter + "+" + audio + (filter.Length > 0 && !exactBox.Checked ? "/bestvideo+" + audio : "");
            if (audioTrack.SelectedIndex == 1) return "bestvideo" + filter + "/best" + filter + (filter.Length > 0 && !exactBox.Checked ? "/bestvideo/best" : "");
            return formatBox.SelectedIndex == 0 ? settings.Media.VideoFormat : formatBox.SelectedIndex == 1 ? "bestvideo+bestaudio/best" : "";
        }
        private DownloadItem CreateItem(MediaEntryInfo entry)
        {
            var queue = queueBox.SelectedItem as QueueSettings;
            return new DownloadItem { Url = entry.Url, LinkText = entry.Title, MediaTitle = entry.Title,
                ThumbnailUrl = !playlist && inspectedInfo != null ? inspectedInfo.Thumbnail : "", MediaDurationSeconds = !playlist && inspectedInfo != null ? inspectedInfo.Duration : 0,
                MediaViewCount = !playlist && inspectedInfo != null ? inspectedInfo.ViewCount : null,
                Kind = "media", Directory = directoryBox.Text.Trim(), DirectoryExplicit = true, QueueId = queue == null ? "" : queue.Id,
                SaveSubtitles = subtitlesBox.Checked, SaveThumbnail = thumbnailBox.Checked, MediaSubtitleLanguage = subtitleLanguage.Text.Trim(),
                MediaFileNamePrefix = playlist && prefixBox.Checked ? entry.Index.ToString("D" + Math.Max(1, maximumIndex.ToString(CultureInfo.InvariantCulture).Length), CultureInfo.InvariantCulture) + " - " : "",
                Connections = (int)connections.Value, State = startBox.Checked ? DownloadState.Queued : DownloadState.Paused };
        }
        private void AddSelected(object sender, EventArgs args)
        {
            if (inspection != null || entries.CheckedItems.Count == 0) return;
            if (String.IsNullOrWhiteSpace(directoryBox.Text) || !Path.IsPathRooted(directoryBox.Text)) { status.Text = L("Choose an absolute download folder.", "请选择完整下载目录。"); return; }
            if (playlist && rangeBox.Checked && rangeStart.Value > rangeEnd.Value) { status.Text = L("The range end must not precede its start.", "范围结束序号不能小于开始序号。"); return; }
            var result = new List<DownloadItem>();
            foreach (ListViewItem row in entries.CheckedItems)
            {
                if (!InRange(row)) continue; MediaEntryInfo entry = (MediaEntryInfo)row.Tag;
                if (videoBox.Checked)
                {
                    DownloadItem item = CreateItem(entry); item.MediaFormat = VideoSelector();
                    item.MediaContainer = container.SelectedIndex > 0 ? Convert.ToString(container.SelectedItem) : "";
                    item.MediaHeight = videoTrack.SelectedIndex == 0 && formatBox.SelectedIndex > 1 ? heights[formatBox.SelectedIndex] : 0;
                    item.MediaExactMatch = item.MediaHeight > 0 && exactBox.Checked; result.Add(item);
                }
                if (audioBox.Checked)
                {
                    DownloadItem item = CreateItem(entry); item.Kind = "audio";
                    item.MediaFormat = audioTrack.SelectedIndex >= 2 ? audioFormats[audioTrack.SelectedIndex - 2].Id : "bestaudio/best";
                    item.MediaAudioFormat = audioFormat.SelectedIndex == 0 ? "original" : audioFormat.SelectedIndex == 1 ? "aac" : "mp3"; item.MediaAudioBitrate = (int)audioBitrate.SelectedItem;
                    if (videoBox.Checked) { item.SaveSubtitles = false; item.SaveThumbnail = false; }
                    result.Add(item);
                }
                if (!videoBox.Checked && !audioBox.Checked && (thumbnailBox.Checked || subtitlesBox.Checked))
                { DownloadItem item = CreateItem(entry); item.MediaOnlyAssets = true; result.Add(item); }
            }
            if (result.Count == 0) return;
            Result = result; DialogResult = DialogResult.OK; Close();
        }
    }
}
