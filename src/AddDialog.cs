using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class AddDialog : Form
    {
        readonly AppSettings settings;
        readonly List<DownloadItem> originals;
        readonly TextBox urls = new TextBox();
        readonly TextBox directory = new TextBox();
        readonly TextBox filename = new TextBox();
        readonly TextBox referer = new TextBox();
        readonly TextBox userAgent = new TextBox();
        readonly TextBox headers = new TextBox();
        readonly TextBox body = new TextBox();
        readonly TextBox format = new TextBox();
        readonly TextBox selectedFiles = new TextBox();
        readonly ComboBox kind = new ComboBox();
        readonly ComboBox method = new ComboBox();
        readonly ComboBox category = new ComboBox();
        readonly ComboBox queue = new ComboBox();
        readonly NumericUpDown connections = new NumericUpDown();
        readonly NumericUpDown limit = new NumericUpDown();
        readonly CheckBox subtitles = new CheckBox();
        readonly CheckBox seed = new CheckBox();
        readonly CheckBox start = new CheckBox();
        readonly CheckBox sharedRequest = new CheckBox();
        readonly CheckBox rememberCategory = new CheckBox { AutoSize = true };
        readonly CheckBox rememberType = new CheckBox { AutoSize = true };
        readonly Button readMetadata = new Button();
        readonly Label metadata = new Label { AutoSize = true, UseMnemonic = false };
        CancellationTokenSource metadataCancellation;
        bool directoryEdited;
        ScheduleSettings schedule;
        bool scheduleEdited;
        readonly TransferEngine preDownloadEngine;
        readonly System.Windows.Forms.Timer preDownloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
        readonly Label preDownloadStatus = new Label { AutoSize = true, Dock = DockStyle.Fill };
        readonly ProgressBar preDownloadProgress = new ProgressBar { Dock = DockStyle.Top, Height = 15 };
        Task preDownloadTask, preDownloadTransition = Task.FromResult(0);
        bool preDownloadDirty = true, finalizing, closingReady;
        DateTime preDownloadChangedAt = DateTime.MinValue;
        public List<DownloadItem> Result { get; private set; }
        public DownloadItem PreDownloadItem { get; private set; }

        string T(string zh, string en) { return settings.Language == "zh-CN" ? zh : en; }

        public AddDialog(AppSettings appSettings, IEnumerable<DownloadItem> items)
            : this(appSettings, items, null) { }

        public AddDialog(AppSettings appSettings, IEnumerable<DownloadItem> items, TransferEngine engine)
        {
            settings = appSettings;
            preDownloadEngine = settings.PreDownloadInAddDialog ? engine : null;
            originals = items.Select(x => x.Copy()).ToList();
            Text = T("添加下载", "Add downloads");
            Size = new Size(760, 670);
            MinimumSize = new Size(650, 540);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10);
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(tabs);
            TableLayoutPanel main = Page(tabs, T("下载", "Download"));
            urls.Multiline = true; urls.ScrollBars = ScrollBars.Vertical; urls.Height = 140; urls.AcceptsReturn = true;
            Row(main, T("链接（每行一个）", "URLs (one per line)"), urls);
            Row(main, T("保存到", "Save to"), directory);
            Button browse = new Button { Text = T("浏览…", "Browse…"), AutoSize = true };
            browse.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { SelectedPath = directory.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) { directory.Text = dialog.SelectedPath; directoryEdited = true; } };
            Row(main, "", browse);
            Row(main, T("文件名（单项）", "Filename (one item)"), filename);
            readMetadata.Text = T("读取文件信息", "Read file information"); readMetadata.AutoSize = true;
            readMetadata.Click += ReadMetadata; Row(main, "", readMetadata);
            Row(main, "", metadata);
            kind.DropDownStyle = ComboBoxStyle.DropDownList; kind.Items.AddRange(new object[] { "file", "media", "audio", "torrent" });
            if (originals.Count > 1) kind.Items.Add(T("保持各项类型", "Keep individual types"));
            Row(main, T("类型", "Type"), kind);
            category.Items.Add(""); foreach (CategorySettings c in settings.Categories) category.Items.Add(c.Name);
            Row(main, T("分类", "Category"), category);
            rememberCategory.Text = T("将此目录保存为分类目录", "Save this folder for the category");
            rememberType.Text = T("将此目录保存为文件类型目录", "Save this folder for the file type");
            Row(main, "", rememberCategory); Row(main, "", rememberType);
            queue.DropDownStyle = ComboBoxStyle.DropDownList;
            queue.Items.Add(new QueueSettings { Id = "", Name = T("默认队列", "Default queue") });
            foreach (QueueSettings q in settings.Queues) queue.Items.Add(q);
            Row(main, T("队列", "Queue"), queue);
            start.Text = T("立即加入运行队列", "Start when a slot is available"); start.AutoSize = true;
            Row(main, "", start);
            schedule = originals.Count == 1 ? originals[0].Schedule : null;
            Button plan = new Button { Text = T("任务计划…", "Schedule…"), AutoSize = true };
            plan.Click += delegate
            {
                using (var dialog = new ScheduleDialog(schedule ?? new ScheduleSettings { RetryCount = settings.RetryCount, RetryDelaySeconds = settings.RetryDelaySeconds }, settings.Language))
                    if (dialog.ShowDialog(this) == DialogResult.OK) { schedule = dialog.Result; scheduleEdited = true; start.Checked = true; }
            };
            Row(main, "", plan);
            TableLayoutPanel network = Page(tabs, T("请求与连接", "Request and connections"));
            sharedRequest.Text = T("将请求设置应用到所有链接", "Apply request settings to all URLs"); sharedRequest.AutoSize = true;
            sharedRequest.Checked = originals.Count <= 1; sharedRequest.Visible = originals.Count > 1;
            Row(network, "", sharedRequest);
            method.DropDownStyle = ComboBoxStyle.DropDownList; method.Items.AddRange(new object[] { "GET", "POST" });
            Row(network, T("方法", "Method"), method);
            Row(network, "Referer", referer); Row(network, "User-Agent", userAgent);
            headers.Multiline = true; headers.ScrollBars = ScrollBars.Vertical; headers.Height = 130;
            Row(network, T("请求头（名称: 值）", "Headers (Name: value)"), headers);
            body.Multiline = true; body.ScrollBars = ScrollBars.Vertical; body.Height = 100;
            Row(network, T("POST 请求体", "POST body"), body);
            Action requestEnabled = () => { foreach (Control control in new Control[] { method, referer, userAgent, headers, body }) control.Enabled = sharedRequest.Checked; };
            sharedRequest.CheckedChanged += delegate { requestEnabled(); }; requestEnabled();
            connections.Minimum = 1; connections.Maximum = 16;
            Row(network, T("连接数", "Connections"), connections);
            limit.Maximum = 1024 * 1024 * 1024; limit.Increment = 1024;
            Row(network, T("限速 B/s（0 不限）", "Limit B/s (0 unlimited)"), limit);
            TableLayoutPanel media = Page(tabs, T("媒体与种子", "Media and torrent"));
            Row(media, T("媒体格式选择器", "Media format selector"), format);
            subtitles.Text = T("下载字幕", "Download subtitles"); subtitles.AutoSize = true;
            Row(media, "", subtitles);
            Row(media, T("种子文件序号（例 1,3-5）", "Torrent indexes (e.g. 1,3-5)"), selectedFiles);
            Button chooseFiles = new Button { Text = T("读取并选择种子文件…", "Read and select torrent files…"), AutoSize = true };
            chooseFiles.Click += ChooseTorrentFiles; Row(media, "", chooseFiles);
            seed.Text = T("下载完成后继续做种", "Seed after download"); seed.AutoSize = true;
            Row(media, "", seed);
            Label help = new Label { Text = T("批量数字链接可写为 [001-020]。留空文件名时自动获取；请求头仅发送到所填链接。", "Use [001-020] for numbered batches. Leave filename empty for automatic naming. Headers are sent with the supplied URL."), AutoSize = true, MaximumSize = new Size(510, 0) };
            Row(main, "", help);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(7) };
            Button cancel = new Button { Text = T("取消", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            Button add = new Button { Text = T("确定", "OK"), AutoSize = true };
            add.Click += Accept;
            buttons.Controls.Add(cancel); buttons.Controls.Add(add); Controls.Add(buttons);
            AcceptButton = add; CancelButton = cancel;
            DownloadItem first = originals.FirstOrDefault() ?? new DownloadItem { Directory = settings.DownloadDirectory, Connections = settings.Connections, Seed = settings.SeedAfterDownload };
            urls.Text = String.Join(Environment.NewLine, originals.Select(x => x.Url));
            directory.Text = first.Directory; filename.Text = originals.Count <= 1 ? first.FileName : "";
            kind.SelectedItem = first.Kind; if (kind.SelectedIndex < 0) kind.SelectedIndex = 0;
            if (originals.Count > 1) kind.SelectedIndex = 4;
            category.Text = first.Category; queue.SelectedIndex = 0;
            for (int i = 0; i < queue.Items.Count; i++) if (((QueueSettings)queue.Items[i]).Id == first.QueueId) queue.SelectedIndex = i;
            method.SelectedItem = first.Method; if (method.SelectedIndex < 0) method.SelectedIndex = 0;
            referer.Text = first.Referer; userAgent.Text = first.UserAgent;
            headers.Text = String.Join(Environment.NewLine, first.Headers.Select(x => x.Key + ": " + x.Value));
            body.Text = first.Body; format.Text = first.MediaFormat; subtitles.Checked = first.SaveSubtitles;
            seed.Checked = first.Seed; selectedFiles.Text = first.SelectedFiles;
            Action connectionRange = () => connections.Maximum = MediaTools.IsYouTubeUrl(urls.Text.Trim()) && (kind.SelectedIndex == 1 || kind.SelectedIndex == 2) ? 64 : 16;
            connectionRange(); urls.TextChanged += delegate { connectionRange(); }; kind.SelectedIndexChanged += delegate { connectionRange(); };
            connections.Value = Math.Min(connections.Maximum, Math.Max(1, first.Connections));
            limit.Value = Math.Min(limit.Maximum, Math.Max(0, first.SpeedLimit));
            start.Checked = originals.Count == 0 ? settings.AutoStartDownloads : first.State == DownloadState.Queued;
            directoryEdited = first.DirectoryExplicit;
            directory.TextChanged += delegate { directoryEdited = true; };
            Action suggestDirectory = delegate
            {
                string link = urls.Lines.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? "";
                string extension = AppSettings.FileType(link.Trim(), filename.Text.Trim());
                CategorySettings selectedCategory = settings.Categories.FirstOrDefault(x => x.Name == category.Text);
                rememberType.Enabled = extension.Length > 0; rememberCategory.Enabled = selectedCategory != null;
                if (!directoryEdited)
                {
                    string suggested = settings.RememberedDirectory(link.Trim(), filename.Text.Trim());
                    if (suggested.Length == 0) suggested = selectedCategory != null && selectedCategory.Directory.Length > 0 ? selectedCategory.Directory : settings.DownloadDirectory;
                    directory.Text = suggested; directoryEdited = false;
                }
            };
            urls.TextChanged += delegate { suggestDirectory(); }; filename.TextChanged += delegate { suggestDirectory(); };
            category.TextChanged += delegate { suggestDirectory(); }; suggestDirectory();
            foreach (Control field in new Control[] { urls, kind, method, referer, userAgent, headers, body })
                field.TextChanged += delegate { metadata.Text = ""; if (metadataCancellation != null) metadataCancellation.Cancel(); };
            FormClosed += delegate { if (metadataCancellation != null) metadataCancellation.Cancel(); };
            if (preDownloadEngine != null)
            {
                var preview = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Padding = new Padding(10, 7, 10, 7) };
                preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                preview.Controls.Add(preDownloadStatus); preview.Controls.Add(preDownloadProgress); Controls.Add(preview);
                preDownloadStatus.Text = T("预下载只暂存普通 HTTP(S) GET 文件；确认后才保存输出，取消会移除临时数据。", "Ordinary HTTP(S) GET files are pre-downloaded to temporary storage. Confirm to save; Cancel removes temporary data.");
                foreach (Control field in new Control[] { urls, kind, method, referer, userAgent, headers, body })
                    field.TextChanged += delegate
                    {
                        if (finalizing) return;
                        preDownloadDirty = true; preDownloadChangedAt = DateTime.UtcNow;
                        if (PreDownloadItem != null) preDownloadEngine.Pause(PreDownloadItem);
                    };
                foreach (Control field in new Control[] { filename, directory })
                    field.TextChanged += delegate { if (!finalizing && PreDownloadItem == null) { preDownloadDirty = true; preDownloadChangedAt = DateTime.UtcNow; } };
                preDownloadTimer.Tick += delegate
                {
                    ShowPreDownloadProgress();
                    if (!finalizing && preDownloadTransition.IsCompleted && preDownloadDirty && (DateTime.UtcNow - preDownloadChangedAt).TotalMilliseconds >= 400)
                        preDownloadTransition = UpdatePreDownloadAsync();
                };
                Shown += delegate { preDownloadTimer.Start(); preDownloadTransition = UpdatePreDownloadAsync(); };
                FormClosing += ClosePreDownload;
            }
            FormClosed += delegate { preDownloadTimer.Stop(); preDownloadTimer.Dispose(); };
            Appearance.Apply(this);
        }

        async Task UpdatePreDownloadAsync()
        {
            preDownloadDirty = false;
            try
            {
                if (PreDownloadItem != null)
                {
                    await preDownloadEngine.ResetPartialAsync(PreDownloadItem);
                    PreDownloadItem = null;
                }
                if (finalizing) return;
                List<string> links = Expand(urls.Text).Distinct().ToList(); Uri uri;
                if (links.Count != 1 || kind.SelectedIndex != 0 || Convert.ToString(method.SelectedItem) != "GET" ||
                    !Uri.TryCreate(links[0], UriKind.Absolute, out uri) || !(new[] { "http", "https" }).Contains(uri.Scheme) ||
                    (new[] { ".mpd", ".m3u8", ".torrent" }).Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase) ||
                    !Path.IsPathRooted(directory.Text) || filename.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    preDownloadStatus.Text = T("此输入不预下载；支持单项普通 HTTP(S) GET 文件，流媒体、种子、FTP 和 POST 除外。", "No pre-download for this input. Use one ordinary HTTP(S) GET file; streams, torrents, FTP and POST are excluded.");
                    preDownloadProgress.Style = ProgressBarStyle.Continuous; preDownloadProgress.Value = 0;
                    return;
                }
                DownloadItem item = originals.Count == 1 ? originals[0].Copy() : new DownloadItem();
                item.Url = links[0]; item.Directory = directory.Text.Trim(); item.FileName = filename.Text.Trim();
                item.Kind = "file"; item.Method = "GET"; item.Body = body.Text;
                item.Headers = ReadHeaders(); item.Referer = referer.Text; item.UserAgent = userAgent.Text;
                item.Connections = (int)connections.Value; item.SpeedLimit = (long)limit.Value;
                preDownloadTask = preDownloadEngine.PreDownloadAsync(item);
                PreDownloadItem = item;
                ShowPreDownloadProgress();
            }
            catch (Exception error)
            {
                preDownloadStatus.Text = T("预下载未开始：", "Pre-download unavailable: ") + error.Message;
            }
        }

        void ShowPreDownloadProgress()
        {
            DownloadItem item = PreDownloadItem; if (item == null) return;
            if (item.Error.Length > 0) preDownloadStatus.Text = (item.State == DownloadState.Error ? T("预下载失败：", "Pre-download failed: ") : "") + item.Error;
            else
            {
                string percentage = item.TotalBytes > 0 ? Math.Min(100, item.BytesReceived * 100.0 / item.TotalBytes).ToString("0.0") + "% · " : "";
                preDownloadStatus.Text = T("临时预下载：", "Temporary pre-download: ") + percentage + item.BytesReceived.ToString("N0") + " B" +
                    (item.State == DownloadState.Paused ? T(" · 等待确认", " · Awaiting confirmation") : "");
            }
            preDownloadProgress.Style = item.TotalBytes > 0 || item.State != DownloadState.Running ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (item.TotalBytes > 0) preDownloadProgress.Value = (int)Math.Min(100, item.BytesReceived * 100.0 / item.TotalBytes);
        }

        async void ClosePreDownload(object sender, FormClosingEventArgs args)
        {
            if (closingReady) return;
            if (PreDownloadItem == null && preDownloadTransition.IsCompleted) return;
            args.Cancel = true;
            if (finalizing) return;
            finalizing = true; preDownloadTimer.Stop(); Enabled = false;
            try
            {
                await preDownloadTransition;
                if (PreDownloadItem != null) await preDownloadEngine.ResetPartialAsync(PreDownloadItem);
                PreDownloadItem = null; closingReady = true; DialogResult = DialogResult.Cancel; Close();
            }
            catch (Exception error)
            {
                finalizing = false; Enabled = true; preDownloadTimer.Start();
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async void ReadMetadata(object sender, EventArgs args)
        {
            if (metadataCancellation != null) { metadataCancellation.Cancel(); return; }
            CancellationTokenSource cancellation = new CancellationTokenSource();
            metadataCancellation = cancellation;
            try
            {
                List<string> links = Expand(urls.Text).Distinct().ToList();
                if (links.Count != 1 || !ValidUrl(links[0]) || kind.SelectedIndex != 0 || Convert.ToString(method.SelectedItem) != "GET")
                    throw new ArgumentException(T("文件信息读取用于单个普通文件 GET 链接。批量链接请在批量窗口中扫描。", "Read information for one ordinary file GET URL. Use the batch window to scan multiple URLs."));
                Uri uri;
                if (!Uri.TryCreate(links[0], UriKind.Absolute, out uri) || !(new[] { "http", "https", "ftp", "ftps" }).Contains(uri.Scheme))
                    throw new ArgumentException(T("请选择 HTTP、HTTPS、FTP 或 FTPS 文件链接。", "Choose an HTTP, HTTPS, FTP or FTPS file URL."));
                DownloadItem item = new DownloadItem { Url = links[0], Headers = ReadHeaders(), Referer = referer.Text, UserAgent = userAgent.Text };
                metadata.Text = T("正在读取响应头…", "Reading response headers…");
                readMetadata.Text = T("取消读取", "Cancel reading");
                DownloadMetadata result = await TransferEngine.InspectMetadataAsync(item, settings, cancellation.Token);
                if (cancellation.IsCancellationRequested || IsDisposed) return;
                metadata.Text = T("名称：", "Name: ") + (result.FileName.Length > 0 ? result.FileName : T("服务器未提供", "Not supplied")) + Environment.NewLine +
                    T("大小：", "Size: ") + (result.TotalBytes >= 0 ? result.TotalBytes.ToString("N0") + " B" : T("未知", "Unknown")) + Environment.NewLine +
                    T("类型：", "Type: ") + (result.ContentType.Length > 0 ? result.ContentType : T("未知", "Unknown"));
                if (settings.ShowDirectUrls && result.DirectUrl.Length > 0)
                {
                    metadata.MaximumSize = new Size(420, 0);
                    metadata.Text += Environment.NewLine + T("查询得到的地址：", "Address from lookup: ") + result.DirectUrl;
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) metadata.Text = T("读取已取消", "Reading canceled"); }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is ArgumentException) && !(error is FormatException) && !(error is OverflowException) && !(error is System.ComponentModel.Win32Exception)) throw;
                if (!IsDisposed) metadata.Text = error.Message;
            }
            finally
            {
                metadataCancellation = null; cancellation.Dispose();
                if (!IsDisposed) readMetadata.Text = T("读取文件信息", "Read file information");
            }
        }

        static TableLayoutPanel Page(TabControl tabs, string title)
        {
            TabPage page = new TabPage(title) { Padding = new Padding(12) };
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(panel); tabs.TabPages.Add(page); return panel;
        }

        static void Row(TableLayoutPanel panel, string title, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(0, 3, 0, 8); panel.Controls.Add(control, 1, row);
        }

        public static bool ValidUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ftp" || uri.Scheme == "ftps" || uri.Scheme == "magnet") ||
                Path.IsPathRooted(value) && value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(value);
        }

        public static IEnumerable<string> Expand(string input)
        {
            foreach (string raw in input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                Match range = Regex.Match(line, @"\[(\d+)-(\d+)\]");
                if (!range.Success) { yield return line; continue; }
                int from = Int32.Parse(range.Groups[1].Value), to = Int32.Parse(range.Groups[2].Value);
                if (to < from || (long)to - from > 10000) throw new ArgumentException("Invalid batch range (maximum 10001 URLs).");
                for (long n = from; n <= to; n++) yield return line.Substring(0, range.Index) + n.ToString("D" + range.Groups[1].Length) + line.Substring(range.Index + range.Length);
            }
        }

        async void Accept(object sender, EventArgs args)
        {
            try
            {
                List<string> links = Expand(urls.Text).Distinct().ToList();
                if (links.Count == 0 || links.Any(x => !ValidUrl(x))) throw new ArgumentException(T("请输入有效下载链接或本地 .torrent 文件路径。", "Enter valid URLs or local .torrent paths."));
                if (String.IsNullOrWhiteSpace(directory.Text) || !Path.IsPathRooted(directory.Text)) throw new ArgumentException(T("请选择完整保存路径。", "Choose an absolute download directory."));
                if (filename.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException(T("文件名包含无效字符。", "Filename contains invalid characters."));
                Dictionary<string, string> requestHeaders = ReadHeaders();
                Result = new List<DownloadItem>();
                foreach (string url in links)
                {
                    DownloadItem item = originals.Count == 1 && links.Count == 1 ? originals[0] : originals.FirstOrDefault(x => x.Url == url);
                    item = item == null ? new DownloadItem() : item.Copy();
                    item.Url = url; item.FileName = links.Count == 1 ? filename.Text.Trim() : item.FileName;
                    item.Directory = directory.Text.Trim(); if (kind.SelectedIndex < 4) item.Kind = Convert.ToString(kind.SelectedItem);
                    item.DirectoryExplicit = item.DirectoryExplicit || directoryEdited;
                    if (scheduleEdited) item.Schedule = schedule.Copy();
                    if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) item.Kind = "torrent";
                    item.Category = category.Text; item.QueueId = ((QueueSettings)queue.SelectedItem).Id;
                    if (sharedRequest.Checked)
                    {
                        item.Method = Convert.ToString(method.SelectedItem); item.Referer = referer.Text; item.UserAgent = userAgent.Text;
                        item.Headers = new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase); item.Body = body.Text;
                    }
                    item.Connections = (int)connections.Value; item.SpeedLimit = (long)limit.Value;
                    item.MediaFormat = format.Text; item.SaveSubtitles = subtitles.Checked; item.Seed = seed.Checked; item.SelectedFiles = selectedFiles.Text;
                    item.State = start.Checked ? DownloadState.Queued : DownloadState.Paused;
                    Result.Add(item);
                }
                if (preDownloadEngine != null)
                {
                    finalizing = true; preDownloadTimer.Stop(); Enabled = false;
                    await preDownloadTransition;
                    if (PreDownloadItem != null)
                    {
                        preDownloadEngine.Pause(PreDownloadItem); await preDownloadTask;
                        DownloadItem item = Result.Count == 1 ? Result[0] : null;
                        if (!preDownloadDirty && item != null && item.Url == PreDownloadItem.Url && item.Kind == "file" && item.Method == "GET")
                        {
                            item.Id = PreDownloadItem.Id; item.WorkingDirectory = PreDownloadItem.WorkingDirectory; item.EngineId = PreDownloadItem.EngineId;
                            item.BytesReceived = PreDownloadItem.BytesReceived; item.TotalBytes = PreDownloadItem.TotalBytes;
                            item.DirectUrl = PreDownloadItem.DirectUrl;
                            if (item.FileName.Length == 0) item.FileName = PreDownloadItem.FileName;
                        }
                        else { await preDownloadEngine.ResetPartialAsync(PreDownloadItem); PreDownloadItem = null; }
                    }
                }
                if (rememberCategory.Checked && rememberCategory.Enabled) settings.Categories.First(x => x.Name == category.Text).Directory = directory.Text.Trim();
                if (rememberType.Checked && rememberType.Enabled)
                    foreach (DownloadItem item in Result)
                    {
                        string extension = AppSettings.FileType(item.Url, item.FileName);
                        if (extension.Length > 0) settings.FileTypeDirectories[extension] = item.Directory;
                    }
                closingReady = true; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception error)
            {
                finalizing = false; Enabled = true; if (preDownloadEngine != null) preDownloadTimer.Start();
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Dictionary<string, string> ReadHeaders()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in headers.Lines.Where(x => !String.IsNullOrWhiteSpace(x)))
            {
                int colon = line.IndexOf(':');
                if (colon < 1) throw new ArgumentException(T("请求头需要使用 名称: 值 格式。", "Use Name: value for each header."));
                result[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            return result;
        }

        void ChooseTorrentFiles(object sender, EventArgs args)
        {
            try
            {
                List<string> links = Expand(urls.Text).Distinct().ToList();
                if (links.Count != 1 || !ValidUrl(links[0])) throw new ArgumentException(T("请先填写一个种子链接、磁链或本地种子路径。", "Enter one torrent URL, magnet link or local torrent path first."));
                DownloadItem original = originals.FirstOrDefault(x => x.Url == links[0]);
                DownloadItem item = original == null ? new DownloadItem() : original.Copy();
                item.Url = links[0]; item.Kind = "torrent"; item.SelectedFiles = selectedFiles.Text;
                if (sharedRequest.Checked)
                {
                    item.Headers = ReadHeaders(); item.Method = Convert.ToString(method.SelectedItem);
                    item.Referer = referer.Text; item.UserAgent = userAgent.Text; item.Body = body.Text;
                }
                using (TorrentFilesDialog dialog = new TorrentFilesDialog(item, settings))
                    if (dialog.ShowDialog(this) == DialogResult.OK) { selectedFiles.Text = dialog.SelectedFiles; kind.SelectedItem = "torrent"; }
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is FormatException) && !(error is OverflowException)) throw;
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
