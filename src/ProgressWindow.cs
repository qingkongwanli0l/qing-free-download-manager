using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class ProgressWindow : Form
    {
        readonly Func<AppSettings> settings;
        readonly Action save;
        readonly Action<DownloadItem, DownloadState> change;
        readonly ComboBox tasks = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox address = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        readonly CheckBox pin = new CheckBox { AutoSize = true };
        readonly CheckBox closeAfter = new CheckBox { AutoSize = true };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly ProgressBar audio = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label audioLabel = new Label { AutoSize = true };
        readonly Label detail = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(730, 0) };
        readonly ListView files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        readonly PictureBox animation = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
        byte[] displayedFrame;
        readonly Button open = new Button { AutoSize = true };
        readonly Timer timer = new Timer { Interval = 500 };
        DateTime? finishedAt;
        Point dragStart;
        bool showingFiles;
        string T(string zh, string en) { return Localization.Text(settings().Language, en, zh); }
        sealed class Choice
        {
            public DownloadItem Item;
            public override string ToString() { return String.IsNullOrEmpty(Item.FileName) ? Item.Url : Item.FileName; }
        }
        DownloadItem Current { get { return tasks.SelectedItem == null ? null : ((Choice)tasks.SelectedItem).Item; } }

        public ProgressWindow(Func<AppSettings> settings, Action save, Action<DownloadItem, DownloadState> change)
        {
            this.settings = settings; this.save = save; this.change = change;
            Text = T("下载进度", "Download progress"); Size = new Size(790, 380); MinimumSize = new Size(570, 360);
            Font = new Font("Segoe UI", 9F); StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(14) };
            for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(tasks, 0, 0); root.Controls.Add(address, 0, 1); root.Controls.Add(progress, 0, 2);
            audioLabel.Text = T("独立音轨", "Separate audio track"); root.Controls.Add(audioLabel, 0, 3); root.Controls.Add(audio, 0, 4); root.Controls.Add(detail, 0, 5);
            files.Columns.Add(T("种子内文件", "Torrent file"), 380); files.Columns.Add(T("大小", "Size"), 105); files.Columns.Add(T("已完成", "Completed"), 105); files.Columns.Add(T("选择", "Selected"), 70);
            var content = new Panel { Dock = DockStyle.Fill }; content.Controls.Add(files); content.Controls.Add(animation); root.Controls.Add(content, 0, 6);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            AddButton(actions, T("继续", "Resume"), () => SetState(DownloadState.Queued));
            AddButton(actions, T("暂停", "Pause"), () => SetState(DownloadState.Paused));
            AddButton(actions, T("停止", "Stop"), () => SetState(DownloadState.Stopped));
            open.Text = T("打开文件（可拖出）", "Open file (drag to copy)");
            open.Click += delegate { if (Current != null && File.Exists(Current.OutputPath)) Run(() => Process.Start(Current.OutputPath)); };
            open.MouseDown += delegate(object sender, MouseEventArgs e) { dragStart = e.Location; };
            open.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && (Math.Abs(e.X - dragStart.X) > SystemInformation.DragSize.Width / 2 || Math.Abs(e.Y - dragStart.Y) > SystemInformation.DragSize.Height / 2) && Current != null && Current.CompletedAt.HasValue && File.Exists(Current.OutputPath))
                    open.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { Current.OutputPath }), DragDropEffects.Copy);
            };
            actions.Controls.Add(open);
            AddButton(actions, T("文件夹", "Folder"), () => { if (Current != null) Process.Start("explorer.exe", "/select," + MainForm.Quote(Current.OutputPath)); });
            AddButton(actions, T("从此窗口移除", "Remove from window"), () => { if (tasks.SelectedIndex >= 0) tasks.Items.RemoveAt(tasks.SelectedIndex); if (tasks.Items.Count > 0) tasks.SelectedIndex = 0; else Close(); });
            root.Controls.Add(actions, 0, 7);
            var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            pin.Text = T("固定当前任务", "Pin current task"); options.Controls.Add(pin);
            var top = new CheckBox { Text = T("总在最前", "Always on top"), AutoSize = true }; top.CheckedChanged += delegate { TopMost = top.Checked; }; options.Controls.Add(top);
            closeAfter.Text = T("完成后自动关闭", "Close after completion"); closeAfter.Checked = settings().CloseProgressAfterDownload;
            closeAfter.CheckedChanged += delegate { settings().CloseProgressAfterDownload = closeAfter.Checked; save(); }; options.Controls.Add(closeAfter);
            var hide = new CheckBox { Text = T("以后不自动显示", "Do not open automatically"), AutoSize = true, Checked = !settings().ShowProgressWindow };
            hide.CheckedChanged += delegate { settings().ShowProgressWindow = !hide.Checked; save(); }; options.Controls.Add(hide);
            root.Controls.Add(options, 0, 8); Controls.Add(root);
            tasks.SelectedIndexChanged += delegate { RefreshProgress(); };
            timer.Tick += delegate { RefreshProgress(); }; timer.Start(); FormClosed += delegate { timer.Dispose(); if (animation.Image != null) animation.Image.Dispose(); };
            Appearance.Apply(this);
        }

        void AddButton(FlowLayoutPanel panel, string label, Action action) { var button = new Button { Text = label, AutoSize = true }; button.Click += delegate { Run(action); }; panel.Controls.Add(button); }
        void Run(Action action) { try { action(); } catch (Exception error) { MessageBox.Show(this, Localization.Error(settings().Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        void SetState(DownloadState state) { if (Current != null) { change(Current, state); RefreshProgress(); } }
        public void Track(DownloadItem item)
        {
            Choice existing = tasks.Items.Cast<Choice>().FirstOrDefault(x => x.Item.Id == item.Id);
            if (existing != null) { existing.Item = item; return; }
            var choice = new Choice { Item = item }; tasks.Items.Add(choice);
            if (!pin.Checked || tasks.SelectedIndex < 0) tasks.SelectedItem = choice;
            finishedAt = null; RefreshProgress();
        }

        static string SizeText(long bytes) { return bytes < 1024 ? bytes + " B" : bytes < 1048576 ? (bytes / 1024d).ToString("0.0") + " KiB" : (bytes / 1048576d).ToString("0.0") + " MiB"; }
        static void UpdateBar(ProgressBar bar, long completed, long total, bool running)
        {
            bar.Style = total <= 0 && running ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            bar.Value = total > 0 ? (int)Math.Min(1000, Math.Max(0, completed * 1000d / total)) : 0;
        }
        void RefreshProgress()
        {
            DownloadItem item = Current; if (item == null) return;
            string url = item.DisplayUrl(settings().ShowDirectUrls); if (address.Text != url) address.Text = url;
            bool running = item.State == DownloadState.Running;
            long total = item.MediaVideoTotal > 0 ? item.MediaVideoTotal : item.TotalBytes, received = item.MediaVideoTotal > 0 ? item.MediaVideoBytes : item.BytesReceived;
            UpdateBar(progress, received, total, running); UpdateBar(audio, item.MediaAudioBytes, item.MediaAudioTotal, running);
            audio.Visible = audioLabel.Visible = item.MediaAudioBytes > 0 || item.MediaAudioTotal > 0;
            string state = Localization.State(settings().Language, item.State);
            if (running && item.Phase == "processing") state = T("合并 / 转换", "Merging / converting");
            else if (running && item.Phase == "metadata") state = T("读取元数据", "Reading metadata");
            Text = (total > 0 ? (received * 100d / total).ToString("0.0") + "% · " : "") + state + " · " + (String.IsNullOrEmpty(item.FileName) ? T("下载进度", "Download progress") : item.FileName);
            string remaining = item.BytesPerSecond > 0 && total >= received ? TimeSpan.FromSeconds((total - received) / item.BytesPerSecond).ToString() : "—";
            detail.Text = item.OutputPath + Environment.NewLine + state + " · " + SizeText(received) + " / " + SizeText(total) + Environment.NewLine + T("速度：", "Speed: ") + SizeText(item.BytesPerSecond) + "/s   " + T("剩余：", "Remaining: ") + remaining + Environment.NewLine + Localization.Error(settings().Language, item.Error);
            if (item.UploadedBytes > 0 || item.UploadBytesPerSecond > 0) detail.Text += Environment.NewLine + T("已上传：", "Uploaded: ") + SizeText(item.UploadedBytes) + " · " + SizeText(item.UploadBytesPerSecond) + "/s";
            open.Enabled = item.CompletedAt.HasValue && File.Exists(item.OutputPath);
            List<TorrentFileProgress> snapshot = item.TorrentFiles; files.Visible = snapshot.Count > 0;
            byte[] frame = running && settings().Media.AnimateDownload ? item.AnimationFrame : null;
            if (!Object.ReferenceEquals(frame, displayedFrame))
            {
                Image next = null;
                if (frame != null)
                    try { using (var stream = new MemoryStream(frame)) using (Image image = Image.FromStream(stream)) next = new Bitmap(image); }
                    catch (ArgumentException) { }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                Image previous = animation.Image; animation.Image = next; if (previous != null) previous.Dispose(); displayedFrame = frame;
            }
            animation.Visible = animation.Image != null;
            bool expanded = files.Visible || animation.Visible;
            if (showingFiles != expanded) { showingFiles = expanded; Height = Math.Min(Screen.FromControl(this).WorkingArea.Height, (int)((showingFiles ? 640 : 380) * Font.SizeInPoints / 9F)); }
            files.BeginUpdate(); files.Items.Clear();
            foreach (TorrentFileProgress file in snapshot) files.Items.Add(new ListViewItem(new[] { file.Path, SizeText(file.TotalBytes), file.TotalBytes > 0 ? (100d * file.CompletedBytes / file.TotalBytes).ToString("0.0") + "%" : SizeText(file.CompletedBytes), file.Selected ? T("是", "Yes") : T("否", "No") }));
            files.EndUpdate();
            bool finished = tasks.Items.Cast<Choice>().All(x => x.Item.CompletedAt.HasValue && (x.Item.State == DownloadState.Complete || x.Item.State == DownloadState.Seeding));
            if (!finished) finishedAt = null;
            else if (!finishedAt.HasValue) finishedAt = DateTime.Now;
            if (finishedAt.HasValue && closeAfter.Checked && (DateTime.Now - finishedAt.Value).TotalSeconds >= settings().CloseProgressDelaySeconds) Close();
        }
    }
}
