using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class BatchDialog : Form
    {
        private sealed class Entry
        {
            internal DownloadItem Item;
            internal bool ExplicitName;
            internal string ContentType = "";
        }

        private readonly AppSettings settings;
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox search = new TextBox();
        private readonly TextBox extensions = new TextBox();
        private readonly ComboBox category = new ComboBox();
        private readonly CheckBox useDirectory = new CheckBox();
        private readonly TextBox directory = new TextBox();
        private readonly ComboBox queue = new ComboBox();
        private readonly ComboBox start = new ComboBox();
        private readonly Button addLinks = new Button();
        private readonly CheckBox scanEnabled = new CheckBox();
        private readonly Button scan = new Button();
        private readonly Button add = new Button();
        private readonly Label status = new Label();
        private readonly Label selection = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private CancellationTokenSource operation;
        private bool closeRequested, loading;
        public List<DownloadItem> Result { get; private set; }

        public BatchDialog(AppSettings settings, IEnumerable<DownloadItem> items)
        {
            this.settings = settings.Copy();
            Text = T("选择批量下载", "Choose batch downloads");
            Size = new Size(1100, 730); MinimumSize = new Size(800, 540);
            Font = new Font("Segoe UI", 9F); AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 6 };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            filters.Controls.Add(new Label { Text = T("搜索", "Search"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            search.Dock = DockStyle.Fill; filters.Controls.Add(search, 1, 0);
            filters.Controls.Add(new Label { Text = T("分类", "Category"), AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
            category.Dock = DockStyle.Fill; category.DropDownStyle = ComboBoxStyle.DropDownList;
            category.Items.Add(T("全部类型", "All types"));
            foreach (CategorySettings item in settings.Categories) category.Items.Add(item.Name);
            category.SelectedIndex = 0; filters.Controls.Add(category, 3, 0);
            filters.Controls.Add(new Label { Text = T("扩展名（逗号分隔）", "Extensions (comma-separated)"), AutoSize = true, Anchor = AnchorStyles.Left }, 4, 0);
            extensions.Dock = DockStyle.Fill; filters.Controls.Add(extensions, 5, 0); root.Controls.Add(filters, 0, 0);

            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = SystemColors.Window; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = T("选择", "Select"), Width = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = T("文件名", "Filename"), Width = 190 });
            AddColumn("Type", T("类型", "Type"), 130); AddColumn("Size", T("大小", "Size"), 90);
            AddColumn("LinkText", T("链接文字", "Link text"), 150); AddColumn("Url", "URL", 300);
            AddColumn("Status", T("扫描状态", "Scan status"), 190);
            AddEntries(items);
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (loading || e.RowIndex < 0) return;
                DataGridViewRow row = grid.Rows[e.RowIndex]; Entry entry = (Entry)row.Tag;
                if (e.ColumnIndex == 1)
                {
                    entry.Item.FileName = Convert.ToString(row.Cells[1].Value).Trim(); entry.ExplicitName = entry.Item.FileName.Length > 0;
                }
                UpdateSelection();
            };
            root.Controls.Add(grid, 0, 1);
            var destination = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 6, 0, 3) };
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            useDirectory.Text = T("所选项统一保存到", "Save selection in"); useDirectory.AutoSize = true; destination.Controls.Add(useDirectory, 0, 0);
            directory.Text = settings.DownloadDirectory; directory.Dock = DockStyle.Fill; directory.Enabled = false; destination.Controls.Add(directory, 1, 0);
            useDirectory.CheckedChanged += delegate { directory.Enabled = useDirectory.Checked; };
            var browse = new Button { Text = T("浏览…", "Browse…"), AutoSize = true };
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { SelectedPath = directory.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) { directory.Text = dialog.SelectedPath; useDirectory.Checked = true; } };
            destination.Controls.Add(browse, 2, 0);
            var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            queue.DropDownStyle = ComboBoxStyle.DropDownList; queue.Width = 210;
            queue.Items.Add(T("保留各项队列", "Keep individual queues")); queue.Items.Add(new QueueSettings { Id = "", Name = T("默认队列", "Default queue") });
            foreach (QueueSettings item in settings.Queues) queue.Items.Add(item); queue.SelectedIndex = 0;
            start.DropDownStyle = ComboBoxStyle.DropDownList; start.Width = 210;
            start.Items.AddRange(new[] { T("保留各项开始设置", "Keep individual start options"), T("立即加入运行队列", "Start when a slot is available"), T("稍后下载", "Download later") }); start.SelectedIndex = 0;
            choices.Controls.AddRange(new Control[] { new Label { Text = T("队列", "Queue"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, queue, start });
            destination.Controls.Add(choices, 0, 1); destination.SetColumnSpan(choices, 3); root.Controls.Add(destination, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 3) };
            addLinks.Text = T("添加链接…", "Add links…"); addLinks.AutoSize = true; addLinks.Click += AddLinks;
            var all = new Button { Text = T("勾选显示项", "Select visible"), AutoSize = true };
            var none = new Button { Text = T("取消显示项", "Clear visible"), AutoSize = true };
            all.Click += delegate { SetVisibleSelection(true); }; none.Click += delegate { SetVisibleSelection(false); };
            scanEnabled.Text = T("扫描链接响应头", "Scan link headers"); scanEnabled.AutoSize = true;
            scanEnabled.CheckedChanged += async delegate { if (scanEnabled.Checked && operation == null) await ScanAsync(); else if (!scanEnabled.Checked && operation != null) operation.Cancel(); };
            scan.Text = T("重新扫描", "Scan again"); scan.AutoSize = true;
            scan.Click += async delegate { if (operation != null) operation.Cancel(); else if (!scanEnabled.Checked) scanEnabled.Checked = true; else await ScanAsync(); };
            actions.Controls.AddRange(new Control[] { addLinks, all, none, scanEnabled, scan }); root.Controls.Add(actions, 0, 3);
            selection.AutoSize = true; selection.Dock = DockStyle.Fill; root.Controls.Add(selection, 0, 4);
            var feedback = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            progress.Dock = DockStyle.Top; feedback.Controls.Add(progress, 0, 0);
            status.AutoSize = true; status.Dock = DockStyle.Fill; status.Text = T("可跳过扫描。筛选不改变隐藏项的勾选状态。", "Scanning is optional. Filtering keeps hidden items selected."); feedback.Controls.Add(status, 0, 1);
            root.Controls.Add(feedback, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
            var cancel = new Button { Text = T("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            add.Text = T("添加所选项", "Add selected"); add.AutoSize = true; add.Click += Accept;
            buttons.Controls.Add(cancel); buttons.Controls.Add(add); root.Controls.Add(buttons, 0, 6);
            Controls.Add(root); AcceptButton = add; CancelButton = cancel;
            search.TextChanged += delegate { ApplyFilter(); }; extensions.TextChanged += delegate { ApplyFilter(); };
            category.SelectedIndexChanged += delegate { ApplyFilter(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (operation != null) { closeRequested = true; operation.Cancel(); e.Cancel = true; }
            };
            UpdateSelection();
            Appearance.Apply(this);
        }

        public static List<DownloadItem> ParseText(string text, AppSettings settings)
        {
            var result = new List<DownloadItem>();
            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = raw.IndexOf("||", StringComparison.Ordinal);
                string url = (separator < 0 ? raw : raw.Substring(0, separator)).Trim();
                string name = separator < 0 ? "" : raw.Substring(separator + 2).Trim();
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid output filename: " + name);
                foreach (string expanded in AddDialog.Expand(url))
                {
                    if (!AddDialog.ValidUrl(expanded)) throw new ArgumentException("Invalid download URL: " + expanded);
                    result.Add(new DownloadItem { Url = expanded, FileName = name, Directory = settings.DownloadDirectory,
                        Connections = settings.Connections, Seed = settings.SeedAfterDownload,
                        SaveSubtitles = settings.Media.SaveSubtitles, Kind = expanded.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || expanded.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? "torrent" : "file",
                        State = settings.AutoStartDownloads ? DownloadState.Queued : DownloadState.Paused });
                }
            }
            return result;
        }

        private void AddColumn(string name, string title, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, Width = width, ReadOnly = true });
        }

        private void AddEntries(IEnumerable<DownloadItem> items)
        {
            loading = true;
            foreach (DownloadItem item in items)
            {
                DownloadItem copy = item.Copy();
                int index = grid.Rows.Add(true, DisplayName(copy), Extension(copy), copy.TotalBytes > 0 ? SizeText(copy.TotalBytes) : T("未知", "Unknown"), copy.LinkText, copy.Url, T("未扫描", "Not scanned"));
                grid.Rows[index].Tag = new Entry { Item = copy, ExplicitName = !String.IsNullOrEmpty(copy.FileName) };
            }
            loading = false;
        }

        private void AddLinks(object sender, EventArgs args)
        {
            using (var dialog = new Form { Text = T("添加批量链接", "Add batch links"), Size = new Size(660, 360), StartPosition = FormStartPosition.CenterParent, Padding = new Padding(12) })
            {
                var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
                var help = new Label { Dock = DockStyle.Top, Height = 42, Text = T("每行一个链接。可写 URL || 文件名，数字范围如 [001-020]。", "One URL per line. Optional: URL || filename. Numbered ranges: [001-020].") };
                var ok = new Button { Dock = DockStyle.Bottom, Height = 32, Text = T("加入列表", "Add to list") };
                ok.Click += delegate
                {
                    try { AddEntries(ParseText(input.Text, settings)); dialog.DialogResult = DialogResult.OK; dialog.Close(); }
                    catch (Exception error)
                    {
                        if (!(error is ArgumentException) && !(error is FormatException) && !(error is OverflowException)) throw;
                        MessageBox.Show(dialog, Localization.Error(settings.Language, error.Message), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                dialog.Controls.Add(input); dialog.Controls.Add(help); dialog.Controls.Add(ok);
                Appearance.Apply(dialog);
                if (dialog.ShowDialog(this) == DialogResult.OK) ApplyFilter();
            }
        }

        private async Task ScanAsync()
        {
            operation = new CancellationTokenSource(); CancellationToken token = operation.Token;
            scan.Text = T("停止扫描", "Stop scanning"); add.Enabled = false; addLinks.Enabled = false;
            progress.Maximum = Math.Max(1, grid.Rows.Count); progress.Value = 0;
            int failures = 0;
            try
            {
                foreach (DataGridViewRow row in grid.Rows.Cast<DataGridViewRow>().ToArray())
                {
                    token.ThrowIfCancellationRequested(); Entry entry = (Entry)row.Tag;
                    row.Cells[6].Value = T("扫描中…", "Scanning…");
                    try
                    {
                        DownloadMetadata metadata = await TransferEngine.InspectMetadataAsync(entry.Item, settings, token);
                        token.ThrowIfCancellationRequested();
                        if (!entry.ExplicitName && metadata.FileName.Length > 0) entry.Item.FileName = metadata.FileName;
                        entry.ContentType = metadata.ContentType; entry.Item.TotalBytes = Math.Max(0, metadata.TotalBytes);
                        loading = true;
                        row.Cells[1].Value = DisplayName(entry.Item); row.Cells[2].Value = entry.ContentType.Length > 0 ? entry.ContentType : Extension(entry.Item);
                        row.Cells[3].Value = metadata.TotalBytes >= 0 ? SizeText(metadata.TotalBytes) : T("未知", "Unknown");
                        row.Cells[6].Value = T("完成", "Complete"); loading = false;
                    }
                    catch (OperationCanceledException) { row.Cells[6].Value = T("已取消", "Canceled"); throw; }
                    catch (Exception error) { row.Cells[6].Value = Localization.Error(settings.Language, error.Message); row.Cells[6].ToolTipText = Localization.Error(settings.Language, error.Message); failures++; }
                    progress.Value++; status.Text = progress.Value + " / " + grid.Rows.Count + T(" 项已扫描", " scanned");
                }
                status.Text = T("扫描完成；失败 ", "Scan finished; failed ") + failures + T(" 项。", ".");
            }
            catch (OperationCanceledException) { status.Text = T("扫描已停止；可继续添加所选项。", "Scan stopped. You can add the selected items."); }
            finally
            {
                operation.Dispose(); operation = null; loading = false;
                scan.Text = T("重新扫描", "Scan again"); addLinks.Enabled = true; ApplyFilter(); UpdateSelection();
                if (closeRequested) Close();
            }
        }

        private void ApplyFilter()
        {
            grid.EndEdit(); grid.CurrentCell = null;
            string text = search.Text.Trim();
            string[] types = extensions.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('.')).ToArray();
            CategorySettings selected = category.SelectedIndex > 0 ? settings.Categories[category.SelectedIndex - 1] : null;
            foreach (DataGridViewRow row in grid.Rows)
            {
                Entry entry = (Entry)row.Tag; string extension = Extension(entry.Item);
                string fields = DisplayName(entry.Item) + " " + entry.Item.Url + " " + entry.Item.LinkText;
                row.Visible = (text.Length == 0 || fields.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (types.Length == 0 || types.Contains(extension, StringComparer.OrdinalIgnoreCase)) &&
                    (selected == null || selected.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
            }
            UpdateSelection();
        }

        private void SetVisibleSelection(bool value)
        {
            grid.EndEdit(); foreach (DataGridViewRow row in grid.Rows) if (row.Visible) row.Cells[0].Value = value;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            int count = 0, hidden = 0, visible = 0;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Visible) visible++;
                if (Convert.ToBoolean(row.Cells[0].Value)) { count++; if (!row.Visible) hidden++; }
            }
            selection.Text = T("显示 ", "Visible ") + visible + " / " + grid.Rows.Count + T("；已选 ", "; selected ") + count + T("（隐藏项 ", " (hidden ") + hidden + ")";
            add.Enabled = count > 0 && operation == null;
        }

        private void Accept(object sender, EventArgs args)
        {
            grid.EndEdit(); var result = new List<DownloadItem>();
            if (useDirectory.Checked && (String.IsNullOrWhiteSpace(directory.Text) || !Path.IsPathRooted(directory.Text)))
            {
                MessageBox.Show(this, T("请选择完整保存路径。", "Choose an absolute download directory."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!Convert.ToBoolean(row.Cells[0].Value)) continue;
                DownloadItem item = ((Entry)row.Tag).Item;
                if (item.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(this, T("文件名包含无效字符：", "Invalid filename: ") + item.FileName, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
                }
                DownloadItem copy = item.Copy();
                if (useDirectory.Checked) { copy.Directory = directory.Text.Trim(); copy.DirectoryExplicit = true; }
                if (queue.SelectedIndex > 0) copy.QueueId = ((QueueSettings)queue.SelectedItem).Id;
                if (start.SelectedIndex > 0) copy.State = start.SelectedIndex == 1 ? DownloadState.Queued : DownloadState.Paused;
                result.Add(copy);
            }
            if (result.Count == 0) return;
            Result = result; DialogResult = DialogResult.OK; Close();
        }

        private static string DisplayName(DownloadItem item)
        {
            if (item.FileName.Length > 0) return item.FileName;
            Uri uri; return Uri.TryCreate(item.Url, UriKind.Absolute, out uri) ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) : Path.GetFileName(item.Url);
        }
        private static string Extension(DownloadItem item) { return Path.GetExtension(DisplayName(item)).TrimStart('.'); }
        private static string SizeText(long value) { return value.ToString("N0") + " B"; }
        private string T(string zh, string en) { return Localization.Text(settings.Language, en, zh); }
    }
}
