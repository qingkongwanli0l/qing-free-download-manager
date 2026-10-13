using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class RefreshAddressDialog : Form
    {
        readonly bool chinese;
        readonly TextBox page = new TextBox();
        readonly TextBox candidateUrl = new TextBox();
        readonly TextBox candidateName = new TextBox();
        readonly Label status = new Label();
        readonly Label warning = new Label();
        readonly Button open = new Button();
        readonly Button accept = new Button();
        TaskCompletionSource<int> pending;

        public string ArmedPage { get; private set; }
        public DownloadItem Candidate { get; private set; }

        public RefreshAddressDialog(DownloadItem target, AppSettings settings)
        {
            chinese = settings.Language != null && settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            ArmedPage = "";
            Text = T("刷新下载地址", "Refresh download address");
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 540);
            MinimumSize = new Size(540, 430);
            ShowInTaskbar = false;
            MinimizeBox = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(0, 0, 12, 0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddText(fields, T("在原页面重新点击下载；收到同一页面的新地址后，请核对再更新。", "Open the original page and download again. Check the captured address before updating this task."));
            AddField(fields, T("原任务文件", "Original file"), new TextBox { Text = target.FileName, ReadOnly = true });
            AddField(fields, T("保存路径", "Output path"), new TextBox { Text = target.OutputPath, ReadOnly = true });
            page.Text = target.SourcePage ?? "";
            AddField(fields, T("原页面地址（未知时请填写）", "Original page address (enter if unknown)"), page);
            open.Text = T("打开原页面", "Open original page");
            open.AutoSize = true;
            open.Click += OpenPage;
            fields.Controls.Add(open);
            status.Text = T("尚未打开页面。", "No page opened yet.");
            status.AutoSize = true;
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(3, 8, 3, 8);
            fields.Controls.Add(status);
            candidateUrl.ReadOnly = true;
            candidateUrl.Multiline = true;
            candidateUrl.Height = 48;
            candidateUrl.ScrollBars = ScrollBars.Vertical;
            AddField(fields, T("新下载地址", "New download address"), candidateUrl);
            candidateName.ReadOnly = true;
            AddField(fields, T("新地址提供的名称（仅供核对）", "Captured name (for comparison only)"), candidateName);
            warning.Text = T("确认后保留同一任务及保存位置、下载选项，从头重新下载，不继续旧片段。已经保存的输出文件会保留，并按文件冲突设置处理。", "Confirmation keeps this task, its output location and download options, and downloads from the beginning without reusing old fragments. Saved output files remain and follow the existing-file setting.");
            warning.AutoSize = true;
            warning.Dock = DockStyle.Fill;
            warning.Margin = new Padding(3, 8, 3, 8);
            page.TextChanged += delegate
            {
                ArmedPage = "";
                status.Text = T("地址已修改，请打开原页面后重新捕获。", "Address changed. Open the page to capture again.");
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = T("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            accept.Text = T("更新并继续", "Update and continue");
            accept.AutoSize = true;
            accept.Enabled = false;
            accept.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(accept);
            layout.Controls.Add(fields, 0, 0);
            layout.Controls.Add(warning, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            AcceptButton = accept;
            CancelButton = cancel;
            Appearance.Apply(this);
        }

        public bool CanOffer(DownloadItem item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.Url) || pending != null || Candidate != null || IsDisposed || Disposing || DialogResult != DialogResult.None) return false;
            string source = PageAddress(item.SourcePage), armed = PageAddress(ArmedPage);
            return source != null && armed != null && String.Equals(source, armed, StringComparison.Ordinal);
        }

        public Task<int> OfferAsync(DownloadItem item)
        {
            if (!CanOffer(item)) return Task.FromResult(0);
            pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Candidate = item.Copy();
            candidateUrl.Text = Candidate.Url;
            candidateName.Text = Candidate.FileName;
            page.Enabled = open.Enabled = false;
            status.Text = T("已捕获同一页面的新地址，请核对文件后确认。", "Captured an address from the same page. Check the file before confirming.");
            accept.Enabled = true;
            Activate();
            return pending.Task;
        }

        public void Finish(int accepted)
        {
            if (pending != null) pending.TrySetResult(accepted == 1 ? 1 : 0);
        }

        public void Fail(Exception error)
        {
            if (pending != null) pending.TrySetException(error);
        }

        void OpenPage(object sender, EventArgs args)
        {
            string address = PageAddress(page.Text.Trim());
            if (address == null)
            {
                MessageBox.Show(this, T("请填写有效的 HTTP 或 HTTPS 页面地址。", "Enter a valid HTTP or HTTPS page address."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(page.Text.Trim()) { UseShellExecute = true })) { }
                ArmedPage = address;
                status.Text = T("等待此页面的新下载。其他页面的下载不会替换此任务。", "Waiting for a download from this page. Downloads from other pages will not replace this task.");
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static string PageAddress(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
            return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        }

        static void AddField(TableLayoutPanel fields, string caption, TextBox control)
        {
            AddText(fields, caption);
            control.Dock = DockStyle.Fill;
            fields.Controls.Add(control);
        }

        static void AddText(TableLayoutPanel fields, string text)
        {
            fields.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        }

        string T(string zh, string en) { return chinese ? zh : en; }
    }
}
