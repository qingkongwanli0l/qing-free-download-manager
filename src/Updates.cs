using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class ReleaseInfo
    {
        public string Tag, Notes, PageUrl, InstallerUrl, InstallerHash;
        public Version Version;
        public long InstallerSize;
    }

    public static class Updates
    {
        const string Repository = "qingkongwanli0l/qing-free-download-manager";
        public static bool Due(AppSettings settings, DateTime now)
        {
            return settings.CheckUpdates && (!settings.LastUpdateCheckUtc.HasValue || now >= settings.LastUpdateCheckUtc.Value.AddDays(settings.UpdateCheckDays));
        }

        public static ReleaseInfo ReadRelease(string text, string architecture)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            if (JsonObject.String(data, "draft") == "True" || JsonObject.String(data, "prerelease") == "True") return null;
            string tag = JsonObject.String(data, "tag_name"); Version version;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out version)) throw new InvalidDataException("Unrecognized release version: " + tag);
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            var release = new ReleaseInfo { Tag = tag, Version = version, Notes = JsonObject.String(data, "body"), PageUrl = "https://github.com/" + Repository + "/releases/tag/" + Uri.EscapeDataString(tag) };
            string name = "QingFDM-" + version.ToString(4) + "-" + architecture + "-setup.exe";
            foreach (var asset in JsonObject.Objects(data, "assets"))
            {
                if (JsonObject.String(asset, "name") != name) continue;
                string digest = JsonObject.String(asset, "digest"), url = JsonObject.String(asset, "browser_download_url");
                string expected = "https://github.com/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name;
                if (url != expected || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71) continue;
                string hash = digest.Substring(7);
                bool valid = true; foreach (char c in hash) if (!Uri.IsHexDigit(c)) valid = false;
                if (!valid) continue;
                release.InstallerUrl = url; release.InstallerHash = hash; release.InstallerSize = (long)JsonObject.Number(asset, "size");
                break;
            }
            return release;
        }

        public static async Task<ReleaseInfo> LatestAsync(HttpClient client, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/latest"))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                request.Headers.UserAgent.ParseAdd("QingFDM/" + Application.ProductVersion);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    return ReadRelease(await response.Content.ReadAsStringAsync().ConfigureAwait(false), IntPtr.Size == 8 ? "x64" : "x86");
                }
            }
        }

        public static async Task DownloadAsync(HttpClient client, ReleaseInfo release, string destination, Action<long> progress, CancellationToken token)
        {
            string temporary = destination + ".partial";
            try
            {
                using (HttpResponseMessage response = await client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] buffer = new byte[65536]; long total = 0; int count;
                        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                            sha.TransformBlock(buffer, 0, count, buffer, 0); total += count; progress(total);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        string digest = BitConverter.ToString(sha.Hash).Replace("-", "");
                        if (total != release.InstallerSize || !String.Equals(digest, release.InstallerHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update size or SHA256 digest does not match its release.");
                    }
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class UpdateDialog : Form
    {
        readonly AppSettings settings;
        readonly Action save;
        readonly HttpClient client;
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        readonly TextBox notes = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly Button install = new Button { AutoSize = true, Enabled = false };
        readonly Button skip = new Button { AutoSize = true, Enabled = false };
        ReleaseInfo release;
        public string InstallerPath { get; private set; }
        string T(string zh, string en) { return settings.Language == "zh-CN" ? zh : en; }

        public UpdateDialog(AppSettings settings, Action save, HttpClient client, ReleaseInfo knownRelease = null)
        {
            this.settings = settings; this.save = save; this.client = client; release = knownRelease;
            Text = T("检查更新", "Check for updates"); Font = new Font("Segoe UI", 9F); Size = new Size(640, 460); MinimumSize = new Size(480, 330); StartPosition = FormStartPosition.CenterParent;
            var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = T("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            install.Text = T("下载并安装", "Download and install"); skip.Text = T("跳过此版本", "Skip this version");
            var never = new CheckBox { Text = T("不自动检查更新", "Never check automatically"), AutoSize = true, Checked = !settings.CheckUpdates };
            never.CheckedChanged += delegate { settings.CheckUpdates = !never.Checked; save(); };
            var page = new Button { Text = T("发行版页面", "Release page"), AutoSize = true };
            page.Click += delegate { try { Process.Start(release == null ? "https://github.com/qingkongwanli0l/qing-free-download-manager/releases" : release.PageUrl); } catch (Exception error) { status.Text = error.Message; } };
            skip.Click += delegate { settings.SkippedUpdateVersion = release.Tag; save(); Close(); };
            install.Click += Install;
            controls.Controls.AddRange(new Control[] { close, install, skip, page, never });
            Controls.Add(notes); Controls.Add(status); Controls.Add(controls); CancelButton = close;
            Shown += Inspect; FormClosing += delegate { cancellation.Cancel(); };
            Disposed += delegate { cancellation.Dispose(); };
            Appearance.Apply(this);
        }

        async void Inspect(object sender, EventArgs args)
        {
            status.Text = T("正在检查…", "Checking…");
            try
            {
                if (release == null)
                {
                    settings.LastUpdateCheckUtc = DateTime.UtcNow; save();
                    release = await Updates.LatestAsync(client, cancellation.Token);
                }
                if (IsDisposed) return;
                bool newer = release != null && release.Version > new Version(Application.ProductVersion);
                status.Text = release == null ? T("尚无公开的稳定发行版。", "No public stable release is available.") : T("当前版本：", "Installed: ") + Application.ProductVersion + T("；最新版本：", "; latest: ") + release.Tag;
                notes.Text = release == null ? "" : release.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                skip.Enabled = newer;
                install.Enabled = newer && release.InstallerUrl != null && !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable"));
                if (newer && !install.Enabled) status.Text += T(" 请从发行版页面取得适合的安装包或便携版。", " Get the appropriate installer or portable package from the release page.");
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = T("检查已取消或超时。", "The check was canceled or timed out."); }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
        }

        async void Install(object sender, EventArgs args)
        {
            install.Enabled = skip.Enabled = false;
            string directory = Path.Combine(Path.GetTempPath(), "QingFDM-Update-" + Guid.NewGuid().ToString("N"));
            string destination = Path.Combine(directory, "QingFDM-setup.exe");
            try
            {
                Directory.CreateDirectory(directory);
                var progress = new Progress<long>(bytes =>
                {
                    if (!IsDisposed) status.Text = T("正在下载更新：", "Downloading update: ") + bytes.ToString("N0", CultureInfo.CurrentCulture) + " / " + release.InstallerSize.ToString("N0", CultureInfo.CurrentCulture);
                });
                await Updates.DownloadAsync(client, release, destination, ((IProgress<long>)progress).Report, cancellation.Token);
                if (IsDisposed) return;
                InstallerPath = destination; DialogResult = DialogResult.OK; Close();
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = T("下载已取消或超时。", "The download was canceled or timed out."); }
            catch (Exception error) { if (!IsDisposed) { status.Text = error.Message; install.Enabled = skip.Enabled = true; } }
            finally
            {
                if (InstallerPath == null)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    if (Directory.Exists(directory)) Directory.Delete(directory);
                }
            }
        }
    }
}
