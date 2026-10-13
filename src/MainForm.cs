using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QingDownloadManager
{
    public sealed class MainForm : Form
    {
        readonly Storage storage;
        AppSettings settings;
        readonly List<DownloadItem> downloads;
        readonly TransferEngine engine;
        readonly NativeBridge bridge;
        readonly System.Threading.CancellationTokenSource browserQueries = new System.Threading.CancellationTokenSource();
        readonly ListView list = new ListView();
        readonly TreeView tree = new TreeView();
        readonly TextBox search = new TextBox();
        readonly TextBox detail = new TextBox();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Timer timer = new Timer { Interval = 1000 };
        readonly HashSet<string> activeQueues = new HashSet<string>();
        readonly Dictionary<string, DateTime> notifiedCompletions = new Dictionary<string, DateTime>();
        readonly HashSet<string> notifiedErrors = new HashSet<string>();
        readonly HashSet<string> completedRemovals = new HashSet<string>();
        readonly Dictionary<string, Image> thumbnails = new Dictionary<string, Image>();
        readonly HashSet<string> requestedThumbnails = new HashSet<string>();
        readonly HttpClient thumbnailClient = new HttpClient(new HttpClientHandler { UseCookies = false });
        ProgressWindow progressWindow;
        LinkBasket linkBasket;
        VideoBox videoBox;
        RefreshAddressDialog refreshAddressDialog;
        DownloadItem refreshingItem;
        bool exiting, dirty, background, completionPending, allSoundPending, ticking, changingTaskView, checkingUpdates;
        string clipboard = "";
        int ticks;
        string filter = "all";
        string T(string zh, string en) { return Localization.Text(settings.Language, en, zh); }
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        public MainForm(Storage store, SavedState state, bool startBackground)
        {
            storage = store; settings = state.Settings; downloads = state.Downloads; background = startBackground || settings.StartMinimized;
            Appearance.Configure(settings);
            Text = "Qing Free Download Manager";
            Icon = AppIcons.Normal; Size = new Size(1120, 720); MinimumSize = new Size(800, 500);
            Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            TopMost = settings.AlwaysOnTop;
            BuildUi();
            Appearance.Apply(this);
            engine = new TransferEngine(storage.DirectoryPath, settings);
            engine.ConflictResolver = ChooseConflict;
            engine.Changed += EngineChanged;
            bridge = new NativeBridge(request => (Task<object>)Invoke(new Func<Task<object>>(() => ReceiveAsync(request))));
            timer.Tick += Tick;
            Shown += delegate
            {
                if (background) Hide(); timer.Start(); RefreshRows(); UpdateLinkBasket();
                if (settings.TorrentEnabled && settings.CheckTorrentAssociation) BeginInvoke(new Action(CheckTorrentAssociation));
            };
            FormClosing += OnClosing;
            Disposed += delegate { if (!browserQueries.IsCancellationRequested) browserQueries.Cancel(); browserQueries.Dispose(); thumbnailClient.Dispose(); foreach (Image image in thumbnails.Values) image.Dispose(); thumbnails.Clear(); };
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs e)
            {
                string text = e.Data.GetDataPresent(DataFormats.FileDrop) ? String.Join(Environment.NewLine, (string[])e.Data.GetData(DataFormats.FileDrop)) : Convert.ToString(e.Data.GetData(DataFormats.Text));
                AddText(text);
            };
        }

        ToolStripMenuItem MakeMenu(string title, Action action, Keys shortcut = Keys.None)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(title); item.Click += delegate { Run(action); }; item.ShortcutKeys = shortcut; return item;
        }

        void BuildUi()
        {
            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem(T("文件", "File"));
            file.DropDownItems.Add(MakeMenu(T("添加下载…", "Add downloads…"), () => Add(new List<DownloadItem>()), Keys.Control | Keys.N));
            file.DropDownItems.Add(MakeMenu(T("批量下载…", "Batch downloads…"), () => AddBatch(new List<DownloadItem>())));
            file.DropDownItems.Add(MakeMenu(T("从剪贴板添加…", "Add from clipboard…"), () => AddText(Clipboard.GetText()), Keys.Control | Keys.V));
            file.DropDownItems.Add(MakeMenu(T("打开种子…", "Open torrent…"), OpenTorrent));
            file.DropDownItems.Add(MakeMenu(T("导入链接…", "Import links…"), Import));
            file.DropDownItems.Add(MakeMenu(T("导出所选链接…", "Export selected links…"), Export));
            file.DropDownItems.Add(MakeMenu(T("导入设置与任务…", "Import settings and tasks…"), ImportState));
            file.DropDownItems.Add(MakeMenu(T("导出设置与任务…", "Export settings and tasks…"), ExportState));
            file.DropDownItems.Add(new ToolStripSeparator()); file.DropDownItems.Add(MakeMenu(T("退出", "Exit"), Exit, Keys.Alt | Keys.F4));
            ToolStripMenuItem tasks = new ToolStripMenuItem(T("任务", "Tasks"));
            tasks.DropDownItems.Add(MakeMenu(T("开始 / 继续", "Start / resume"), () => ChangeSelected(DownloadState.Queued), Keys.F5));
            tasks.DropDownItems.Add(MakeMenu(T("强制开始", "Force start"), ForceStart));
            tasks.DropDownItems.Add(MakeMenu(T("暂停", "Pause"), () => ChangeSelected(DownloadState.Paused), Keys.F6));
            tasks.DropDownItems.Add(MakeMenu(T("停止", "Stop"), () => ChangeSelected(DownloadState.Stopped)));
            tasks.DropDownItems.Add(MakeMenu(T("设置所选任务限速…", "Set selected download speed limit…"), SetSelectedSpeedLimit));
            tasks.DropDownItems.Add(MakeMenu(T("全部开始", "Start all"), () => ChangeAll(DownloadState.Queued)));
            tasks.DropDownItems.Add(MakeMenu(T("全部暂停", "Pause all"), () => ChangeAll(DownloadState.Paused)));
            tasks.DropDownItems.Add(MakeMenu(T("全部停止", "Stop all"), () => ChangeAll(DownloadState.Stopped)));
            tasks.DropDownItems.Add(MakeMenu(T("属性 / 更新链接…", "Properties / refresh URL…"), Edit, Keys.Alt | Keys.Enter));
            tasks.DropDownItems.Add(MakeMenu(T("在浏览器中刷新地址…", "Refresh address in browser…"), RefreshAddress));
            tasks.DropDownItems.Add(MakeMenu(T("校验文件摘要…", "Check file hashes…"), CheckHashes));
            tasks.DropDownItems.Add(MakeMenu(T("重命名已下载文件…", "Rename downloaded file…"), RenameFile, Keys.F2));
            tasks.DropDownItems.Add(MakeMenu(T("重新下载", "Download again"), DownloadAgain));
            tasks.DropDownItems.Add(MakeMenu(T("启用 / 禁用任务", "Enable / disable tasks"), ToggleEnabled));
            tasks.DropDownItems.Add(MakeMenu(T("任务计划…", "Schedule downloads…"), EditSchedule));
            tasks.DropDownItems.Add(MakeMenu(T("下载进度…", "Download progress…"), () => ShowProgress(Selected())));
            tasks.DropDownItems.Add(MakeMenu(T("移至历史", "Move to history"), () => Delete(false), Keys.Delete));
            tasks.DropDownItems.Add(MakeMenu(T("删除任务（不保留历史）", "Remove tasks without history"), () => RemoveItems(Selected(), false, false), Keys.Control | Keys.Delete));
            tasks.DropDownItems.Add(MakeMenu(T("移除全部已完成任务", "Remove all completed tasks"), () => RemoveItems(downloads.Where(x => x.State == DownloadState.Complete).ToList(), false, true)));
            tasks.DropDownItems.Add(MakeMenu(T("删除文件与任务…", "Delete files and tasks…"), () => Delete(true), Keys.Control | Keys.Shift | Keys.Delete));
            ToolStripMenuItem queues = new ToolStripMenuItem(T("队列", "Queues"));
            queues.DropDownItems.Add(MakeMenu(T("新建队列…", "New queue…"), () => EditQueue(null)));
            queues.DropDownItems.Add(MakeMenu(T("编辑当前队列…", "Edit current queue…"), () => EditQueue(CurrentQueue())));
            queues.DropDownItems.Add(MakeMenu(T("开始当前队列", "Start current queue"), () => SetQueue(true)));
            queues.DropDownItems.Add(MakeMenu(T("停止当前队列", "Stop current queue"), () => SetQueue(false)));
            queues.DropDownItems.Add(MakeMenu(T("移动所选任务到队列…", "Move selection to queue…"), MoveQueue));
            queues.DropDownItems.Add(MakeMenu(T("任务上移", "Move downloads up"), () => MoveInQueue(-1), Keys.Control | Keys.Up));
            queues.DropDownItems.Add(MakeMenu(T("任务下移", "Move downloads down"), () => MoveInQueue(1), Keys.Control | Keys.Down));
            ToolStripMenuItem tools = new ToolStripMenuItem(T("工具", "Tools"));
            tools.DropDownItems.Add(MakeMenu(T("媒体与播放列表…", "Media and playlists…"), Media));
            tools.DropDownItems.Add(MakeMenu(T("转换媒体…", "Convert media…"), ConvertMedia));
            tools.DropDownItems.Add(MakeMenu(T("第三方下载账号…", "Debrid accounts…"), DebridAccounts));
            tools.DropDownItems.Add(MakeMenu(T("浏览器集成…", "Browser integration…"), BrowserIntegration));
            tools.DropDownItems.Add(MakeMenu(T("注册种子与磁链打开方式…", "Register torrent and magnet handler…"), RegisterFileTypes));
            tools.DropDownItems.Add(MakeMenu(T("设置…", "Settings…"), Settings));
            ToolStripMenuItem help = new ToolStripMenuItem(T("帮助", "Help"));
            ToolStripMenuItem view = new ToolStripMenuItem(T("查看", "View"));
            var onTop = new ToolStripMenuItem(T("总在最前", "Always on top")) { CheckOnClick = true, Checked = settings.AlwaysOnTop };
            onTop.CheckedChanged += delegate { TopMost = settings.AlwaysOnTop = onTop.Checked; dirty = true; };
            view.DropDownOpening += delegate { onTop.Checked = settings.AlwaysOnTop; };
            view.DropDownItems.Add(onTop);
            var basket = new ToolStripMenuItem(T("链接篮", "Link basket")) { CheckOnClick = true, Checked = settings.ShowLinkBasket };
            basket.CheckedChanged += delegate { settings.ShowLinkBasket = basket.Checked; UpdateLinkBasket(); Save(); };
            view.DropDownOpening += delegate { basket.Checked = settings.ShowLinkBasket; }; view.DropDownItems.Add(basket);
            ToolStripMenuItem columns = new ToolStripMenuItem(T("显示列", "Visible columns")); view.DropDownItems.Add(columns);
            view.DropDownItems.Add(TaskViewMenu());
            view.DropDownItems.Add(MakeMenu(T("搜索当前分组", "Search current group"), () => { search.Focus(); search.SelectAll(); }, Keys.Control | Keys.F));
            view.DropDownItems.Add(MakeMenu(T("查找下一项", "Find next"), () => FindResult(1), Keys.F3));
            view.DropDownItems.Add(MakeMenu(T("查找上一项", "Find previous"), () => FindResult(-1), Keys.Shift | Keys.F3));
            help.DropDownItems.Add(MakeMenu(T("使用说明", "User guide"), () => Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md"))));
            help.DropDownItems.Add(MakeMenu(T("检查更新…", "Check for updates…"), () => CheckForUpdates(false)));
            help.DropDownItems.Add(MakeMenu(T("关于", "About"), () => MessageBox.Show(this, "Qing Free Download Manager\n" + Application.ProductVersion + "\nMIT\nhttps://github.com/qingkongwanli0l/qing-free-download-manager", Text)));
            menu.Items.AddRange(new ToolStripItem[] { file, tasks, queues, view, tools, help }); MainMenuStrip = menu;
            ToolStrip toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8), BackColor = Color.FromArgb(235, 244, 248) };
            AddButton(toolbar, T("＋ 添加", "+ Add"), () => Add(new List<DownloadItem>()));
            AddButton(toolbar, T("开始", "Start"), () => ChangeSelected(DownloadState.Queued));
            AddButton(toolbar, T("暂停", "Pause"), () => ChangeSelected(DownloadState.Paused));
            AddButton(toolbar, T("移除", "Remove"), () => Delete(false));
            toolbar.Items.Add(new ToolStripSeparator()); AddButton(toolbar, T("媒体", "Media"), Media);
            AddButton(toolbar, T("设置", "Settings"), Settings);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Width = ClientSize.Width, SplitterDistance = 210, FixedPanel = FixedPanel.Panel1 };
            tree.Dock = DockStyle.Fill; tree.BorderStyle = BorderStyle.None; tree.HideSelection = false;
            tree.AfterSelect += delegate { filter = Convert.ToString(tree.SelectedNode.Tag); RefreshRows(); };
            tree.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs e) { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            var treeMenu = new ContextMenuStrip();
            treeMenu.Items.Add(MakeMenu(T("打开分类目录", "Open category folder"), () => CategoryFolder(false)));
            treeMenu.Items.Add(MakeMenu(T("更改分类目录…", "Change category folder…"), () => CategoryFolder(true)));
            treeMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = CurrentCategory() == null; };
            tree.ContextMenuStrip = treeMenu;
            split.Panel1.Controls.Add(tree);
            search.Dock = DockStyle.Top; search.Height = 30; search.TextChanged += delegate { RefreshRows(); };
            Label searchLabel = new Label { Text = T("搜索名称或链接", "Search name or URL"), Dock = DockStyle.Top, Height = 24, Padding = new Padding(4) };
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.BorderStyle = BorderStyle.None;
            list.Columns.Add(T("文件", "File"), 260); list.Columns.Add(T("进度", "Progress"), 80); list.Columns.Add(T("状态", "State"), 95);
            list.Columns.Add(T("大小", "Size"), 90); list.Columns.Add(T("速度", "Speed"), 90); list.Columns.Add(T("剩余", "Remaining"), 90); list.Columns.Add(T("队列", "Queue"), 100);
            for (int index = 0; index < list.Columns.Count; index++)
            {
                int column = index, defaultWidth = list.Columns[index].Width;
                if (index < settings.ColumnWidths.Count) list.Columns[index].Width = settings.ColumnWidths[index];
                var toggle = new ToolStripMenuItem(list.Columns[index].Text) { CheckOnClick = true, Checked = list.Columns[index].Width > 0, Enabled = index > 0 };
                toggle.CheckedChanged += delegate { list.Columns[column].Width = toggle.Checked ? defaultWidth : 0; };
                columns.DropDownOpening += delegate { toggle.Checked = list.Columns[column].Width > 0; };
                columns.DropDownItems.Add(toggle);
            }
            list.ColumnWidthChanged += delegate(object sender, ColumnWidthChangedEventArgs e) { settings.ColumnWidths[e.ColumnIndex] = list.Columns[e.ColumnIndex].Width; dirty = true; };
            list.SelectedIndexChanged += delegate { ShowDetails(); }; list.DoubleClick += delegate { Run(OpenSelected); };
            list.DrawItem += DrawThumbnailItem;
            list.FontChanged += delegate { SetTaskView(settings.TaskView, false); };
            list.ClientSizeChanged += delegate { if (settings.TaskView != "table") SetTaskView(settings.TaskView, false); };
            list.ItemDrag += delegate
            {
                string[] paths = Selected().Where(x => x.CompletedAt.HasValue && File.Exists(x.OutputPath)).Select(x => x.OutputPath).Distinct().ToArray();
                if (paths.Length > 0) list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
            };
            list.ColumnClick += delegate(object sender, ColumnClickEventArgs e) { list.ListViewItemSorter = new RowComparer(e.Column, list.Sorting != SortOrder.Ascending); list.Sorting = list.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending; list.Sort(); };
            ContextMenuStrip context = new ContextMenuStrip();
            context.Items.Add(MakeMenu(T("打开", "Open"), OpenSelected));
            context.Items.Add(MakeMenu(T("打开文件夹", "Open folder"), OpenFolder));
            context.Items.Add(MakeMenu(T("校验文件摘要…", "Check file hashes…"), CheckHashes));
            context.Items.Add(MakeMenu(T("重命名文件…", "Rename file…"), RenameFile));
            context.Items.Add(MakeMenu(T("拼接所选媒体…", "Merge selected media…"), () => ConvertMedia(MediaOperation.Merge)));
            context.Items.Add(MakeMenu(T("重新下载", "Download again"), DownloadAgain));
            context.Items.Add(MakeMenu(T("在浏览器中刷新地址…", "Refresh address in browser…"), RefreshAddress));
            context.Items.Add(MakeMenu(T("启用 / 禁用", "Enable / disable"), ToggleEnabled));
            context.Items.Add(MakeMenu(T("复制原始链接", "Copy original URLs"), () => Clipboard.SetText(String.Join(Environment.NewLine, Selected().Select(x => x.Url)))));
            context.Items.Add(MakeMenu(T("提高优先级", "Higher priority"), () => { foreach (DownloadItem item in Selected()) item.Priority++; dirty = true; }));
            context.Items.Add(MakeMenu(T("降低优先级", "Lower priority"), () => { foreach (DownloadItem item in Selected()) item.Priority--; dirty = true; }));
            context.Items.Add(MakeMenu(T("在队列中上移", "Move up in queue"), () => MoveInQueue(-1)));
            context.Items.Add(MakeMenu(T("在队列中下移", "Move down in queue"), () => MoveInQueue(1)));
            context.Items.Add(MakeMenu(T("属性…", "Properties…"), Edit));
            context.Items.Add(MakeMenu(T("任务计划…", "Schedule downloads…"), EditSchedule));
            context.Items.Add(MakeMenu(T("下载进度…", "Download progress…"), () => ShowProgress(Selected())));
            context.Items.Add(MakeMenu(T("设置所选任务限速…", "Set selected download speed limit…"), SetSelectedSpeedLimit));
            context.Items.Add(TaskViewMenu());
            context.Items.Add(MakeMenu(T("恢复到任务列表", "Restore from history"), () => ChangeSelected(DownloadState.Paused)));
            context.Items.Add(MakeMenu(T("移除", "Remove"), () => Delete(false))); list.ContextMenuStrip = context;
            detail.Dock = DockStyle.Bottom; detail.Height = 115; detail.Multiline = true; detail.ReadOnly = true; detail.ScrollBars = ScrollBars.Vertical; detail.BackColor = Color.FromArgb(248, 250, 252); detail.BorderStyle = BorderStyle.None;
            split.Panel2.Controls.Add(list); split.Panel2.Controls.Add(detail); split.Panel2.Controls.Add(search); split.Panel2.Controls.Add(searchLabel);
            StatusStrip strip = new StatusStrip(); strip.Items.Add(status);
            Controls.Add(split); Controls.Add(toolbar); Controls.Add(menu); Controls.Add(strip);
            tray.Icon = Icon; tray.Text = "Qing Free Download Manager"; tray.Visible = true;
            tray.DoubleClick += delegate { ShowMain(); };
            tray.ContextMenuStrip = new ContextMenuStrip(); tray.ContextMenuStrip.Items.Add(MakeMenu(T("打开", "Open"), ShowMain));
            tray.ContextMenuStrip.Items.Add(MakeMenu(T("全部暂停", "Pause all"), () => ChangeAll(DownloadState.Paused))); tray.ContextMenuStrip.Items.Add(MakeMenu(T("退出", "Exit"), Exit));
            BuildTree();
            SetTaskView(settings.TaskView, false);
        }

        ToolStripMenuItem TaskViewMenu()
        {
            var menu = new ToolStripMenuItem(T("任务视图", "Task view"));
            string[] values = { "table", "small", "medium", "large" };
            string[] labels = { T("表格", "Table"), T("小缩略图", "Small thumbnails"), T("中缩略图", "Medium thumbnails"), T("大缩略图", "Large thumbnails") };
            for (int index = 0; index < values.Length; index++)
            {
                string value = values[index];
                var choice = MakeMenu(labels[index], () => SetTaskView(value, true)); choice.Tag = value; menu.DropDownItems.Add(choice);
            }
            menu.DropDownOpening += delegate { foreach (ToolStripMenuItem choice in menu.DropDownItems) choice.Checked = Convert.ToString(choice.Tag) == settings.TaskView; };
            return menu;
        }

        void SetTaskView(string value, bool save)
        {
            if (changingTaskView) return;
            changingTaskView = true;
            if (value != "small" && value != "medium" && value != "large") value = "table";
            settings.TaskView = value;
            list.BeginUpdate();
            list.View = View.Details; list.OwnerDraw = value != "table";
            if (value != "table")
            {
                int width = ThumbnailWidth();
                int tileWidth = width + Math.Max(200, list.Font.Height * 14) + 30;
                if (list.ClientSize.Width > 100) tileWidth = Math.Min(tileWidth, list.ClientSize.Width - 6);
                list.TileSize = new Size(Math.Max(100, tileWidth), Math.Max(width * 9 / 16 + 20, list.Font.Height * 5 + 22));
                list.View = View.Tile;
            }
            list.EndUpdate();
            changingTaskView = false;
            list.Invalidate();
            if (save) Save();
        }

        int ThumbnailWidth() { return settings.TaskView == "small" ? 96 : settings.TaskView == "large" ? 224 : 160; }

        void DrawThumbnailItem(object sender, DrawListViewItemEventArgs args)
        {
            DownloadItem item = (DownloadItem)args.Item.Tag;
            Rectangle bounds = Rectangle.Inflate(args.Bounds, -4, -4);
            bool selected = args.Item.Selected;
            Color background = selected ? SystemColors.Highlight : list.BackColor;
            Color text = selected ? SystemColors.HighlightText : list.ForeColor;
            using (var brush = new SolidBrush(background)) args.Graphics.FillRectangle(brush, args.Bounds);
            int imageWidth = Math.Min(ThumbnailWidth(), Math.Max(32, bounds.Width / 2));
            Rectangle picture = new Rectangle(bounds.Left + 4, bounds.Top + 4, imageWidth, imageWidth * 9 / 16);
            using (var brush = new SolidBrush(settings.AppearanceTheme == "dark" ? Color.FromArgb(51, 63, 74) : Color.FromArgb(225, 234, 241))) args.Graphics.FillRectangle(brush, picture);
            Image image;
            if (!String.IsNullOrEmpty(item.ThumbnailUrl) && thumbnails.TryGetValue(item.ThumbnailUrl, out image))
            {
                float ratio = Math.Min((float)picture.Width / image.Width, (float)picture.Height / image.Height);
                int width = Math.Max(1, (int)(image.Width * ratio)), height = Math.Max(1, (int)(image.Height * ratio));
                args.Graphics.DrawImage(image, picture.Left + (picture.Width - width) / 2, picture.Top + (picture.Height - height) / 2, width, height);
            }
            else
            {
                TextRenderer.DrawText(args.Graphics, item.Kind == "media" || item.Kind == "audio" ? T("媒体", "Media") : T("文件", "File"), list.Font, picture, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (!String.IsNullOrEmpty(item.ThumbnailUrl) && requestedThumbnails.Add(item.ThumbnailUrl)) LoadTaskThumbnail(item.ThumbnailUrl);
            }
            int x = picture.Right + 12, widthText = Math.Max(20, bounds.Right - x - 4), y = bounds.Top + 4, line = list.Font.Height + 2;
            string title = !String.IsNullOrEmpty(item.MediaTitle) ? item.MediaTitle : args.Item.Text;
            TextRenderer.DrawText(args.Graphics, title, list.Font, new Rectangle(x, y, widthText, line * 2), text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += line * 2;
            List<string> details = new List<string>();
            string extension = Path.GetExtension(item.FileName).TrimStart('.'); if (extension.Length > 0) details.Add(extension.ToUpperInvariant());
            if (!String.IsNullOrEmpty(item.MediaQuality)) details.Add(item.MediaQuality);
            if (item.MediaDurationSeconds > 0) details.Add(TimeSpan.FromSeconds(item.MediaDurationSeconds).ToString(@"hh\:mm\:ss"));
            if (item.MediaViewCount.HasValue) details.Add(item.MediaViewCount.Value.ToString("N0") + T(" 次观看", " views"));
            TextRenderer.DrawText(args.Graphics, String.Join(" · ", details), list.Font, new Rectangle(x, y, widthText, line), text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix); y += line;
            string state = item.Phase == "processing" ? T("正在处理媒体", "Processing media") : args.Item.SubItems[2].Text;
            TextRenderer.DrawText(args.Graphics, state + " · " + args.Item.SubItems[1].Text, list.Font, new Rectangle(x, y, widthText, line), text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            Rectangle progress = new Rectangle(x, bounds.Bottom - 8, widthText, 5);
            using (var brush = new SolidBrush(settings.AppearanceTheme == "dark" ? Color.FromArgb(70, 82, 91) : Color.FromArgb(215, 225, 230))) args.Graphics.FillRectangle(brush, progress);
            double fraction = item.TotalBytes > 0 ? Math.Max(0, Math.Min(1, (double)item.BytesReceived / item.TotalBytes)) : 0;
            Color progressColor = item.Phase == "processing" || item.State == DownloadState.Complete ? Color.FromArgb(30, 152, 118) : Color.FromArgb(214, 151, 35);
            using (var brush = new SolidBrush(progressColor)) args.Graphics.FillRectangle(brush, progress.Left, progress.Top, (int)(progress.Width * fraction), progress.Height);
            if (args.Item.Focused) args.DrawFocusRectangle();
        }

        async void LoadTaskThumbnail(string url)
        {
            try
            {
                Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "http" && uri.Scheme != "https") return;
                using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (HttpResponseMessage response = await thumbnailClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                using (timeout.Token.Register(response.Dispose))
                {
                    response.EnsureSuccessStatusCode(); if (response.Content.Headers.ContentLength > 2097152) return;
                    using (Stream input = await response.Content.ReadAsStreamAsync())
                    using (var bytes = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192]; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                        { if (bytes.Length + count > 2097152) return; bytes.Write(buffer, 0, count); }
                        if (IsDisposed || Disposing) return;
                        bytes.Position = 0;
                        using (Image image = Image.FromStream(bytes))
                        {
                            float ratio = Math.Min(224F / image.Width, 224F / image.Height);
                            thumbnails[url] = new Bitmap(image, new Size(Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio))));
                        }
                        list.Invalidate();
                    }
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
        }

        void SetSelectedSpeedLimit()
        {
            List<DownloadItem> items = Selected(); if (items.Count == 0) return;
            using (var dialog = new Form { Text = T("所选任务限速", "Selected download speed limit"), ClientSize = new Size(380, 155), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Text = T("KiB/s；0 表示仅受全局限制", "KiB/s; 0 uses only the global limit"), Dock = DockStyle.Top, Height = 40, Padding = new Padding(12) };
                var speed = new NumericUpDown { Dock = DockStyle.Top, Minimum = 0, Maximum = Int32.MaxValue, Value = Math.Min(Int32.MaxValue, Math.Max(0, items[0].SpeedLimit / 1024)) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
                var cancel = new Button { Text = T("取消", "Cancel"), DialogResult = DialogResult.Cancel };
                var save = new Button { Text = T("确定", "OK"), DialogResult = DialogResult.OK }; buttons.Controls.AddRange(new Control[] { cancel, save });
                dialog.Controls.Add(speed); dialog.Controls.Add(label); dialog.Controls.Add(buttons); dialog.AcceptButton = save; dialog.CancelButton = cancel;
                Appearance.Apply(dialog); if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (DownloadItem item in items) engine.SetSpeedLimit(item, (long)speed.Value * 1024);
                Save(); ShowDetails();
            }
        }

        void AddButton(ToolStrip bar, string label, Action action) { ToolStripButton b = new ToolStripButton(label) { DisplayStyle = ToolStripItemDisplayStyle.Text }; b.Click += delegate { Run(action); }; bar.Items.Add(b); }
        void Run(Action action) { try { action(); } catch (Exception error) { MessageBox.Show(this, Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        List<DownloadItem> Selected() { return list.SelectedItems.Cast<ListViewItem>().Select(x => (DownloadItem)x.Tag).ToList(); }
        static bool Active(DownloadItem item) { return item.State == DownloadState.Running || item.State == DownloadState.Seeding; }
        static string SizeText(long value) { string[] units = { "B", "KiB", "MiB", "GiB", "TiB" }; double n = value; int i = 0; while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; } return n.ToString(i == 0 ? "0" : "0.0") + " " + units[i]; }
        string StateText(DownloadState state) { return Localization.State(settings.Language, state); }

        void BuildTree()
        {
            tree.BeginUpdate(); tree.Nodes.Clear();
            tree.Nodes.Add(new TreeNode(T("全部下载", "All downloads")) { Tag = "all" });
            foreach (DownloadState state in Enum.GetValues(typeof(DownloadState)))
            {
                TreeNode node = new TreeNode(StateText(state)) { Tag = "state:" + state }; tree.Nodes.Add(node);
                if (state == DownloadState.History)
                    foreach (DateTime date in downloads.Where(x => x.State == DownloadState.History).Select(HistoryDate).Select(x => x.Date).Distinct().OrderByDescending(x => x))
                        node.Nodes.Add(new TreeNode(date.ToString("yyyy-MM-dd")) { Tag = "history:" + date.ToString("yyyy-MM-dd") });
            }
            tree.Nodes.Add(new TreeNode(T("计划下载", "Scheduled downloads")) { Tag = "scheduled" });
            TreeNode categories = tree.Nodes.Add(T("分类", "Categories")); categories.Tag = "all";
            foreach (CategorySettings category in settings.Categories) categories.Nodes.Add(new TreeNode(category.Name) { Tag = "category:" + category.Name });
            TreeNode queues = tree.Nodes.Add(T("队列", "Queues")); queues.Tag = "all";
            queues.Nodes.Add(new TreeNode(T("默认队列", "Default queue")) { Tag = "queue:" });
            foreach (QueueSettings queue in settings.Queues) queues.Nodes.Add(new TreeNode(queue.Name) { Tag = "queue:" + queue.Id });
            tree.ExpandAll(); tree.EndUpdate();
        }

        void RefreshRows()
        {
            Icon activityIcon = settings.AnimateIcon && ticks % 2 != 0 && downloads.Any(Active) ? AppIcons.Active : AppIcons.Normal;
            if (Icon != activityIcon || tray.Icon != activityIcon) Icon = tray.Icon = activityIcon;
            HashSet<string> selected = new HashSet<string>(Selected().Select(x => x.Id));
            list.BeginUpdate(); list.Items.Clear();
            foreach (DownloadItem item in filter.StartsWith("queue:") ? downloads.OrderByDescending(x => x.Priority) : (IEnumerable<DownloadItem>)downloads)
            {
                bool history = filter == "state:History" || filter.StartsWith("history:");
                if (!history && item.State == DownloadState.History) continue;
                if (filter.StartsWith("history:") && (item.State != DownloadState.History || HistoryDate(item).ToString("yyyy-MM-dd") != filter.Substring(8))) continue;
                if (filter == "scheduled" && (item.Schedule == null || item.Schedule.ScheduleMode == ScheduleMode.Manual)) continue;
                if (filter.StartsWith("state:") && item.State.ToString() != filter.Substring(6)) continue;
                if (filter.StartsWith("queue:") && item.QueueId != filter.Substring(6)) continue;
                if (filter.StartsWith("category:") && item.Category != filter.Substring(9)) continue;
                if (!String.IsNullOrEmpty(search.Text) && (item.FileName + item.Url).IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string name = String.IsNullOrEmpty(item.FileName) ? item.Url : item.FileName;
                string progress = item.TotalBytes > 0 ? (Math.Min(1, (double)item.BytesReceived / item.TotalBytes) * 100).ToString("0.0") + "%" : SizeText(item.BytesReceived);
                string remaining = item.BytesPerSecond > 0 && item.TotalBytes >= item.BytesReceived ? TimeSpan.FromSeconds((item.TotalBytes - item.BytesReceived) / item.BytesPerSecond).ToString(@"d\.hh\:mm\:ss") : "";
                QueueSettings queue = settings.Queues.FirstOrDefault(x => x.Id == item.QueueId);
                ListViewItem row = new ListViewItem(new[] { name, progress, item.Enabled ? StateText(item.State) : T("已禁用", "Disabled"), item.TotalBytes > 0 ? SizeText(item.TotalBytes) : "—", item.BytesPerSecond > 0 ? SizeText(item.BytesPerSecond) + "/s" : "", remaining, queue == null ? "" : queue.Name }) { Tag = item, Selected = selected.Contains(item.Id) };
                if (item.State == DownloadState.Error) row.ForeColor = settings.AppearanceTheme == "dark" ? Color.FromArgb(255, 145, 145) : Color.Firebrick;
                if (item.State == DownloadState.Complete) row.ForeColor = settings.AppearanceTheme == "dark" ? Color.FromArgb(118, 218, 184) : Color.FromArgb(20, 110, 85);
                list.Items.Add(row);
            }
            list.EndUpdate();
            status.Text = T("任务 ", "Tasks ") + downloads.Count + "   |   " + downloads.Count(Active) + T(" 个活动任务", " active") + "   |   " + SizeText(downloads.Sum(x => x.BytesPerSecond)) + "/s";
            if (engine != null && !String.IsNullOrEmpty(engine.PortMappingStatus)) status.Text += "   |   " + engine.PortMappingStatus;
        }

        void ShowDetails()
        {
            DownloadItem item = Selected().FirstOrDefault();
            detail.Text = item == null ? "" : item.DisplayUrl(settings.ShowDirectUrls) + Environment.NewLine + item.OutputPath + Environment.NewLine + StateText(item.State) + " · " + SizeText(item.BytesReceived) + " / " + SizeText(item.TotalBytes) + " · " + item.Connections + T(" 个连接", " connections") + Environment.NewLine + Localization.Error(settings.Language, item.Error);
            if (item != null && !String.IsNullOrEmpty(item.MediaTitle))
                detail.AppendText(Environment.NewLine + item.MediaTitle + " · " + item.MediaQuality +
                    (item.MediaDurationSeconds > 0 ? " · " + TimeSpan.FromSeconds(item.MediaDurationSeconds).ToString(@"hh\:mm\:ss") : "") +
                    (item.MediaViewCount.HasValue ? " · " + item.MediaViewCount.Value.ToString("N0") + T(" 次观看", " views") : ""));
            if (item != null && item.MediaFiles.Count > 0)
                detail.AppendText(Environment.NewLine + MediaFileInformation.Describe(item.MediaFiles, settings.Language));
        }

        void FindResult(int direction)
        {
            if (list.Items.Count == 0) return;
            int current = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : direction > 0 ? -1 : 0;
            int next = (current + direction + list.Items.Count) % list.Items.Count;
            foreach (ListViewItem row in list.SelectedItems) row.Selected = false;
            list.Items[next].Selected = true; list.Items[next].Focused = true; list.Items[next].EnsureVisible(); list.Focus();
        }

        public void AddText(string text)
        {
            List<DownloadItem> items = BatchDialog.ParseText(text, settings);
            if (items.Count > 0) Add(items);
        }

        List<DownloadItem> ClipboardItems(string text)
        {
            try
            {
                List<DownloadItem> items = BatchDialog.ParseText(text, settings);
                if (settings.ClipboardHighSensitivity) return items;
                return items.Where(item => item.Kind == "torrent" || settings.Extensions.Contains(Path.GetExtension(new Uri(item.Url).AbsolutePath).TrimStart('.'), StringComparer.OrdinalIgnoreCase)).ToList();
            }
            catch (ArgumentException) { return new List<DownloadItem>(); }
            catch (FormatException) { return new List<DownloadItem>(); }
            catch (OverflowException) { return new List<DownloadItem>(); }
        }

        DownloadItem NewItem(string url)
        {
            return new DownloadItem { Url = url, Directory = settings.DownloadDirectory, Connections = MediaTools.IsYouTubeUrl(url) ? settings.Media.YouTubeConnections : settings.Connections,
                Seed = settings.SeedAfterDownload, SaveSubtitles = settings.Media.SaveSubtitles, State = settings.AutoStartDownloads ? DownloadState.Queued : DownloadState.Paused };
        }

        async void Add(List<DownloadItem> items)
        {
            try { await AddAsync(items); }
            catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        async Task<int> AddAsync(List<DownloadItem> items)
        {
            if (items.Count > 1) return AddBatch(items);
            ShowMain(); using (AddDialog dialog = new AddDialog(settings, items, engine))
            {
                WindowPlacement.Attach(dialog, () => settings, Save);
                int accepted = 0;
                Exception failure = null;
                try { if (dialog.ShowDialog(this) == DialogResult.OK) accepted = Insert(dialog.Result); }
                catch (Exception error) { failure = error; }
                if (dialog.PreDownloadItem != null && !downloads.Any(x => x.Id == dialog.PreDownloadItem.Id))
                    await engine.ResetPartialAsync(dialog.PreDownloadItem);
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                return accepted;
            }
        }

        int AddBatch(List<DownloadItem> items)
        {
            ShowMain(); using (BatchDialog dialog = new BatchDialog(settings, items))
            {
                WindowPlacement.Attach(dialog, () => settings, Save);
                if (dialog.ShowDialog(this) == DialogResult.OK) return Insert(dialog.Result);
            }
            return 0;
        }

        int Insert(List<DownloadItem> items)
        {
            int count = 0, previousCount = downloads.Count;
            foreach (DownloadItem item in items)
            {
                if (items.Count > 1 && !item.ConflictAction.HasValue) item.ConflictAction = settings.ExistingBatchFileAction;
                DownloadItem duplicate = settings.CheckDuplicateDownloads ? downloads.FirstOrDefault(x => SameDownload(x, item) && x.State != DownloadState.History && x.State != DownloadState.Error) : null;
                if (duplicate != null)
                {
                    int choice = ChooseDuplicate(duplicate);
                    if (choice == 0) continue;
                    if (choice == 2) { if (duplicate.State != DownloadState.Complete && !Active(duplicate)) Change(duplicate, DownloadState.Queued); count++; continue; }
                }
                Uri uri; string host = Uri.TryCreate(item.Url, UriKind.Absolute, out uri) ? uri.Host : "";
                string extension = Path.GetExtension(String.IsNullOrEmpty(item.FileName) && uri != null ? uri.LocalPath : item.FileName).TrimStart('.');
                CategorySettings category = settings.Categories.FirstOrDefault(x => x.Name == item.Category);
                if (category == null && String.IsNullOrEmpty(item.Category))
                    category = settings.Categories.OrderBy(x => x.DefaultId == "other" ? 2 : x.DefaultId.Length > 0 ? 1 : 0).FirstOrDefault(x => x.Enabled && (x.Extensions.Count == 0 && x.Hosts.Count > 0 || x.Extensions.Any(e => e == "*" || String.Equals(e.TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase))) && (x.Hosts.Count == 0 || x.Hosts.Any(h =>
                    {
                        string pattern = h.Trim().TrimEnd('.');
                        if (pattern.StartsWith("*.")) pattern = pattern.Substring(2);
                        return String.Equals(host, pattern, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
                    })));
                if (category != null) { item.Category = category.Name; if (!item.DirectoryExplicit && !String.IsNullOrEmpty(category.Directory)) item.Directory = category.Directory; }
                string remembered = settings.RememberedDirectory(item.Url, item.FileName);
                if (!item.DirectoryExplicit && remembered.Length > 0) item.Directory = remembered;
                downloads.Add(item); count++;
            }
            Save(); RefreshRows();
            if (downloads.Count > previousCount) SoundAlerts.Play(settings, DownloadSound.Added);
            return count;
        }

        static bool SameDownload(DownloadItem first, DownloadItem second)
        {
            return first.Url == second.Url && first.Kind == second.Kind && first.MediaFormat == second.MediaFormat &&
                first.MediaOnlyAssets == second.MediaOnlyAssets && first.MediaContainer == second.MediaContainer &&
                first.MediaHeight == second.MediaHeight && first.MediaExactMatch == second.MediaExactMatch &&
                first.MediaAudioFormat == second.MediaAudioFormat && first.MediaAudioBitrate == second.MediaAudioBitrate &&
                first.SaveSubtitles == second.SaveSubtitles && first.MediaSubtitleLanguage == second.MediaSubtitleLanguage &&
                first.SaveThumbnail == second.SaveThumbnail;
        }

        int ChooseDuplicate(DownloadItem existing)
        {
            using (var dialog = new Form { Text = T("已有相同下载", "Duplicate download"), Size = new Size(620, 220), StartPosition = FormStartPosition.CenterParent, Font = Font, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(14), Text = existing.Url + Environment.NewLine + existing.OutputPath + Environment.NewLine + StateText(existing.State) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                int choice = 0;
                var cancel = new Button { Text = T("取消新下载", "Cancel new download"), AutoSize = true, DialogResult = DialogResult.Cancel }; buttons.Controls.Add(cancel); dialog.CancelButton = cancel;
                var resume = new Button { Text = existing.State == DownloadState.Complete ? T("保留已完成任务", "Keep completed task") : T("继续已有任务", "Resume existing"), AutoSize = true };
                resume.Click += delegate { choice = 2; dialog.Close(); }; buttons.Controls.Add(resume);
                var create = new Button { Text = T("创建新任务", "Create another"), AutoSize = true }; create.Click += delegate { choice = 1; dialog.Close(); }; buttons.Controls.Add(create);
                dialog.Controls.Add(label); dialog.Controls.Add(buttons); Appearance.Apply(dialog); dialog.ShowDialog(this); return choice;
            }
        }

        void ChangeSelected(DownloadState state) { foreach (DownloadItem item in Selected()) Change(item, state); Save(); RefreshRows(); }
        void ChangeAll(DownloadState state) { foreach (DownloadItem item in downloads.Where(x => x.State != DownloadState.History && x.State != DownloadState.Complete)) Change(item, state); Save(); RefreshRows(); }
        void Change(DownloadItem item, DownloadState state)
        {
            if (state == DownloadState.Queued && Active(item)) return;
            if (state == DownloadState.Stopped) engine.Stop(item);
            else if (Active(item)) engine.Pause(item);
            item.State = state; item.BytesPerSecond = 0; item.Error = ""; item.ForceStart = false;
        }
        void ForceStart() { foreach (DownloadItem item in Selected()) { Change(item, DownloadState.Queued); item.ForceStart = true; } Save(); }

        async void Edit()
        {
            try { await EditSelectedAsync(); }
            catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        async Task EditSelectedAsync()
        {
            DownloadItem item = Selected().FirstOrDefault(); if (item == null) return;
            using (AddDialog dialog = new AddDialog(settings, new[] { item }))
            {
                WindowPlacement.Attach(dialog, () => settings, Save);
                dialog.Text = T("任务属性 / 更新链接", "Task properties / refresh URL");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                DownloadItem replacement = dialog.Result[0]; replacement.Id = item.Id; replacement.EngineId = "";
                replacement.BytesReceived = item.BytesReceived; replacement.TotalBytes = item.TotalBytes;
                bool changedTarget = !String.Equals(item.Directory, replacement.Directory, StringComparison.OrdinalIgnoreCase) || item.FileName != replacement.FileName;
                bool newDownload = changedTarget;
                if (item.BytesReceived > 0 && item.Url != replacement.Url && !changedTarget)
                {
                    DialogResult choice = MessageBox.Show(this, T("新链接是否指向同一个文件？\n是：保留同一任务，清理旧临时片段并从头下载。已保存的输出文件会保留。\n否：创建新任务，旧内容保留在历史记录中。", "Does the new URL identify the same file?\nYes: keep the task, clear old temporary fragments and start from the beginning. Saved output files remain.\nNo: create a new task and keep the old download in history."), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (choice == DialogResult.Cancel) return;
                    newDownload = choice == DialogResult.No;
                }
                refreshingItem = item; Enabled = false;
                try
                {
                    await engine.StopAsync(item);
                    if (IsDisposed) return;
                    replacement.BytesReceived = item.BytesReceived; replacement.TotalBytes = item.TotalBytes;
                    if (newDownload && item.BytesReceived > 0)
                    {
                        engine.Remove(item); item.State = DownloadState.History;
                        replacement.Id = Guid.NewGuid().ToString("N"); replacement.BytesReceived = replacement.TotalBytes = 0; replacement.CompletedAt = null;
                        replacement.WorkingDirectory = ""; replacement.OwnedFiles.Clear();
                        replacement.MediaFiles.Clear();
                        replacement.DirectUrl = "";
                        downloads.Add(replacement); Save(); RefreshRows(); return;
                    }
                    if (item.Url != replacement.Url)
                    {
                        await engine.ResetPartialAsync(item);
                        if (IsDisposed) return;
                        replacement.WorkingDirectory = ""; replacement.CompletedAt = null;
                        replacement.DirectUrl = "";
                        replacement.MediaFiles.Clear();
                        replacement.BytesReceived = replacement.TotalBytes = replacement.BytesPerSecond = 0;
                        replacement.MediaAudioBytes = replacement.MediaAudioTotal = replacement.MediaVideoBytes = replacement.MediaVideoTotal = 0;
                        replacement.OwnedFiles = new List<string>(item.OwnedFiles);
                    }
                    engine.Remove(item); downloads[downloads.IndexOf(item)] = replacement; Save(); RefreshRows();
                }
                finally { refreshingItem = null; if (!IsDisposed) Enabled = true; }
            }
        }

        async void RefreshAddress()
        {
            if (refreshAddressDialog != null) { refreshAddressDialog.Activate(); return; }
            List<DownloadItem> selected = Selected();
            if (selected.Count != 1 || selected[0].State == DownloadState.Complete || selected[0].State == DownloadState.Seeding || selected[0].State == DownloadState.History || selected[0].Kind == "torrent")
            {
                MessageBox.Show(this, T("请选择一个尚未完成的普通或媒体下载。", "Select one unfinished file or media download."), Text);
                return;
            }
            DownloadItem original = selected[0];
            using (var dialog = new RefreshAddressDialog(original, settings))
            {
                refreshAddressDialog = dialog; refreshingItem = original;
                try
                {
                    Change(original, DownloadState.Paused); Save(); RefreshRows();
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        Enabled = false;
                        await engine.ResetPartialAsync(original);
                        if (IsDisposed) { dialog.Finish(0); return; }
                        DownloadItem candidate = dialog.Candidate, replacement = original.Copy();
                        replacement.Url = candidate.Url; replacement.SourcePage = dialog.ArmedPage;
                        replacement.Referer = candidate.Referer; replacement.UserAgent = candidate.UserAgent;
                        replacement.Method = candidate.Method; replacement.Body = candidate.Body;
                        replacement.Headers = new Dictionary<string, string>(candidate.Headers, StringComparer.OrdinalIgnoreCase);
                        if (candidate.MediaFormat.Length > 0) replacement.MediaFormat = candidate.MediaFormat;
                        replacement.EngineId = ""; replacement.Error = ""; replacement.State = DownloadState.Queued;
                        downloads[downloads.IndexOf(original)] = replacement;
                        Save(); RefreshRows(); dialog.Finish(1);
                    }
                    else dialog.Finish(0);
                }
                catch (Exception error) { dialog.Fail(error); if (!IsDisposed) MessageBox.Show(this, Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { refreshAddressDialog = null; refreshingItem = null; if (!IsDisposed) Enabled = true; }
            }
        }

        void Delete(bool files)
        {
            List<DownloadItem> items = Selected(); if (items.Count == 0) return;
            if (!ConfirmRemoval(files)) return;
            RemoveItems(items, files, true);
        }

        bool ConfirmRemoval(bool files)
        {
            if (!(files ? settings.ConfirmFileDeletion : settings.ConfirmTaskRemoval)) return true;
            using (var dialog = new Form { Text = T("确认移除", "Confirm removal"), ClientSize = new Size(480, 180), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = files ? T("永久删除所选任务已经下载的文件？", "Permanently delete the selected downloaded files?") : T("移除所选任务？已下载的文件会保留。", "Remove the selected tasks? Downloaded files will be kept.") }, 0, 0);
                var hide = new CheckBox { Text = T("以后不再询问（可在设置的“提示”页恢复）", "Do not ask again (restore in Settings > Messages)"), AutoSize = true };
                layout.Controls.Add(hide, 0, 1);
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var cancel = new Button { Text = T("取消", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                var confirm = new Button { Text = files ? T("删除文件", "Delete files") : T("移除任务", "Remove tasks"), DialogResult = DialogResult.OK, AutoSize = true };
                buttons.Controls.Add(cancel); buttons.Controls.Add(confirm); layout.Controls.Add(buttons, 0, 2); dialog.Controls.Add(layout);
                dialog.CancelButton = cancel; dialog.AcceptButton = cancel; Appearance.Apply(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                if (hide.Checked) { if (files) settings.ConfirmFileDeletion = false; else settings.ConfirmTaskRemoval = false; Save(); }
                return true;
            }
        }

        void RemoveItems(List<DownloadItem> items, bool files, bool keepHistory)
        {
            foreach (DownloadItem item in items)
            {
                bool wasHistory = item.State == DownloadState.History;
                engine.Remove(item, files);
                if (!files && keepHistory && settings.KeepHistory && !wasHistory) { item.State = DownloadState.History; item.BytesPerSecond = 0; item.HistoryAt = DateTime.Now; }
                else downloads.Remove(item);
            }
            Save(); BuildTree(); RefreshRows();
        }

        static DateTime HistoryDate(DownloadItem item) { return item.HistoryAt ?? item.CompletedAt ?? item.AddedAt ?? DateTime.MinValue; }
        CategorySettings CurrentCategory() { return filter.StartsWith("category:") ? settings.Categories.FirstOrDefault(x => x.Name == filter.Substring(9)) : null; }
        void CategoryFolder(bool change)
        {
            CategorySettings category = CurrentCategory(); if (category == null) return;
            string folder = String.IsNullOrEmpty(category.Directory) ? settings.DownloadDirectory : category.Directory;
            if (change)
            {
                using (var dialog = new FolderBrowserDialog { SelectedPath = folder })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { category.Directory = dialog.SelectedPath; Save(); }
            }
            else { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        }

        void OpenSelected() { DownloadItem item = Selected().FirstOrDefault(); if (item == null) return; if (item.State == DownloadState.Complete && File.Exists(item.OutputPath)) Process.Start(item.OutputPath); else Edit(); }
        void OpenFolder() { DownloadItem item = Selected().FirstOrDefault(); if (item != null) Process.Start("explorer.exe", "/select," + Quote(item.OutputPath)); }
        void CheckHashes()
        {
            DownloadItem item = Selected().FirstOrDefault(); if (item == null) return;
            using (HashDialog dialog = new HashDialog(item.OutputPath, settings.Language)) dialog.ShowDialog(this);
        }

        void ShowProgress(IEnumerable<DownloadItem> items)
        {
            List<DownloadItem> selected = items.ToList(); if (selected.Count == 0) return;
            if (progressWindow == null || progressWindow.IsDisposed)
            {
                progressWindow = new ProgressWindow(() => settings, Save, (item, state) => { Change(item, state); Save(); RefreshRows(); });
                WindowPlacement.Attach(progressWindow, () => settings, Save);
            }
            foreach (DownloadItem item in selected) progressWindow.Track(item);
            if (!progressWindow.Visible) progressWindow.Show(this);
        }

        void UpdateLinkBasket()
        {
            if (!settings.ShowLinkBasket) { if (linkBasket != null && !linkBasket.IsDisposed) linkBasket.Close(); return; }
            if (linkBasket == null || linkBasket.IsDisposed)
                linkBasket = new LinkBasket(() => settings, Save, () => downloads, () => Run(() => Add(new List<DownloadItem>())), () => Run(() => ChangeAll(DownloadState.Queued)), () => Run(() => ChangeAll(DownloadState.Stopped)), () => Run(Settings), text => Run(() => AddText(text)));
            linkBasket.TopMost = settings.LinkBasketOnTop;
            if (!linkBasket.Visible) linkBasket.Show();
        }

        void ShowCompleted(DownloadItem item)
        {
            var dialog = new Form { Text = T("下载完成", "Download complete"), Size = new Size(580, 270), StartPosition = FormStartPosition.CenterParent, Font = Font, Padding = new Padding(14), MaximizeBox = false, MinimizeBox = false };
            var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Text = item.FileName + Environment.NewLine + item.OutputPath + Environment.NewLine + SizeText(item.TotalBytes) + Environment.NewLine + item.CompletedAt };
            var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
            var hide = new CheckBox { Text = T("以后不显示完成窗口", "Do not show completion windows"), AutoSize = true };
            hide.CheckedChanged += delegate { settings.ShowCompleteDialog = !hide.Checked; Save(); }; options.Controls.Add(hide);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = T("关闭", "Close"), AutoSize = true }; close.Click += delegate { dialog.Close(); }; buttons.Controls.Add(close);
            var folder = new Button { Text = T("打开文件夹", "Open folder"), AutoSize = true }; folder.Click += delegate { Run(() => Process.Start("explorer.exe", "/select," + Quote(item.OutputPath))); }; buttons.Controls.Add(folder);
            var open = new Button { Text = T("打开文件", "Open file"), AutoSize = true, Enabled = File.Exists(item.OutputPath) }; open.Click += delegate { Run(() => Process.Start(item.OutputPath)); }; buttons.Controls.Add(open);
            dialog.Controls.Add(text); dialog.Controls.Add(options); dialog.Controls.Add(buttons); Appearance.Apply(dialog); dialog.Show(this);
        }
        void RenameFile()
        {
            DownloadItem item = Selected().FirstOrDefault(); if (item == null) return;
            if (Active(item) || !item.CompletedAt.HasValue || !File.Exists(item.OutputPath)) throw new InvalidOperationException(T("请先完成下载并停止做种。", "Finish the download and stop seeding first."));
            using (Form dialog = new Form { Text = T("重命名文件", "Rename file"), Size = new Size(520, 160), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Padding = new Padding(12) })
            {
                TextBox name = new TextBox { Text = item.FileName, Dock = DockStyle.Top };
                Button ok = new Button { Text = T("确定", "OK"), Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                dialog.Controls.Add(name); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
                Appearance.Apply(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK || name.Text == item.FileName) return;
                if (String.IsNullOrWhiteSpace(name.Text) || name.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException(T("请输入有效文件名。", "Enter a valid filename."));
                string oldPath = item.OutputPath, newPath = Path.Combine(item.Directory, name.Text);
                File.Move(oldPath, newPath); item.FileName = name.Text;
                for (int i = 0; i < item.OwnedFiles.Count; i++) if (String.Equals(item.OwnedFiles[i], oldPath, StringComparison.OrdinalIgnoreCase)) item.OwnedFiles[i] = newPath;
                foreach (MediaFileInformation info in item.MediaFiles)
                    if (String.Equals(info.Path, oldPath, StringComparison.OrdinalIgnoreCase)) { info.Path = newPath; info.FileName = item.FileName; }
                Save(); RefreshRows();
            }
        }
        void DownloadAgain()
        {
            foreach (DownloadItem original in Selected())
            {
                DownloadItem item = original.Copy(); item.Id = Guid.NewGuid().ToString("N");
                item.WorkingDirectory = item.EngineId = ""; item.OwnedFiles.Clear();
                item.MediaFiles.Clear();
                item.DirectUrl = "";
                item.BytesReceived = item.TotalBytes = item.BytesPerSecond = 0; item.CompletedAt = null; item.AddedAt = DateTime.Now;
                item.Error = ""; item.State = DownloadState.Queued; item.Enabled = true; item.ForceStart = false;
                downloads.Add(item);
            }
            Save(); RefreshRows();
        }
        void ToggleEnabled()
        {
            List<DownloadItem> items = Selected(); bool enabled = items.Any(x => !x.Enabled);
            foreach (DownloadItem item in items)
            {
                item.Enabled = enabled; item.ForceStart = false;
                if (!enabled && Active(item)) { engine.Pause(item); item.State = DownloadState.Queued; }
            }
            Save(); RefreshRows();
        }
        public static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        void OpenTorrent() { using (OpenFileDialog dialog = new OpenFileDialog { Filter = "BitTorrent|*.torrent", Multiselect = true }) if (dialog.ShowDialog(this) == DialogResult.OK) AddText(String.Join(Environment.NewLine, dialog.FileNames)); }
        void Import() { using (OpenFileDialog dialog = new OpenFileDialog { Filter = T("文本|*.txt;*.csv|所有文件|*.*", "Text|*.txt;*.csv|All files|*.*") }) if (dialog.ShowDialog(this) == DialogResult.OK) AddText(File.ReadAllText(dialog.FileName)); }
        void Export() { List<DownloadItem> items = Selected(); if (items.Count == 0) return; using (SaveFileDialog dialog = new SaveFileDialog { Filter = T("文本|*.txt", "Text|*.txt"), FileName = "downloads.txt" }) if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllLines(dialog.FileName, items.Select(x => x.Url), new UTF8Encoding(false)); }

        void ExportState()
        {
            using (var scope = new StateTransferDialog(settings.Language, false, true, true, true))
            {
                if (scope.ShowDialog(this) != DialogResult.OK) return;
                using (var file = new SaveFileDialog { Filter = T("Qing 备份|*.qingbackup", "Qing backup|*.qingbackup"), FileName = "Qing-settings-downloads.qingbackup" })
                    if (file.ShowDialog(this) == DialogResult.OK) storage.Export(file.FileName, settings, downloads, scope.IncludeSettings, scope.IncludeCurrent, scope.IncludeHistory);
            }
        }

        void ImportState()
        {
            using (var file = new OpenFileDialog { Filter = T("Qing 备份|*.qingbackup", "Qing backup|*.qingbackup") })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                StateArchive archive = storage.ReadArchive(file.FileName);
                using (var scope = new StateTransferDialog(settings.Language, true, archive.IncludesSettings, archive.IncludesCurrent, archive.IncludesHistory))
                {
                    if (scope.ShowDialog(this) != DialogResult.OK) return;
                    ReplaceState(archive.State, scope.IncludeSettings, scope.IncludeCurrent, scope.IncludeHistory);
                }
            }
        }

        void ReplaceState(SavedState state, bool includeSettings, bool includeCurrent, bool includeHistory)
        {
            foreach (DownloadItem item in downloads.Where(x => x.State == DownloadState.History ? includeHistory : includeCurrent).ToList())
            {
                engine.Remove(item); downloads.Remove(item);
            }
            foreach (DownloadItem item in state.Downloads.Where(x => x.State == DownloadState.History ? includeHistory : includeCurrent))
            {
                item.Id = Guid.NewGuid().ToString("N");
                if (!(includeSettings ? state.Settings : settings).Queues.Any(q => q.Id == item.QueueId)) item.QueueId = "";
                downloads.Add(item);
            }
            if (includeCurrent) { activeQueues.Clear(); completionPending = false; }
            if (includeSettings) ApplySettings(state.Settings);
            else { Save(); BuildTree(); RefreshRows(); }
        }

        QueueSettings CurrentQueue() { return filter.StartsWith("queue:") ? settings.Queues.FirstOrDefault(x => x.Id == filter.Substring(6)) : null; }
        void MoveInQueue(int direction)
        {
            List<DownloadItem> selected = Selected().Where(x => x.State != DownloadState.History).ToList();
            foreach (string queueId in selected.Select(x => x.QueueId).Distinct())
            {
                List<DownloadItem> items = downloads.Where(x => x.QueueId == queueId && x.State != DownloadState.History).OrderByDescending(x => x.Priority).ToList();
                int[] priorities = items.Select(x => x.Priority).ToArray();
                for (int index = direction < 0 ? 1 : items.Count - 2; index >= 0 && index < items.Count; index -= direction)
                {
                    int next = index + direction;
                    if (next < 0 || next >= items.Count || !selected.Contains(items[index]) || selected.Contains(items[next])) continue;
                    DownloadItem item = items[index]; items[index] = items[next]; items[next] = item;
                }
                int position = 0;
                for (int index = 0; index < downloads.Count; index++)
                {
                    if (downloads[index].QueueId != queueId || downloads[index].State == DownloadState.History) continue;
                    items[position].Priority = priorities[position];
                    downloads[index] = items[position++];
                }
            }
            list.ListViewItemSorter = null; list.Sorting = SortOrder.None;
            Save(); RefreshRows();
        }
        void EditQueue(QueueSettings queue)
        {
            using (QueueDialog dialog = new QueueDialog(queue ?? new QueueSettings(), settings.Language))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (queue == null) settings.Queues.Add(dialog.Result); else settings.Queues[settings.Queues.IndexOf(queue)] = dialog.Result;
                Save(); BuildTree();
            }
        }
        void SetQueue(bool enabled)
        {
            QueueSettings queue = CurrentQueue(); if (queue == null) return; queue.Enabled = enabled;
            foreach (DownloadItem item in downloads.Where(x => x.QueueId == queue.Id))
            {
                if (!enabled) { item.ForceStart = false; if (Active(item)) { engine.Pause(item); item.State = DownloadState.Queued; } }
            }
            Save();
        }
        void MoveQueue()
        {
            List<DownloadItem> items = Selected(); if (items.Count == 0) return;
            using (Form dialog = new Form { Text = T("移动到队列", "Move to queue"), Size = new Size(380, 160), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                ComboBox choices = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                choices.Items.Add(new QueueSettings { Id = "", Name = T("默认队列", "Default queue") });
                foreach (QueueSettings queue in settings.Queues) choices.Items.Add(queue); choices.SelectedIndex = 0;
                Button ok = new Button { Text = T("确定", "OK"), Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                dialog.Padding = new Padding(16); dialog.Controls.Add(choices); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
                Appearance.Apply(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (DownloadItem item in items) item.QueueId = ((QueueSettings)choices.SelectedItem).Id;
                Save(); RefreshRows();
            }
        }

        void EditSchedule()
        {
            List<DownloadItem> items = Selected(); if (items.Count == 0) return;
            using (var dialog = new ScheduleDialog(items[0].Schedule ?? new ScheduleSettings { RetryCount = settings.RetryCount, RetryDelaySeconds = settings.RetryDelaySeconds }, settings.Language))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (DownloadItem item in items)
                {
                    if (Active(item)) engine.Pause(item);
                    item.Schedule = dialog.Result.Copy(); item.ForceStart = false;
                    if (item.State != DownloadState.Complete && item.State != DownloadState.History) item.State = DownloadState.Queued;
                }
                Save(); RefreshRows();
            }
        }

        void Settings()
        {
            using (SettingsDialog dialog = new SettingsDialog(settings))
            {
                dialog.ApplyRequested += ApplySettings;
                dialog.ShowDialog(this);
            }
        }

        void ApplySettings(AppSettings value)
        {
            settings = value;
            Appearance.Configure(settings); Appearance.Apply(this);
            SetTaskView(settings.TaskView, false);
            if (progressWindow != null && !progressWindow.IsDisposed) Appearance.Apply(progressWindow);
            if (videoBox != null && !videoBox.IsDisposed) videoBox.ApplySettings();
            if (linkBasket != null && !linkBasket.IsDisposed) Appearance.Apply(linkBasket);
            TopMost = settings.AlwaysOnTop;
            for (int index = 0; index < list.Columns.Count && index < settings.ColumnWidths.Count; index++) list.Columns[index].Width = settings.ColumnWidths[index];
            List<DownloadItem> orphaned = downloads.Where(x => x.QueueId != "" && !settings.Queues.Any(q => q.Id == x.QueueId)).ToList();
            foreach (DownloadItem item in orphaned)
            {
                item.QueueId = ""; item.ForceStart = false;
                if (Active(item)) engine.Pause(item);
                if (item.State == DownloadState.Queued) item.State = DownloadState.Paused;
            }
            engine.ApplySettings(settings);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (settings.StartWithWindows) key.SetValue("QingFDM", Quote(Application.ExecutablePath) + " --background");
                else if (String.Equals(Convert.ToString(key.GetValue("QingFDM")), Quote(Application.ExecutablePath) + " --background", StringComparison.OrdinalIgnoreCase)) key.DeleteValue("QingFDM", false);
            }
            Save(); BuildTree(); RefreshRows();
            UpdateLinkBasket();
            if (orphaned.Count > 0) MessageBox.Show(this, T("已删除队列中的任务已移至默认队列；等待和运行中的任务已暂停。", "Tasks from deleted queues were moved to the default queue; queued and active tasks were paused."), Text);
        }

        void BrowserIntegration() { using (IntegrationDialog dialog = new IntegrationDialog(settings)) dialog.ShowDialog(this); }
        void RegisterFileTypes()
        {
            FileAssociations.Register();
            MessageBox.Show(this, T("已登记 Qing 的种子与磁链打开方式。请在 Windows 默认应用中选择 Qing；现有默认程序未更改。", "Qing is registered for torrent files and magnet links. Choose Qing in Windows Default Apps; existing defaults are unchanged."), Text);
            Process.Start("control.exe", "/name Microsoft.DefaultPrograms");
        }
        async void CheckForUpdates(bool automatic)
        {
            if (checkingUpdates) return;
            checkingUpdates = true;
            try
            {
                using (var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan })
                {
                    ReleaseInfo release = null;
                    if (automatic)
                    {
                        settings.LastUpdateCheckUtc = DateTime.UtcNow; Save();
                        release = await Updates.LatestAsync(client, System.Threading.CancellationToken.None);
                        if (IsDisposed || !settings.CheckUpdates || release == null || release.Version <= new Version(Application.ProductVersion) || release.Tag == settings.SkippedUpdateVersion) return;
                    }
                    using (var dialog = new UpdateDialog(settings, Save, client, release))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.InstallerPath == null) return;
                        foreach (DownloadItem item in downloads.Where(Active).ToList()) engine.Pause(item);
                        Save();
                        Process.Start(new ProcessStartInfo(dialog.InstallerPath, "/DIR=" + Quote(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))) { UseShellExecute = true });
                        Exit();
                    }
                }
            }
            catch (Exception error) { if (!automatic && !IsDisposed) MessageBox.Show(this, Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { checkingUpdates = false; }
        }

        void CheckTorrentAssociation()
        {
            if (IsDisposed || FileAssociations.IsTorrentDefault()) return;
            using (var dialog = new Form { Text = T("种子文件关联", "Torrent file association"), Size = new Size(560, 240), MinimumSize = new Size(440, 220), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            {
                var message = new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Text = T("Windows 当前未使用此 Qing 安装打开 .torrent 文件。可以登记 Qing 并在 Windows 默认程序设置中选择它。", "Windows does not currently open .torrent files with this Qing installation. Register Qing and choose it in Windows default programs to change this.") };
                var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
                var later = new Button { Text = T("暂不更改", "Not now"), AutoSize = true, DialogResult = DialogResult.Cancel };
                var open = new Button { Text = T("打开默认程序设置…", "Open default programs…"), AutoSize = true };
                var never = new CheckBox { Text = T("不再检查，可在 BT 设置中恢复", "Stop checking; restore in BitTorrent settings"), AutoSize = true };
                never.CheckedChanged += delegate { settings.CheckTorrentAssociation = !never.Checked; Save(); };
                open.Click += delegate
                {
                    Run(() => { FileAssociations.Register(); Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.DefaultPrograms") { UseShellExecute = true }); dialog.Close(); });
                };
                controls.Controls.AddRange(new Control[] { later, open, never }); dialog.Controls.Add(message); dialog.Controls.Add(controls); dialog.CancelButton = later;
                Appearance.Apply(dialog); dialog.ShowDialog(this);
            }
        }

        void Media() { using (MediaDialog dialog = new MediaDialog(settings)) if (dialog.ShowDialog(this) == DialogResult.OK) Insert(dialog.Result); }
        void ConvertMedia() { ConvertMedia(MediaOperation.Convert); }
        void ConvertMedia(MediaOperation operation)
        {
            Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> files = new List<string>();
            foreach (DownloadItem item in Selected().Where(x => File.Exists(x.OutputPath)))
            {
                if (!links.ContainsKey(item.OutputPath)) files.Add(item.OutputPath);
                links[item.OutputPath] = item.Url;
            }
            using (ConversionDialog dialog = new ConversionDialog(settings, files, links, operation)) dialog.ShowDialog(this);
        }
        void DebridAccounts()
        {
            using (DebridDialog dialog = new DebridDialog(settings))
                if (dialog.ShowDialog(this) == DialogResult.OK) { settings.DebridAccounts = dialog.Accounts; Insert(dialog.Items); }
        }

        bool QueueOpen(QueueSettings queue, DateTime now)
        {
            return queue.Enabled && ScheduleOpen(queue, now);
        }

        static DateTime ScheduleDay(ScheduleSettings schedule, DateTime now)
        {
            TimeSpan start = schedule.StartAt.HasValue ? schedule.StartAt.Value.TimeOfDay : TimeSpan.Zero;
            return schedule.StopAt.HasValue && schedule.StopAt.Value.TimeOfDay < start && now.TimeOfDay < schedule.StopAt.Value.TimeOfDay ? now.Date.AddDays(-1) : now.Date;
        }

        static bool ScheduleOpen(ScheduleSettings schedule, DateTime now)
        {
            if (schedule == null) return true;
            if (schedule.ScheduleMode == ScheduleMode.Daily)
            {
                TimeSpan start = schedule.StartAt.HasValue ? schedule.StartAt.Value.TimeOfDay : TimeSpan.Zero;
                TimeSpan? stop = schedule.StopAt.HasValue ? (TimeSpan?)schedule.StopAt.Value.TimeOfDay : null;
                DateTime day = ScheduleDay(schedule, now);
                if (!schedule.Days.Contains(day.DayOfWeek)) return false;
                return now >= day + start && (!stop.HasValue || now < day.AddDays(stop.Value < start ? 1 : 0) + stop.Value);
            }
            return (schedule.ScheduleMode == ScheduleMode.Manual || !schedule.StartAt.HasValue || now >= schedule.StartAt.Value) && (!schedule.StopAt.HasValue || now < schedule.StopAt.Value);
        }

        static bool RepeatDue(ScheduleSettings schedule, DateTime completed, DateTime now)
        {
            if (schedule.ScheduleMode == ScheduleMode.Interval) return (now - completed).TotalMinutes >= schedule.RepeatIntervalMinutes;
            return schedule.ScheduleMode == ScheduleMode.Daily && completed < ScheduleDay(schedule, now) + (schedule.StartAt.HasValue ? schedule.StartAt.Value.TimeOfDay : TimeSpan.Zero);
        }

        static void Repeat(DownloadItem item)
        {
            item.State = DownloadState.Queued; item.EngineId = ""; item.BytesReceived = 0; item.CompletedAt = null; item.WorkingDirectory = "";
        }

        static void ConsumeStart(ScheduleSettings schedule)
        {
            if (schedule != null && schedule.ScheduleMode == ScheduleMode.Once) schedule.StartAt = null;
        }

        void Tick(object sender, EventArgs args)
        {
            if (ticking) return;
            ticking = true;
            try { TickDownloads(); }
            finally { ticking = false; }
        }

        void TickDownloads()
        {
            ticks++;
            if (Updates.Due(settings, DateTime.UtcNow)) CheckForUpdates(true);
            DateTime now = DateTime.Now;
            foreach (QueueSettings queue in settings.Queues)
            {
                bool open = QueueOpen(queue, now);
                if (!open) foreach (DownloadItem item in downloads.Where(x => x.QueueId == queue.Id && Active(x) && !x.ForceStart).ToList()) { engine.Pause(item); item.State = DownloadState.Queued; }
                if (open && (queue.ScheduleMode == ScheduleMode.Interval || queue.ScheduleMode == ScheduleMode.Daily))
                {
                    List<DownloadItem> cycle = downloads.Where(x => x.QueueId == queue.Id && x.Enabled && x.State != DownloadState.History).ToList();
                    if (cycle.Count > 0 && cycle.All(x => x.State == DownloadState.Complete && x.CompletedAt.HasValue) && RepeatDue(queue, cycle.Max(x => x.CompletedAt.Value), now))
                    {
                        foreach (DownloadItem item in cycle) Repeat(item);
                        dirty = true;
                    }
                }
            }
            foreach (DownloadItem item in downloads.Where(x => x.Schedule != null && x.Enabled && !x.ForceStart).ToList())
            {
                bool open = ScheduleOpen(item.Schedule, now);
                if (!open && Active(item)) { engine.Pause(item); item.State = DownloadState.Queued; dirty = true; }
                if (open && item.State == DownloadState.Complete && item.CompletedAt.HasValue && RepeatDue(item.Schedule, item.CompletedAt.Value, now)) { Repeat(item); dirty = true; }
            }
            foreach (DownloadItem item in downloads.Where(x => x.State == DownloadState.Queued && x.Enabled && x != refreshingItem).OrderByDescending(x => x.Priority).ToList())
            {
                QueueSettings queue = settings.Queues.FirstOrDefault(x => x.Id == item.QueueId);
                if (!item.ForceStart && (downloads.Count(Active) >= settings.MaxConcurrent || !ScheduleOpen(item.Schedule, now) || queue != null && (!QueueOpen(queue, now) || downloads.Count(x => x.QueueId == queue.Id && Active(x)) >= queue.MaxConcurrent))) continue;
                try
                {
                    engine.Start(item);
                    if (settings.NotifyStarted) tray.ShowBalloonTip(3000, T("下载开始", "Download started"), item.FileName.Length > 0 ? item.FileName : T("下载任务已开始运行。", "The download is now running."), ToolTipIcon.Info);
                    if (settings.ShowProgressWindow || progressWindow != null && !progressWindow.IsDisposed) ShowProgress(new[] { item });
                    if (ScheduleOpen(item.Schedule, now)) ConsumeStart(item.Schedule);
                    if (queue != null && QueueOpen(queue, now)) ConsumeStart(queue);
                    activeQueues.Add(item.QueueId); completionPending = allSoundPending = true; dirty = true;
                }
                catch (Exception error) { item.State = DownloadState.Error; item.Error = error.Message; dirty = true; EngineChanged(item); }
            }
            if (allSoundPending && downloads.Any(x => x.State == DownloadState.Complete || x.State == DownloadState.Seeding) && downloads.All(x => x.State == DownloadState.Complete || x.State == DownloadState.Seeding || x.State == DownloadState.History))
            {
                allSoundPending = false;
                SoundAlerts.Play(settings, DownloadSound.AllFinished);
            }
            foreach (string queueId in activeQueues.ToList())
            {
                List<DownloadItem> items = downloads.Where(x => x.QueueId == queueId && x.State != DownloadState.History).ToList();
                if (items.Count == 0 || items.Any(x => x.State != DownloadState.Complete)) continue;
                activeQueues.Remove(queueId);
                QueueSettings queue = settings.Queues.FirstOrDefault(x => x.Id == queueId);
                if (queue != null) Completion(queue.CompletionAction, queue.CompletionCommand, queue.CompletionArguments);
            }
            if (completionPending && downloads.Any(x => x.State == DownloadState.Complete) && downloads.All(x => x.State == DownloadState.Complete || x.State == DownloadState.History))
            {
                completionPending = false;
                Completion(settings.CompletionAction, settings.CompletionCommand, settings.CompletionArguments);
            }
            if (IsDisposed) return;
            if (completedRemovals.Count > 0)
            {
                List<DownloadItem> remove = downloads.Where(x => x.State == DownloadState.Complete && completedRemovals.Contains(x.Id)).ToList();
                completedRemovals.Clear();
                if (settings.RemoveCompletedTasks && remove.Count > 0) RemoveItems(remove, false, true);
            }
            SetThreadExecutionState(settings.PreventSleep && downloads.Any(Active) ? 0x80000001u : 0x80000000u);
            if (settings.ClipboardEnabled && ticks % 2 == 0)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string value = Clipboard.GetText();
                        if (value != clipboard) { clipboard = value; List<DownloadItem> items = ClipboardItems(value); if (items.Count > 0) Add(items); }
                    }
                }
                catch (ExternalException) { }
            }
            if (dirty && ticks % 5 == 0) Run(Save);
            RefreshRows();
        }

        void EngineChanged(DownloadItem item)
        {
            if (IsDisposed || !IsHandleCreated) return;
            DateTime? completed = item.State == DownloadState.Complete || item.State == DownloadState.Seeding ? item.CompletedAt : null;
            DownloadState state = item.State;
            string filename = item.FileName, output = item.OutputPath;
            BeginInvoke(new Action(delegate
            {
                dirty = true;
                if (state != DownloadState.Error) notifiedErrors.Remove(item.Id);
                else if (downloads.Contains(item) && notifiedErrors.Add(item.Id)) SoundAlerts.Play(settings, DownloadSound.Error);
                if (settings.RemoveCompletedTasks && item.State == DownloadState.Complete) completedRemovals.Add(item.Id);
                DateTime previous;
                if (completed.HasValue && (!notifiedCompletions.TryGetValue(item.Id, out previous) || previous != completed.Value) && downloads.Any(x => x.Id == item.Id))
                {
                    notifiedCompletions[item.Id] = completed.Value;
                    SoundAlerts.Play(settings, DownloadSound.Finished);
                    if (settings.Notifications) tray.ShowBalloonTip(3000, T("下载完成", "Download complete"), filename, ToolTipIcon.Info);
                    if (settings.ShowCompleteDialog) ShowCompleted(item.Copy());
                    if (!String.IsNullOrEmpty(settings.ScanCommand) && (settings.ScanExtensions.Count == 0 || settings.ScanExtensions.Contains(Path.GetExtension(filename).TrimStart('.'), StringComparer.OrdinalIgnoreCase)))
                        Run(() => Process.Start(new ProcessStartInfo(settings.ScanCommand, settings.ScanArguments.Replace("{file}", Quote(output))) { UseShellExecute = false, CreateNoWindow = true }));
                }
            }));
        }

        Task<ExistingFileAction> ChooseConflict(DownloadItem item)
        {
            TaskCompletionSource<ExistingFileAction> result = new TaskCompletionSource<ExistingFileAction>();
            BeginInvoke(new Action(delegate
            {
                using (Form dialog = new Form { Text = T("文件已存在", "File already exists"), Size = new Size(560, 210), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
                {
                    Label label = new Label { Dock = DockStyle.Fill, Padding = new Padding(16), Text = item.OutputPath + Environment.NewLine + Environment.NewLine + T("选择同名文件的处理方式。", "Choose how to handle the existing file.") };
                    FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                    Button cancel = new Button { Text = T("取消任务", "Cancel task"), AutoSize = true, DialogResult = DialogResult.Cancel };
                    buttons.Controls.Add(cancel); dialog.CancelButton = cancel;
                    ExistingFileAction choice = ExistingFileAction.Skip;
                    foreach (ExistingFileAction action in new[] { ExistingFileAction.Skip, ExistingFileAction.Rename, ExistingFileAction.Overwrite })
                    {
                        ExistingFileAction selected = action;
                        string title = action == ExistingFileAction.Skip ? T("跳过", "Skip") : action == ExistingFileAction.Rename ? T("自动改名", "Rename") : T("覆盖", "Replace");
                        Button button = new Button { Text = title, AutoSize = true };
                        button.Click += delegate { choice = selected; dialog.DialogResult = DialogResult.OK; dialog.Close(); };
                        buttons.Controls.Add(button);
                    }
                    dialog.Controls.Add(label); dialog.Controls.Add(buttons);
                    Appearance.Apply(dialog);
                    if (dialog.ShowDialog(this) == DialogResult.OK) result.SetResult(choice); else result.SetCanceled();
                }
            }));
            return result.Task;
        }

        void Completion(CompletionAction action, string command, string arguments)
        {
            if (!String.IsNullOrWhiteSpace(command)) Run(() => Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = false, CreateNoWindow = true }));
            if (action == CompletionAction.None) return;
            if (!ConfirmCompletion(action)) return;
            Save();
            if (action == CompletionAction.Exit) Exit();
            else if (action == CompletionAction.Shutdown) Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
            else Application.SetSuspendState(action == CompletionAction.Sleep ? PowerState.Suspend : PowerState.Hibernate, false, false);
        }

        bool ConfirmCompletion(CompletionAction action)
        {
            if (action == CompletionAction.Shutdown && !settings.ShowShutdownWarning) return true;
            using (Form dialog = new Form { Text = T("完成后的操作", "Completion action"), Size = new Size(400, 170), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            using (Timer countdown = new Timer { Interval = 1000 })
            {
                int seconds = 30;
                Label label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
                Button cancel = new Button { Dock = DockStyle.Fill, Text = T("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
                var hide = new CheckBox { AutoSize = true, Text = T("以后不显示关机倒计时", "Do not show shutdown countdown again"), Visible = action == CompletionAction.Shutdown };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(label, 0, 0); layout.Controls.Add(hide, 0, 1); layout.Controls.Add(cancel, 0, 2); dialog.Controls.Add(layout); dialog.CancelButton = cancel;
                Action update = () => label.Text = action + " · " + seconds + T(" 秒", " seconds"); update();
                countdown.Tick += delegate { seconds--; update(); if (seconds == 0) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
                Appearance.Apply(dialog); countdown.Start(); if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                if (action == CompletionAction.Shutdown && hide.Checked) settings.ShowShutdownWarning = false;
            }
            return true;
        }

        async Task<object> ReceiveAsync(Dictionary<string, object> request)
        {
            if (NativeBridge.Get(request, "action") == "add" && refreshAddressDialog != null)
            {
                List<DownloadItem> items = ((IEnumerable)request["items"]).Cast<Dictionary<string, object>>().Select(BrowserItem).ToList();
                if (items.Count == 1 && refreshAddressDialog.CanOffer(items[0]))
                {
                    int accepted = await refreshAddressDialog.OfferAsync(items[0]).ConfigureAwait(false);
                    return new { id = NativeBridge.Get(request, "id"), ok = true, accepted };
                }
            }
            if (NativeBridge.Get(request, "action") != "mediaFormats") return await Receive(request);
            DownloadItem item = BrowserItem((Dictionary<string, object>)request["item"]);
            if (item.Method != "GET") throw new ArgumentException("Media format inspection requires a GET request.");
            using (System.Threading.CancellationTokenSource query = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(browserQueries.Token))
            {
                query.CancelAfter(120000);
                MediaInfo media = await MediaTools.InspectAsync(item, settings.Copy(), query.Token).ConfigureAwait(false);
                return new { id = NativeBridge.Get(request, "id"), ok = true, title = media.Title,
                    formats = media.Formats.Select(format => new { id = format.Id, extension = format.Extension, resolution = format.Resolution,
                        audioCodec = format.AudioCodec, videoCodec = format.VideoCodec, size = format.Size, height = format.Height, language = format.Language }).ToArray() };
            }
        }

        async Task<object> Receive(Dictionary<string, object> request)
        {
            string id = NativeBridge.Get(request, "id");
            string action = NativeBridge.Get(request, "action");
            if (action == "hello") return new { id, ok = true, version = Application.ProductVersion, config = new { language = settings.Language, captureEnabled = settings.CaptureEnabled, mediaEnabled = settings.MediaEnabled, suggestBrowserExclusions = settings.SuggestBrowserExclusions, mediaMinimumSizeKb = settings.Media.MinimumSizeKb, extensions = settings.Extensions, excludedHosts = settings.ExcludedHosts, mediaExcludedHosts = settings.MediaExcludedHosts, excludedExtensions = settings.ExcludedExtensions } };
            if (action == "disableExclusionSuggestions") { settings.SuggestBrowserExclusions = false; Save(); return new { id, ok = true }; }
            if (action == "show")
            {
                ShowMain(); string text = NativeBridge.Get(request, "text");
                if (!String.IsNullOrWhiteSpace(text)) BeginInvoke(new Action(() => Run(() => AddText(text))));
                return new { id, ok = true };
            }
            if (action == "mediaBox")
            {
                string command = NativeBridge.Get(request, "command"), sourceKey = NativeBridge.Get(request, "sourceKey");
                if (command == "close")
                {
                    if (videoBox != null && !videoBox.IsDisposed) videoBox.Close();
                    return new { id, ok = true, visible = false };
                }
                if (command != "show" && command != "update" && command != "clear") return new { id, ok = false, error = "Unsupported media box command." };
                if (command != "show" && (videoBox == null || videoBox.IsDisposed || !videoBox.Visible)) return new { id, ok = true, visible = false };
                if (videoBox == null || videoBox.IsDisposed) videoBox = new VideoBox(() => settings, selected => Run(() => Add(selected)), Save);
                if (command == "clear") videoBox.ClearEntries(sourceKey);
                else
                {
                    List<BrowserMediaCandidate> entries = new List<BrowserMediaCandidate>();
                    foreach (object raw in (IEnumerable)request["entries"])
                    {
                        Dictionary<string, object> data = (Dictionary<string, object>)raw;
                        entries.Add(new BrowserMediaCandidate { Item = BrowserItem(data), Title = NativeBridge.Get(data, "title"),
                            ThumbnailUrl = NativeBridge.Get(data, "thumbnailUrl"), MimeType = NativeBridge.Get(data, "mime"), DurationSeconds = JsonObject.Number(data, "duration"),
                            Width = (int)JsonObject.Number(data, "width"), Height = (int)JsonObject.Number(data, "height"), SourceTitle = NativeBridge.Get(data, "sourceTitle") });
                    }
                    videoBox.UpdateEntries(sourceKey, entries);
                }
                if (command == "show") { videoBox.Show(); if (videoBox.WindowState == FormWindowState.Minimized) videoBox.WindowState = FormWindowState.Normal; videoBox.Activate(); }
                return new { id, ok = true, visible = videoBox.Visible };
            }
            if (action != "add") return new { id, ok = false, error = "Unsupported action." };
            List<DownloadItem> items = new List<DownloadItem>();
            foreach (object raw in (IEnumerable)request["items"]) items.Add(BrowserItem((Dictionary<string, object>)raw));
            bool show = !request.ContainsKey("showDialog") || Convert.ToBoolean(request["showDialog"]);
            if (!show) foreach (DownloadItem item in items) item.State = DownloadState.Queued;
            int accepted = show ? await AddAsync(items) : Insert(items);
            return new { id, ok = true, accepted };
        }

        DownloadItem BrowserItem(Dictionary<string, object> data)
        {
            string url = NativeBridge.Get(data, "url");
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !(new[] { "http", "https", "ftp", "ftps", "magnet" }).Contains(uri.Scheme)) throw new ArgumentException("Unsupported URL.");
            DownloadItem item = NewItem(url);
            item.LinkText = NativeBridge.Get(data, "linkText");
            item.SourcePage = NativeBridge.Get(data, "sourcePage");
            item.MediaTitle = NativeBridge.Get(data, "title"); item.ThumbnailUrl = NativeBridge.Get(data, "thumbnailUrl"); item.MediaDurationSeconds = JsonObject.Number(data, "duration");
            item.MediaFormat = NativeBridge.Get(data, "mediaFormat");
            if (JsonObject.Number(data, "height") > 0) item.MediaQuality = JsonObject.String(data, "height") + "p";
            item.FileName = Path.GetFileName(NativeBridge.Get(data, "fileName"));
            item.Referer = NativeBridge.Get(data, "referer"); item.Method = NativeBridge.Get(data, "method");
            if (String.IsNullOrEmpty(item.Method)) item.Method = "GET";
            if (item.Method != "GET" && item.Method != "POST") throw new ArgumentException("Unsupported HTTP method.");
            item.Body = NativeBridge.Get(data, "body");
            string kind = NativeBridge.Get(data, "kind"); item.Kind = kind == "stream" || kind == "media" ? "media" : "file";
            if (uri.Scheme == "magnet" || uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) item.Kind = "torrent";
            object headers;
            if (data.TryGetValue("headers", out headers)) foreach (KeyValuePair<string, object> header in (Dictionary<string, object>)headers) item.Headers[header.Key] = Convert.ToString(header.Value);
            return item;
        }

        void Save()
        {
            List<DownloadItem> history = downloads.Where(x => x.State == DownloadState.History).OrderByDescending(HistoryDate).ToList();
            foreach (DownloadItem item in history)
            {
                DateTime date = HistoryDate(item);
                if (!settings.KeepHistory || settings.HistoryDays > 0 && date < DateTime.Now.AddDays(-settings.HistoryDays) || settings.HistoryLimit > 0 && history.IndexOf(item) >= settings.HistoryLimit) downloads.Remove(item);
            }
            storage.Save(settings, downloads); dirty = false;
        }
        void Exit() { exiting = true; Close(); }
        void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (!exiting && args.CloseReason == CloseReason.UserClosing && settings.CloseToTray) { args.Cancel = true; Hide(); return; }
            timer.Stop(); browserQueries.Cancel(); bridge.Dispose(); engine.Dispose(); SetThreadExecutionState(0x80000000u);
            if (linkBasket != null && !linkBasket.IsDisposed) linkBasket.CloseWithApplication();
            if (videoBox != null && !videoBox.IsDisposed) videoBox.Close();
            try { Save(); } catch (Exception error) { MessageBox.Show(Localization.Error(settings.Language, error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            tray.Visible = false; tray.Dispose();
        }

        sealed class RowComparer : IComparer
        {
            readonly int column; readonly bool ascending;
            public RowComparer(int c, bool a) { column = c; ascending = a; }
            public int Compare(object left, object right)
            {
                ListViewItem a = (ListViewItem)left, b = (ListViewItem)right;
                DownloadItem first = (DownloadItem)a.Tag, second = (DownloadItem)b.Tag;
                int comparison;
                if (column == 1) comparison = Progress(first).CompareTo(Progress(second));
                else if (column == 3) comparison = first.TotalBytes.CompareTo(second.TotalBytes);
                else if (column == 4) comparison = first.BytesPerSecond.CompareTo(second.BytesPerSecond);
                else if (column == 5) comparison = Remaining(first).CompareTo(Remaining(second));
                else comparison = String.Compare(a.SubItems[column].Text, b.SubItems[column].Text, StringComparison.CurrentCultureIgnoreCase);
                return comparison * (ascending ? 1 : -1);
            }
            static double Progress(DownloadItem item) { return item.TotalBytes > 0 ? (double)item.BytesReceived / item.TotalBytes : 0; }
            static double Remaining(DownloadItem item) { return item.BytesPerSecond > 0 ? (double)Math.Max(0, item.TotalBytes - item.BytesReceived) / item.BytesPerSecond : item.State == DownloadState.Complete ? 0 : Double.PositiveInfinity; }
        }
    }
}
