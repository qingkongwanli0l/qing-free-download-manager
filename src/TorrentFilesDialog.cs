using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class TorrentFilesDialog : Form
    {
        private readonly DownloadItem item;
        private readonly AppSettings settings;
        private readonly ListView files = new ListView();
        private readonly Label status = new Label();
        private readonly Button accept = new Button();
        private readonly Button reload = new Button();
        private CancellationTokenSource inspection;

        public string SelectedFiles { get; private set; }

        public TorrentFilesDialog(DownloadItem item, AppSettings settings)
        {
            this.item = item.Copy(); this.settings = settings.Copy();
            Text = T("选择种子中的文件", "Select torrent files"); Size = new Size(800, 590); MinimumSize = new Size(650, 440);
            Font = new Font("Segoe UI", 9F); AutoScaleMode = AutoScaleMode.Font; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var source = new TextBox { Text = item.Url, ReadOnly = true, Dock = DockStyle.Fill };
            panel.Controls.Add(source, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; files.CheckBoxes = true; files.FullRowSelect = true; files.HideSelection = false;
            files.Columns.Add(T("序号", "Index"), 65); files.Columns.Add(T("路径", "Path"), 500); files.Columns.Add(T("大小", "Size"), 150);
            files.ItemChecked += delegate { UpdateSelection(); }; panel.Controls.Add(files, 0, 1);
            status.AutoSize = true; status.MaximumSize = new Size(760, 75); status.AutoEllipsis = true; status.Margin = new Padding(3, 8, 3, 8); panel.Controls.Add(status, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var all = new Button { Text = T("全选", "Select all"), AutoSize = true };
            var none = new Button { Text = T("全不选", "Clear selection"), AutoSize = true };
            all.Click += delegate { foreach (ListViewItem row in files.Items) row.Checked = true; };
            none.Click += delegate { foreach (ListViewItem row in files.Items) row.Checked = false; };
            reload.Text = T("重新读取", "Reload"); reload.AutoSize = true; reload.Click += LoadFiles;
            accept.Text = T("使用所选文件", "Use selection"); accept.AutoSize = true; accept.Enabled = false;
            accept.Click += delegate
            {
                if (inspection != null || files.CheckedItems.Count == 0) return;
                SelectedFiles = String.Join(",", files.CheckedItems.Cast<ListViewItem>().Select(row => ((TorrentFileInfo)row.Tag).Index.ToString(CultureInfo.InvariantCulture)));
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = new Button { Text = T("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange(new Control[] { all, none, reload, accept, cancel }); panel.Controls.Add(buttons, 0, 3); Controls.Add(panel);
            AcceptButton = accept; CancelButton = cancel;
            Shown += LoadFiles;
            FormClosing += delegate { if (inspection != null) inspection.Cancel(); };
            Appearance.Apply(this);
        }

        private string T(string zh, string en) { return Localization.Text(settings.Language, en, zh); }

        private async void LoadFiles(object sender, EventArgs args)
        {
            if (inspection != null) return;
            var cancellation = new CancellationTokenSource(); inspection = cancellation;
            reload.Enabled = accept.Enabled = false; files.Items.Clear();
            status.Text = item.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? T("正在等待磁链元数据，尚未下载内容。可随时取消。", "Waiting for magnet metadata; content is not being downloaded. You can cancel at any time.")
                : T("正在读取种子文件…", "Reading torrent files…");
            try
            {
                TorrentInfo info = await TorrentTools.InspectAsync(item, settings, cancellation.Token);
                if (IsDisposed || Disposing || cancellation.IsCancellationRequested) return;
                Text = T("选择种子中的文件", "Select torrent files") + " · " + info.Name;
                files.BeginUpdate();
                foreach (var file in info.Files)
                {
                    var row = new ListViewItem(new[] { file.Index.ToString(CultureInfo.InvariantCulture), file.Path, file.Size.ToString("N0") + " B" }) { Tag = file, Checked = IsSelected(file.Index, item.SelectedFiles) };
                    files.Items.Add(row);
                }
                files.EndUpdate();
            }
            catch (OperationCanceledException) { if (!IsDisposed && !Disposing) status.Text = T("已取消读取。", "Reading canceled."); }
            catch (Exception error) { if (!IsDisposed && !Disposing) status.Text = T("读取失败：", "Unable to read: ") + Localization.Error(settings.Language, error.Message); }
            finally
            {
                inspection = null; cancellation.Dispose();
                if (!IsDisposed && !Disposing) { reload.Enabled = true; if (files.Items.Count > 0) UpdateSelection(); }
            }
        }

        private void UpdateSelection()
        {
            accept.Enabled = inspection == null && files.CheckedItems.Count > 0;
            if (inspection != null) return;
            long bytes = files.CheckedItems.Cast<ListViewItem>().Sum(row => ((TorrentFileInfo)row.Tag).Size);
            status.Text = files.CheckedItems.Count + T(" 个文件，", " file(s), ") + bytes.ToString("N0") + " B" + T("。共享数据块可能产生相邻文件的部分数据。", ". Shared pieces may leave partial data for adjacent files.");
        }

        private static bool IsSelected(int index, string selection)
        {
            if (String.IsNullOrWhiteSpace(selection)) return true;
            foreach (string token in selection.Split(','))
            {
                string[] range = token.Trim().Split('-'); int first, last;
                if (range.Length == 1 && Int32.TryParse(range[0], out first) && first == index) return true;
                if (range.Length == 2 && Int32.TryParse(range[0], out first) && Int32.TryParse(range[1], out last) && index >= first && index <= last) return true;
            }
            return false;
        }
    }
}
