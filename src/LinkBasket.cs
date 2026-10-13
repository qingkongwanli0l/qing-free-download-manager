using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class LinkBasket : Form
    {
        readonly Func<AppSettings> settings;
        readonly Action save;
        readonly Func<IEnumerable<DownloadItem>> downloads;
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 0, 8, 0) };
        readonly Panel chart = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 247, 250) };
        readonly Queue<long> samples = new Queue<long>();
        readonly Timer timer = new Timer { Interval = 1000 };
        bool closingWithApplication;
        string T(string zh, string en) { return settings().Language == "zh-CN" ? zh : en; }

        public LinkBasket(Func<AppSettings> settings, Action save, Func<IEnumerable<DownloadItem>> downloads, Action add, Action startAll, Action stopAll, Action configure, Action<string> drop)
        {
            this.settings = settings; this.save = save; this.downloads = downloads;
            Text = T("Qing 链接篮", "Qing link basket"); Size = new Size(330, 225); MinimumSize = new Size(300, 205);
            Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; TopMost = settings().LinkBasketOnTop;
            StartPosition = FormStartPosition.Manual;
            Point position = new Point(settings().LinkBasketLeft ?? Screen.PrimaryScreen.WorkingArea.Right - Width - 24, settings().LinkBasketTop ?? Screen.PrimaryScreen.WorkingArea.Bottom - Height - 24);
            if (!Screen.AllScreens.Any(x => x.WorkingArea.IntersectsWith(new Rectangle(position, Size)))) position = Screen.PrimaryScreen.WorkingArea.Location;
            Location = position;
            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            AddButton(toolbar, T("添加", "Add"), add); AddButton(toolbar, T("全部开始", "Start all"), startAll); AddButton(toolbar, T("全部停止", "Stop all"), stopAll); AddButton(toolbar, T("设置", "Settings"), configure);
            var top = new ToolStripButton(T("置顶", "On top")) { CheckOnClick = true, Checked = TopMost };
            top.CheckedChanged += delegate { TopMost = settings().LinkBasketOnTop = top.Checked; save(); }; toolbar.Items.Add(top);
            Controls.Add(chart); Controls.Add(status); Controls.Add(toolbar);
            chart.Paint += DrawChart;
            foreach (Control surface in new Control[] { this, chart, status, toolbar })
            {
                surface.AllowDrop = true;
                surface.DragEnter += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
                surface.DragDrop += delegate(object sender, DragEventArgs e)
                {
                    string text = e.Data.GetDataPresent(DataFormats.FileDrop) ? String.Join(Environment.NewLine, (string[])e.Data.GetData(DataFormats.FileDrop)) : Convert.ToString(e.Data.GetData(DataFormats.Text));
                    drop(text);
                };
            }
            ResizeEnd += delegate { settings().LinkBasketLeft = Left; settings().LinkBasketTop = Top; save(); };
            timer.Tick += delegate { RefreshStatus(); }; timer.Start(); RefreshStatus();
            FormClosed += delegate { timer.Dispose(); if (!closingWithApplication) settings().ShowLinkBasket = false; settings().LinkBasketLeft = Left; settings().LinkBasketTop = Top; save(); };
            Appearance.Apply(this);
        }

        public void CloseWithApplication() { closingWithApplication = true; Close(); }

        static void AddButton(ToolStrip toolbar, string text, Action action) { var button = new ToolStripButton(text); button.Click += delegate { action(); }; toolbar.Items.Add(button); }
        void RefreshStatus()
        {
            List<DownloadItem> items = downloads().Where(x => x.State != DownloadState.History).ToList();
            long speed = items.Sum(x => x.BytesPerSecond); samples.Enqueue(speed); if (samples.Count > 45) samples.Dequeue();
            List<DownloadItem> active = items.Where(x => x.State == DownloadState.Running || x.State == DownloadState.Seeding).ToList();
            long size = active.Sum(x => x.TotalBytes), completed = active.Sum(x => x.BytesReceived);
            string progress = size > 0 ? Math.Min(100, 100d * completed / size).ToString("0.0") + "%" : "—";
            status.Text = (speed / 1024d).ToString("0.0") + " KiB/s · " + progress + Environment.NewLine + items.Count(x => x.CompletedAt.HasValue) + T(" 个已完成 · 拖入链接添加下载", " completed · Drop links here");
            chart.Invalidate();
        }
        void DrawChart(object sender, PaintEventArgs args)
        {
            long maximum = Math.Max(1, samples.Count == 0 ? 0 : samples.Max()); long[] values = samples.ToArray();
            float width = chart.ClientSize.Width / 45f;
            using (var brush = new SolidBrush(Color.FromArgb(38, 132, 160)))
                for (int i = 0; i < values.Length; i++)
                {
                    float height = (float)(values[i] / (double)maximum * Math.Max(1, chart.ClientSize.Height - 12));
                    args.Graphics.FillRectangle(brush, i * width, chart.ClientSize.Height - height, Math.Max(1, width - 2), height);
                }
        }
    }
}
