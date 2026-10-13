using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class DebridDialog : Form
    {
        readonly AppSettings settings;
        readonly DebridClient client = new DebridClient();
        readonly ListBox accounts = new ListBox { Dock = DockStyle.Fill, DisplayMember = "Name" };
        readonly ComboBox provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox name = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox token = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        readonly CheckBox enabled = new CheckBox { AutoSize = true };
        readonly Label accountStatus = new Label { AutoSize = true, Dock = DockStyle.Fill };
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill };
        readonly DataGridView hosts = Grid();
        readonly DataGridView generated = Grid();
        readonly TextBox links = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 95 };
        readonly TextBox password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        readonly TextBox directory = new TextBox { Dock = DockStyle.Fill };
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly Button stop = new Button { AutoSize = true, Enabled = false };
        readonly Button save = new Button { AutoSize = true };
        readonly List<Control> busyControls = new List<Control>();
        readonly string[] providers = { "alldebrid", "real-debrid", "debrid-link" };
        CancellationTokenSource operation;
        DebridAccount editing;
        bool loading;

        public List<DebridAccount> Accounts { get; private set; }
        public List<DownloadItem> Items { get; private set; }

        public DebridDialog(AppSettings appSettings)
        {
            settings = appSettings;
            Accounts = settings.DebridAccounts.ConvertAll(account => account.Copy());
            Items = new List<DownloadItem>();
            Text = L("Debrid accounts and links", "Debrid 账号与链接");
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            Size = new Size(920, 720);
            MinimumSize = new Size(790, 640);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(accounts, 0, 0);
            var accountButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            accountButtons.Controls.Add(Button(L("Add", "添加"), delegate {
                SaveEdited();
                var account = new DebridAccount { Provider = providers[0], Name = DebridClient.Name(providers[0]) };
                Accounts.Add(account); ReloadAccounts(account);
            }));
            accountButtons.Controls.Add(Button(L("Remove", "移除"), delegate {
                if (editing == null) return;
                Accounts.Remove(editing); editing = null;
                ReloadAccounts(Accounts.FirstOrDefault());
            }));
            left.Controls.Add(accountButtons, 0, 1);
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(tabs, 1, 0);

            var accountPage = new TabPage(L("Account / hosters", "账号 / 文件托管站点")) { Padding = new Padding(10) };
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (string value in providers) provider.Items.Add(DebridClient.Name(value));
            Field(form, 0, L("Service", "服务"), provider);
            Field(form, 1, L("Account label", "账号名称"), name);
            Field(form, 2, L("API token", "API 令牌"), token);
            enabled.Text = L("Enable this account", "启用此账号"); form.Controls.Add(enabled, 1, 3);
            var accountActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            accountActions.Controls.Add(Button(L("Get my token", "获取我的令牌"), delegate {
                if (provider.SelectedIndex >= 0) Process.Start(new ProcessStartInfo(DebridClient.TokenPage(providers[provider.SelectedIndex])) { UseShellExecute = true });
            }));
            accountActions.Controls.Add(Button(L("Refresh account and hosters", "刷新账号与站点状态"), async delegate { await RefreshAccount(); }));
            form.Controls.Add(accountActions, 0, 4); form.SetColumnSpan(accountActions, 2);
            var hint = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(610, 0), Text = L(
                "Use a token issued to your own service account. A disabled account sends no requests. Refresh retrieves current membership and hoster availability.",
                "填写服务为你本人账号签发的令牌。停用账号后不会发送请求。刷新可查询当前会员信息和文件托管站点可用状态。") };
            form.Controls.Add(hint, 0, 5); form.SetColumnSpan(hint, 2);
            form.Controls.Add(accountStatus, 0, 6); form.SetColumnSpan(accountStatus, 2);
            hosts.Columns.Add("name", L("Hoster", "站点"));
            hosts.Columns.Add("domains", L("Domains", "域名"));
            hosts.Columns.Add("status", L("Status", "状态"));
            hosts.Columns.Add("quota", L("Remaining quota", "剩余额度"));
            form.Controls.Add(hosts, 0, 7); form.SetColumnSpan(hosts, 2);
            accountPage.Controls.Add(form); tabs.TabPages.Add(accountPage);

            var linksPage = new TabPage(L("Generate download links", "生成下载链接")) { Padding = new Padding(10) };
            var linkForm = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
            linkForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            linkForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) linkForm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            linkForm.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var intro = new Label { AutoSize = true, MaximumSize = new Size(610, 0), Text = L(
                "Use the selected account to generate links, one HTTP/HTTPS hoster link per line. Generating links may consume the service's quota. Check the results, then save to add selected files.",
                "使用左侧选中的账号生成直链，每行一个 HTTP/HTTPS 文件链接。生成链接可能消耗服务额度。检查结果后，点击保存将勾选的文件加入下载列表。") };
            linkForm.Controls.Add(intro, 0, 0); linkForm.SetColumnSpan(intro, 2);
            linkForm.Controls.Add(links, 0, 1); linkForm.SetColumnSpan(links, 2);
            Field(linkForm, 2, L("Link password (optional)", "链接密码（可选）"), password);
            directory.Text = settings.DownloadDirectory;
            Field(linkForm, 3, L("Save folder", "保存文件夹"), directory);
            linkForm.Controls.Add(Button(L("Generate links", "生成链接"), async delegate { await Generate(); }), 1, 4);
            generated.ReadOnly = false;
            generated.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = L("Add", "添加"), FillWeight = 30 });
            foreach (var column in new[] { new[] { "file", L("File", "文件") }, new[] { "size", L("Bytes", "字节") },
                new[] { "result", L("Result", "结果") }, new[] { "url", L("Source", "来源链接") } })
                generated.Columns.Add(new DataGridViewTextBoxColumn { Name = column[0], HeaderText = column[1], ReadOnly = true });
            linkForm.Controls.Add(generated, 0, 5); linkForm.SetColumnSpan(generated, 2);
            linksPage.Controls.Add(linkForm); tabs.TabPages.Add(linksPage);

            root.Controls.Add(status, 0, 1); root.SetColumnSpan(status, 2);
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            save.Text = L("Save and add selected", "保存并添加所选");
            save.Click += delegate {
                SaveEdited();
                generated.EndEdit();
                Items = generated.Rows.Cast<DataGridViewRow>().Where(row => row.Tag is DownloadItem && Object.Equals(row.Cells[0].Value, true))
                    .Select(row => (DownloadItem)row.Tag).ToList();
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = new Button { Text = L("Cancel", "取消"), AutoSize = true };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            stop.Text = L("Stop request", "停止请求");
            stop.Click += delegate { if (operation != null) operation.Cancel(); };
            bottom.Controls.Add(save); bottom.Controls.Add(cancel); bottom.Controls.Add(stop);
            root.Controls.Add(bottom, 0, 2); root.SetColumnSpan(bottom, 2);
            Controls.Add(root);
            CancelButton = cancel;
            busyControls.AddRange(new Control[] { accounts, provider, name, token, enabled, accountButtons, links, password, directory, save });
            accounts.SelectedIndexChanged += delegate {
                if (loading) return;
                SaveEdited(); editing = accounts.SelectedItem as DebridAccount; LoadEdited();
            };
            ReloadAccounts(Accounts.FirstOrDefault());
            FormClosing += delegate { if (operation != null) operation.Cancel(); };
            FormClosed += delegate { client.Dispose(); };
            Appearance.Apply(this);
        }

        string L(string english, string chineseText) { return Localization.Text(settings.Language, english, chineseText); }
        static DataGridView Grid()
        {
            return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window };
        }
        static void Field(TableLayoutPanel panel, int row, string caption, Control control)
        {
            panel.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, row);
            control.Margin = new Padding(3, 6, 3, 6); panel.Controls.Add(control, 1, row);
        }
        Button Button(string caption, EventHandler action)
        {
            var button = new Button { Text = caption, AutoSize = true };
            button.Click += action; busyControls.Add(button); return button;
        }
        void ReloadAccounts(DebridAccount select)
        {
            loading = true; accounts.Items.Clear();
            foreach (var account in Accounts) accounts.Items.Add(account);
            accounts.SelectedItem = select; editing = select; loading = false; LoadEdited();
        }
        void LoadEdited()
        {
            provider.SelectedIndex = editing == null ? 0 : System.Array.IndexOf(providers, editing.Provider);
            name.Text = editing == null ? "" : editing.Name;
            token.Text = editing == null ? "" : editing.ApiToken;
            enabled.Checked = editing != null && editing.Enabled;
            accountStatus.Text = editing == null ? L("Add an account to begin.", "请先添加账号。") : L("Not refreshed.", "尚未刷新。");
            hosts.Rows.Clear();
        }
        void SaveEdited()
        {
            if (editing == null) return;
            if (provider.SelectedIndex >= 0) editing.Provider = providers[provider.SelectedIndex];
            editing.Name = String.IsNullOrWhiteSpace(name.Text) ? DebridClient.Name(editing.Provider) : name.Text.Trim();
            editing.ApiToken = token.Text.Trim(); editing.Enabled = enabled.Checked;
            accounts.Invalidate();
        }
        DebridAccount Selected()
        {
            SaveEdited();
            if (editing == null) throw new InvalidOperationException(L("Select an account first.", "请先选择账号。"));
            if (!editing.Enabled) throw new InvalidOperationException(L("This account is disabled.", "此账号已停用。"));
            if (String.IsNullOrWhiteSpace(editing.ApiToken)) throw new InvalidOperationException(L("Enter your API token first.", "请先填写 API 令牌。"));
            return editing.Copy();
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (operation != null) return;
            operation = new CancellationTokenSource();
            foreach (Control control in busyControls) control.Enabled = false;
            stop.Enabled = true; status.Text = L("Contacting the service…", "正在连接服务…");
            try { await action(operation.Token); }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = L("Request stopped or timed out. Completed results remain below.", "请求已停止或超时。已完成的结果保留在下方。"); }
            catch (Exception error) { if (!IsDisposed) status.Text = Localization.Error(settings.Language, error.Message); }
            finally {
                operation.Dispose(); operation = null;
                if (!IsDisposed) { foreach (Control control in busyControls) control.Enabled = true; stop.Enabled = false; }
            }
        }
        async Task RefreshAccount()
        {
            await Run(async cancel => {
                DebridAccount account = Selected();
                DebridStatus info = await client.RefreshAsync(account, cancel);
                if (IsDisposed) return;
                string type = info.AccountType == "premium" ? L("Premium", "会员") : info.AccountType == "lifetime" ? L("Lifetime", "终身会员")
                    : info.AccountType == "trial" ? L("Trial", "试用") : L("Free", "免费");
                accountStatus.Text = info.Username + " · " + type + " · " + L("Expires: ", "到期：") +
                    (info.ExpiresAt.HasValue ? info.ExpiresAt.Value.ToString("g") : L("Not provided / not applicable", "未提供 / 不适用"));
                hosts.Rows.Clear();
                foreach (DebridHost host in info.Hosts) {
                    string state = host.Status == "online" ? L("Online", "在线") : host.Status == "offline" ? L("Offline", "离线")
                        : host.Status == "unsupported" ? L("Unsupported", "不支持") : L("Unknown", "未知");
                    hosts.Rows.Add(host.Name, host.Domains, state, host.Quota);
                }
                status.Text = L("Updated: ", "更新时间：") + DateTime.Now.ToString("g");
            });
        }
        async Task Generate()
        {
            await Run(async cancel => {
                DebridAccount account = Selected();
                string folder = directory.Text.Trim();
                if (!System.IO.Path.IsPathRooted(folder)) throw new ArgumentException(L("Choose an absolute save folder.", "请填写完整的保存文件夹路径。"));
                string[] urls = links.Lines.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct().ToArray();
                if (urls.Length == 0) throw new ArgumentException(L("Enter at least one hoster link.", "请至少填写一个文件链接。"));
                generated.Rows.Clear();
                int successes = 0, failures = 0;
                foreach (string url in urls) {
                    cancel.ThrowIfCancellationRequested();
                    status.Text = L("Generating ", "正在生成 ") + (successes + failures + 1) + " / " + urls.Length;
                    try {
                        List<DownloadItem> items = await client.GenerateAsync(account, url, password.Text, folder, settings.Connections, cancel);
                        if (IsDisposed) return;
                        foreach (DownloadItem item in items) {
                            int row = generated.Rows.Add(true, item.FileName, item.TotalBytes > 0 ? item.TotalBytes.ToString("N0") : "", L("Ready", "就绪"), url);
                            generated.Rows[row].Tag = item;
                        }
                        successes++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) {
                        if (IsDisposed) return;
                        int row = generated.Rows.Add(false, "", "", Localization.Error(settings.Language, error.Message), url);
                        generated.Rows[row].Cells[0].ReadOnly = true; failures++;
                    }
                    if (successes + failures < urls.Length) await Task.Delay(300, cancel);
                }
                status.Text = L("Completed links: ", "生成成功：") + successes + L("; failed: ", "；失败：") + failures;
            });
        }
    }
}

