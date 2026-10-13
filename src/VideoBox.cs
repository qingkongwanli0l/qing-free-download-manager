using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class BrowserMediaCandidate
    {
        public DownloadItem Item { get; set; }
        public string Title { get; set; }
        public string ThumbnailUrl { get; set; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string SourceTitle { get; set; }
        public string MimeType { get; set; }
    }

    public sealed class VideoBox : Form
    {
        readonly Func<AppSettings> settings;
        readonly Action<List<DownloadItem>> download;
        readonly Action save;
        readonly Dictionary<string, List<BrowserMediaCandidate>> sources = new Dictionary<string, List<BrowserMediaCandidate>>();
        readonly Dictionary<string, Image> thumbnails = new Dictionary<string, Image>();
        readonly HashSet<string> loading = new HashSet<string>();
        readonly HttpClient client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        readonly DataGridView list = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AutoGenerateColumns = false };
        readonly NumericUpDown rows = new NumericUpDown { Minimum = 2, Maximum = 20, Width = 48 };
        readonly NumericUpDown opacity = new NumericUpDown { Minimum = 20, Maximum = 100, Increment = 5, Width = 52 };
        readonly CheckBox onTop = new CheckBox { AutoSize = true };
        readonly CheckBox showThumbnails = new CheckBox { AutoSize = true };
        readonly CheckBox showDetails = new CheckBox { AutoSize = true };
        readonly Button start = new Button { AutoSize = true };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 25, Padding = new Padding(6, 3, 0, 0) };
        readonly FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(5), WrapContents = true };
        bool applying;
        string T(string zh, string en) { return settings().Language == "zh-CN" ? zh : en; }

        public VideoBox(Func<AppSettings> settings, Action<List<DownloadItem>> onDownload, Action onSettingsChanged)
        {
            this.settings = settings; download = onDownload; save = onSettingsChanged;
            Font = new Font("Segoe UI", 9F); Width = 660; MinimumSize = new Size(530, 230);
            StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.SizableToolWindow;
            list.Columns.Add(new DataGridViewImageColumn { Name = "thumbnail", HeaderText = "", Width = 92, ImageLayout = DataGridViewImageCellLayout.Zoom });
            list.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 140 });
            list.Columns.Add(new DataGridViewTextBoxColumn { Name = "details", Width = 225 });
            list.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            list.Columns["thumbnail"].DefaultCellStyle.NullValue = null;
            var rowLabel = new Label { Text = T("行数", "Rows"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
            var opacityLabel = new Label { Text = T("不透明度 %", "Opacity %"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
            options.Controls.AddRange(new Control[] { start, onTop, showThumbnails, showDetails, rowLabel, rows, opacityLabel, opacity });
            Controls.Add(list); Controls.Add(status); Controls.Add(options);
            start.Click += delegate { DownloadSelection(); };
            list.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) DownloadSelection(); };
            list.SelectionChanged += delegate { start.Enabled = list.SelectedRows.Count != 0; };
            rows.ValueChanged += SaveOptions; opacity.ValueChanged += SaveOptions;
            onTop.CheckedChanged += SaveOptions; showThumbnails.CheckedChanged += SaveOptions; showDetails.CheckedChanged += SaveOptions;
            FormClosed += delegate { client.Dispose(); foreach (Image image in thumbnails.Values) image.Dispose(); thumbnails.Clear(); sources.Clear(); };
            ApplySettings();
        }

        public void ApplySettings()
        {
            applying = true;
            AppSettings value = settings();
            rows.Value = Math.Max(2, Math.Min(20, value.MediaBoxRows)); opacity.Value = Math.Max(20, Math.Min(100, value.MediaBoxOpacity));
            onTop.Checked = value.MediaBoxTopMost; showThumbnails.Checked = value.MediaBoxShowThumbnails; showDetails.Checked = value.MediaBoxShowDetails;
            Text = T("Qing 媒体窗口", "Qing media window"); start.Text = T("下载所选", "Download selected");
            onTop.Text = T("置顶", "On top"); showThumbnails.Text = T("缩略图", "Thumbnails"); showDetails.Text = T("详细信息", "Details");
            list.Columns["title"].HeaderText = T("媒体", "Media"); list.Columns["details"].HeaderText = T("来源与格式", "Source and format");
            TopMost = onTop.Checked; Opacity = (double)opacity.Value / 100;
            Appearance.Apply(this);
            list.Columns["thumbnail"].Visible = showThumbnails.Checked; list.Columns["details"].Visible = showDetails.Checked;
            int height = showThumbnails.Checked ? 64 : Math.Max(42, Font.Height * 2 + 8);
            list.RowTemplate.Height = height; foreach (DataGridViewRow row in list.Rows) row.Height = height;
            ClientSize = new Size(ClientSize.Width, Math.Min(Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height), options.Height + status.Height + list.ColumnHeadersHeight + height * (int)rows.Value + 3));
            if (IsHandleCreated)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            }
            applying = false;
            if (showThumbnails.Checked) LoadThumbnails();
        }

        void SaveOptions(object sender, EventArgs args)
        {
            if (applying) return;
            AppSettings value = settings(); value.MediaBoxRows = (int)rows.Value; value.MediaBoxOpacity = (int)opacity.Value;
            value.MediaBoxTopMost = onTop.Checked; value.MediaBoxShowThumbnails = showThumbnails.Checked; value.MediaBoxShowDetails = showDetails.Checked;
            ApplySettings(); save();
        }

        public void UpdateEntries(string sourceKey, List<BrowserMediaCandidate> entries)
        {
            sources[sourceKey] = entries.Where(x => x.Item != null).GroupBy(x => x.Item.Url).Select(x => x.First()).ToList();
            RefreshEntries();
        }

        public void ClearEntries(string sourceKey) { if (sources.Remove(sourceKey)) RefreshEntries(); }

        void RefreshEntries()
        {
            var selected = new HashSet<string>(list.SelectedRows.Cast<DataGridViewRow>().Select(x => ((BrowserMediaCandidate)x.Tag).Item.Url));
            list.Rows.Clear();
            foreach (BrowserMediaCandidate entry in sources.Values.SelectMany(x => x).GroupBy(x => x.Item.Url).Select(x => x.First()))
            {
                string title = String.IsNullOrWhiteSpace(entry.Title) ? entry.Item.FileName : entry.Title;
                if (String.IsNullOrWhiteSpace(title)) title = entry.Item.Url;
                string detail = entry.SourceTitle ?? "";
                if (entry.Width > 0 && entry.Height > 0) detail += " · " + entry.Width + "×" + entry.Height;
                if (entry.DurationSeconds > 0 && !Double.IsInfinity(entry.DurationSeconds) && !Double.IsNaN(entry.DurationSeconds)) detail += " · " + TimeSpan.FromSeconds(Math.Min(entry.DurationSeconds, 86400000)).ToString(@"hh\:mm\:ss");
                if (!String.IsNullOrWhiteSpace(entry.MimeType)) detail += " · " + entry.MimeType;
                Image image; thumbnails.TryGetValue(entry.ThumbnailUrl ?? "", out image);
                int index = list.Rows.Add(image, title, detail.Trim(' ', '·'));
                list.Rows[index].Tag = entry; list.Rows[index].Cells["title"].ToolTipText = entry.Item.Url;
                list.Rows[index].Selected = selected.Contains(entry.Item.Url);
            }
            status.Text = list.Rows.Count + T(" 项 · 关闭窗口后，需从扩展手动重新打开", " items · Reopen from the extension after closing");
            start.Enabled = list.SelectedRows.Count != 0;
            var used = new HashSet<string>(sources.Values.SelectMany(x => x).Select(x => x.ThumbnailUrl ?? ""));
            foreach (string key in thumbnails.Keys.Where(x => !used.Contains(x)).ToList()) { thumbnails[key].Dispose(); thumbnails.Remove(key); }
            if (showThumbnails.Checked) LoadThumbnails();
        }

        void DownloadSelection()
        {
            var items = new List<DownloadItem>();
            foreach (DataGridViewRow row in list.SelectedRows)
            {
                DownloadItem item = ((BrowserMediaCandidate)row.Tag).Item.Copy(); item.Id = Guid.NewGuid().ToString("N"); items.Add(item);
            }
            if (items.Count != 0) download(items);
        }

        void LoadThumbnails()
        {
            foreach (string url in sources.Values.SelectMany(x => x).Select(x => x.ThumbnailUrl).Where(x => !String.IsNullOrEmpty(x)).Distinct())
                if (!thumbnails.ContainsKey(url) && loading.Add(url)) LoadThumbnail(url);
        }

        async void LoadThumbnail(string url)
        {
            try
            {
                Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                using (timeout.Token.Register(response.Dispose))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 2097152) return;
                    using (Stream source = await response.Content.ReadAsStreamAsync())
                    using (var bytes = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192]; int count;
                        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) != 0)
                        { if (bytes.Length + count > 2097152) return; bytes.Write(buffer, 0, count); }
                        if (IsDisposed || !sources.Values.SelectMany(x => x).Any(x => x.ThumbnailUrl == url)) return;
                        bytes.Position = 0;
                        using (Image image = Image.FromStream(bytes)) thumbnails[url] = new Bitmap(image, new Size(88, 54));
                        foreach (DataGridViewRow row in list.Rows) if (((BrowserMediaCandidate)row.Tag).ThumbnailUrl == url) row.Cells["thumbnail"].Value = thumbnails[url];
                    }
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
            finally { loading.Remove(url); }
        }
    }
}
