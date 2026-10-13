using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QingDownloadManager
{
    public sealed class IntegrationDialog : Form
    {
        const string HostName = "com.qing.download_manager";
        const string FirefoxId = "qing-download-manager@qingkongwanli0l.github.io";
        static readonly string[] ChromiumKeys = {
            @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
            @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
            @"Software\Chromium\NativeMessagingHosts\" + HostName
        };
        static readonly string[] FirefoxKeys = { @"Software\Mozilla\NativeMessagingHosts\" + HostName };
        readonly bool chinese;
        readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingFDM", "Integration");
        readonly TextBox extensionIds = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 74, Dock = DockStyle.Fill };
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };

        public IntegrationDialog(AppSettings settings)
        {
            chinese = settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            Text = L("Browser integration", "浏览器集成");
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(730, 610);
            Size = new Size(810, 690);
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(16) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(root, new Label { AutoSize = true, Text = L(
                "Connect your browser to Qing. Registration applies only to your Windows account.",
                "将浏览器连接到 Qing。注册仅对当前 Windows 账号生效。") });
            var tabs = new TabControl { Dock = DockStyle.Top, Height = 400 };
            var chromium = Page(tabs, "Chrome / Edge / Chromium");
            Add(chromium, Instructions(L(
                "1. Open chrome://extensions (Chrome) or edge://extensions (Edge).\r\n2. Enable Developer mode, choose Load unpacked, and select the chromium folder below.\r\n3. Copy the extension ID shown by the browser. Paste it below, then choose Register / repair. For multiple browsers or profiles, keep each ID on a separate line.",
                "1. 打开 Chrome 的 chrome://extensions 或 Edge 的 edge://extensions。\r\n2. 启用开发者模式，选择“加载已解压的扩展程序”，选取下面的 chromium 文件夹。\r\n3. 复制浏览器显示的扩展 ID，粘贴到下方后点击“注册 / 修复”。使用多个浏览器或个人资料时，每行保留一个 ID。")));
            Add(chromium, FolderControls("chromium"));
            Add(chromium, new Label { AutoSize = true, Text = L("Extension IDs (32 lowercase letters a–p, one per line)", "扩展 ID（32 个 a–p 小写字母，每行一个）") });
            Add(chromium, extensionIds);
            Add(chromium, Buttons(false));
            var firefox = Page(tabs, "Firefox");
            Add(firefox, Instructions(L(
                "Install a signed Qing Firefox extension through Firefox's Add-ons Manager. The unpacked firefox folder can be loaded temporarily through about:debugging → This Firefox → Load Temporary Add-on → manifest.json. Temporary installation ends when Firefox closes; registration alone does not install or sign an extension.\r\n\r\nIf the extension panel asks for website access, choose Allow website access and confirm in Firefox, then reload open pages. You can also manage site access in the extension's Permissions. Choose Register / repair below to connect the desktop application.",
                "在 Firefox 附加组件管理器中安装已签名的 Qing 扩展。未签名的 firefox 文件夹可通过 about:debugging →“此 Firefox”→“临时载入附加组件”→ manifest.json 临时加载。临时安装会在关闭 Firefox 后结束；本机注册不会安装扩展，也不会为扩展签名。\r\n\r\n如果扩展面板提示缺少网站权限，请点击“允许网站访问”并在 Firefox 中确认，然后刷新已打开的网页。也可在扩展的“权限”中管理网站访问。点击下方“注册 / 修复”连接桌面程序。")));
            Add(firefox, FolderControls("firefox"));
            Add(firefox, new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = FirefoxId });
            Add(firefox, Buttons(true));
            Add(root, tabs);
            Add(root, new Label { AutoSize = true, MaximumSize = new Size(740, 0), Text = L(
                "Open the extension popup to check the connection, enable automatic capture, or allow website login information. Removing the connection leaves the extension and local registration settings in place; Register / repair restores it. If you move a portable copy, run Register / repair from its new location.",
                "打开扩展弹窗检查连接，按需启用自动接管或网站登录状态。移除连接会保留扩展和本地集成设置，点击“注册 / 修复”即可恢复。移动便携版目录后，请在新位置重新执行“注册 / 修复”。") });
            Add(root, status);
            var close = new Button { Text = L("Close", "关闭"), AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
            Add(root, close);
            Controls.Add(root);
            CancelButton = close;
            LoadIds();
            Appearance.Apply(this);
        }

        string L(string english, string chineseText) { return chinese ? chineseText : english; }
        static void Add(TableLayoutPanel panel, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(3, 6, 3, 6);
            panel.Controls.Add(control, 0, row);
        }
        static TableLayoutPanel Page(TabControl tabs, string title)
        {
            var page = new TabPage(title) { Padding = new Padding(10) };
            var panel = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, AutoScroll = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(panel);
            tabs.TabPages.Add(page);
            return panel;
        }
        static TextBox Instructions(string text)
        {
            return new TextBox { Text = text, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window, Dock = DockStyle.Fill, Height = 142, ScrollBars = ScrollBars.Vertical };
        }
        Control FolderControls(string browser)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser", browser);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(new TextBox { ReadOnly = true, Text = folder, Dock = DockStyle.Fill }, 0, 0);
            var open = new Button { Text = L("Open folder", "打开文件夹"), AutoSize = true };
            open.Click += delegate { Run(delegate {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L("The extension folder is missing. Re-extract or reinstall Qing.", "扩展文件夹不存在，请重新解压或安装 Qing。"));
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }); };
            panel.Controls.Add(open, 1, 0);
            return panel;
        }
        Control Buttons(bool firefox)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var register = new Button { Text = L("Register / repair", "注册 / 修复"), AutoSize = true };
            var remove = new Button { Text = L("Remove connection", "移除连接"), AutoSize = true };
            register.Click += delegate { Run(delegate { Register(firefox); }); };
            remove.Click += delegate { Run(delegate { Remove(firefox); }); };
            panel.Controls.Add(register);
            panel.Controls.Add(remove);
            return panel;
        }
        void Run(Action action)
        {
            try { action(); }
            catch (Exception error) {
                status.Text = error.Message;
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        string ManifestPath(bool firefox) { return Path.Combine(directory, firefox ? "firefox.json" : "chromium.json"); }
        static IEnumerable<RegistryView> Views()
        {
            yield return RegistryView.Registry32;
            if (Environment.Is64BitOperatingSystem) yield return RegistryView.Registry64;
        }
        internal static bool MatchesRegistration(object value, string manifest)
        {
            return String.Equals(value as string, manifest, StringComparison.OrdinalIgnoreCase);
        }
        internal static string CreateManifest(string executable, bool firefox, string[] ids)
        {
            var manifest = new Dictionary<string, object> {
                { "name", HostName }, { "description", "Qing Free Download Manager" },
                { "path", executable }, { "type", "stdio" }
            };
            if (firefox) manifest["allowed_extensions"] = new[] { FirefoxId };
            else {
                var origins = new List<string>();
                foreach (string id in ids) {
                    if (!Regex.IsMatch(id, "^[a-p]{32}$")) throw new ArgumentException("Extension IDs must contain exactly 32 lowercase letters a-p.");
                    string origin = "chrome-extension://" + id + "/";
                    if (!origins.Contains(origin)) origins.Add(origin);
                }
                if (origins.Count == 0) throw new ArgumentException("Enter an extension ID first.");
                manifest["allowed_origins"] = origins;
            }
            return new JavaScriptSerializer().Serialize(manifest);
        }
        void Register(bool firefox)
        {
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QingFDM.NativeHost.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException(L("QingFDM.NativeHost.exe is missing. Re-extract or reinstall Qing.", "QingFDM.NativeHost.exe 不存在，请重新解压或安装 Qing。"));
            string[] ids = extensionIds.Text.Split(new[] { '\r', '\n', ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            string text = CreateManifest(executable, firefox, ids);
            string manifest = ManifestPath(firefox);
            string[] keys = firefox ? FirefoxKeys : ChromiumKeys;
            foreach (RegistryView view in Views())
                using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    foreach (string path in keys)
                        using (RegistryKey key = user.OpenSubKey(path))
                            if (key != null && key.GetValue("") != null && !MatchesRegistration(key.GetValue(""), manifest))
                                throw new InvalidOperationException(L("An existing registration points to another file. It was not changed: ", "已有注册指向其他文件，未进行更改：") + path);
            Directory.CreateDirectory(directory);
            File.WriteAllText(manifest, text, new UTF8Encoding(false));
            foreach (RegistryView view in Views())
                using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    foreach (string path in keys)
                        using (RegistryKey key = user.CreateSubKey(path))
                            key.SetValue("", manifest, RegistryValueKind.String);
            status.Text = L("Registered. Open the browser extension popup and refresh its connection.", "注册完成。请打开浏览器扩展弹窗并刷新连接状态。");
        }
        void Remove(bool firefox)
        {
            string manifest = ManifestPath(firefox);
            int removed = RemoveRegistration(manifest, firefox);
            status.Text = removed > 0
                ? L("Connection removed. Register / repair restores it.", "连接已移除，点击“注册 / 修复”可以恢复。")
                : L("No matching Qing registration was found. Other registrations were left unchanged.", "未找到匹配的 Qing 注册。其他注册未被更改。");
        }
        static int RemoveRegistration(string manifest, bool firefox)
        {
            int removed = 0;
            foreach (RegistryView view in Views())
                using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    foreach (string path in firefox ? FirefoxKeys : ChromiumKeys)
                    {
                        bool empty = false;
                        using (RegistryKey key = user.OpenSubKey(path, true))
                        {
                            if (key == null || !MatchesRegistration(key.GetValue(""), manifest)) continue;
                            key.DeleteValue("", false);
                            empty = key.ValueCount == 0 && key.SubKeyCount == 0;
                            removed++;
                        }
                        if (empty) user.DeleteSubKey(path, false);
                    }
            return removed;
        }
        public static void UnregisterCurrentInstall()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingFDM", "Integration");
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QingFDM.NativeHost.exe");
            foreach (bool firefox in new[] { false, true })
            {
                string file = Path.Combine(directory, firefox ? "firefox.json" : "chromium.json");
                if (!File.Exists(file)) continue;
                Dictionary<string, object> manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                object registered;
                if (manifest.TryGetValue("path", out registered) && String.Equals(registered as string, executable, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveRegistration(file, firefox); File.Delete(file);
                }
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                string command = "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QingFDM.exe") + "\" --background";
                if (key != null && String.Equals(key.GetValue("QingFDM") as string, command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("QingFDM", false);
            }
        }
        void LoadIds()
        {
            string file = ManifestPath(false);
            if (!File.Exists(file)) return;
            try
            {
                var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                object origins;
                var ids = new List<string>();
                if (manifest != null && manifest.TryGetValue("allowed_origins", out origins))
                    foreach (object origin in (IEnumerable)origins)
                    {
                        Match match = Regex.Match(Convert.ToString(origin), "^chrome-extension://([a-p]{32})/$");
                        if (match.Success && !ids.Contains(match.Groups[1].Value)) ids.Add(match.Groups[1].Value);
                    }
                extensionIds.Text = String.Join(Environment.NewLine, ids);
            }
            catch (Exception error) { status.Text = L("Saved IDs could not be read: ", "无法读取已保存的 ID：") + error.Message; }
        }
    }
}

