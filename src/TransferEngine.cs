using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public sealed class DownloadMetadata
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public long TotalBytes { get; set; }
        public string SourceUrl { get; set; }
        public string SourceAddressHash { get; set; }
        [ScriptIgnore] public string DirectUrl { get; set; }
        public string ETag { get; set; }
        public string LastModified { get; set; }
        public bool AcceptRanges { get; set; }
        public DownloadMetadata() { FileName = ContentType = SourceUrl = SourceAddressHash = DirectUrl = ETag = LastModified = ""; TotalBytes = -1; }
    }

    public sealed class TransferEngine : IDisposable
    {
        private sealed class Transfer
        {
            internal DownloadItem Item;
            internal CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task Task;
            internal DownloadItem Restart;
            internal bool Removed, DeleteFiles, DeleteCompletedOutput, PreDownload;
        }
        private readonly string dataDirectory;
        private readonly object sync = new object();
        private readonly Dictionary<string, Transfer> transfers = new Dictionary<string, Transfer>();
        private readonly SemaphoreSlim daemonLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient rpcClient;
        private AppSettings settings;
        private Process daemon;
        private ProcessJob daemonJob;
        private string rpcAddress, rpcSecret;
        private bool disposed, daemonDht, daemonLpd, daemonSettingsPending;
        private int torrentTcpPort, torrentUdpPort;
        private readonly HashSet<string> torrentUsers = new HashSet<string>();
        private PortMapping portMapping;
        private Task daemonRefresh, mappingCleanup;
        public event Action<DownloadItem> Changed;
        public Func<DownloadItem, Task<ExistingFileAction>> ConflictResolver { get; set; }
        public string PortMappingStatus { get { lock (sync) return daemonSettingsPending ? "BitTorrent network settings will apply when active transfers stop." : portMapping == null ? "" : portMapping.Status; } }

        public TransferEngine(string dataDirectory, AppSettings settings)
        {
            this.dataDirectory = dataDirectory;
            this.settings = settings.Copy();
            Directory.CreateDirectory(dataDirectory);
            rpcClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        }

        public void Start(DownloadItem item)
        {
            StartTransfer(item, false);
        }

        public Task PreDownloadAsync(DownloadItem item)
        {
            Uri address;
            if (item.Kind != "file" || item.Method != "GET" || IsTorrent(item) || !Uri.TryCreate(item.Url, UriKind.Absolute, out address) ||
                !(address.Scheme == "http" || address.Scheme == "https") ||
                address.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || address.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only ordinary HTTP(S) GET file downloads can start before confirmation.");
            return StartTransfer(item, true);
        }

        private Task StartTransfer(DownloadItem item, bool preDownload)
        {
            Task task;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("TransferEngine");
                Transfer existing;
                if (transfers.TryGetValue(item.Id, out existing))
                {
                    if (existing.Cancellation.IsCancellationRequested) existing.Restart = item;
                    return existing.Task;
                }
                if (!preDownload && item.CompletedAt.HasValue && !IsTorrent(item))
                {
                    item.State = DownloadState.Complete;
                    return Task.FromResult(0);
                }
                Transfer transfer = new Transfer { Item = item, PreDownload = preDownload };
                transfers.Add(item.Id, transfer);
                item.State = DownloadState.Running;
                item.Error = "";
                item.DirectUrl = "";
                item.MediaFiles = new List<MediaFileInformation>();
                item.Phase = item.Kind == "media" || item.Kind == "audio" || item.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "metadata" : "download";
                item.UploadBytesPerSecond = 0;
                if (item.Kind == "media" || item.Kind == "audio")
                    item.MediaVideoBytes = item.MediaVideoTotal = item.MediaAudioBytes = item.MediaAudioTotal = 0;
                if (item.OwnedFiles.Count == 0) item.CompletedAt = null;
                transfer.Task = Task.Run(delegate { return RunAsync(transfer); });
                task = transfer.Task;
            }
            Notify(item);
            return task;
        }

        public void Pause(DownloadItem item) { Stop(item, false, false); }
        public void Remove(DownloadItem item, bool deleteFiles = false) { Stop(item, true, deleteFiles); }
        public async Task ResetPartialAsync(DownloadItem item)
        {
            Task pending = null;
            lock (sync)
            {
                Transfer transfer;
                if (transfers.TryGetValue(item.Id, out transfer)) pending = transfer.Task;
                Stop(item, true, false); item.State = DownloadState.Paused;
            }
            if (pending != null) await pending.ConfigureAwait(false);
            else if (item.EngineId.Length > 0 && daemon != null && !daemon.HasExited)
            {
                try { await RpcAsync("aria2.forceRemove", CancellationToken.None, item.EngineId).ConfigureAwait(false); }
                catch (AriaException) { }
            }
            if (item.WorkingDirectory.Length > 0) DeleteWorkingDirectory(item);
            else
            {
                string partial = PartialPath(item);
                File.Delete(partial); File.Delete(partial + ".aria2");
            }
            item.WorkingDirectory = item.EngineId = "";
            item.DirectUrl = "";
            item.MediaFiles.Clear();
            item.BytesReceived = item.TotalBytes = item.MediaAudioBytes = item.MediaAudioTotal = item.MediaVideoBytes = item.MediaVideoTotal = 0;
            item.CompletedAt = null; item.State = DownloadState.Paused;
        }

        private void Stop(DownloadItem item, bool removed, bool deleteFiles)
        {
            bool deleteImmediately = false;
            bool completed = item.State == DownloadState.Complete || item.State == DownloadState.Seeding || item.CompletedAt.HasValue;
            lock (sync)
            {
                item.State = removed ? DownloadState.Stopped : DownloadState.Paused;
                item.BytesPerSecond = 0;
                item.UploadBytesPerSecond = 0;
                item.Phase = "";
                Transfer transfer;
                if (transfers.TryGetValue(item.Id, out transfer))
                {
                    transfer.Restart = null;
                    transfer.Removed = removed;
                    transfer.DeleteFiles = deleteFiles;
                    transfer.DeleteCompletedOutput = completed;
                    transfer.Cancellation.Cancel();
                }
                else deleteImmediately = deleteFiles;
            }
            if (deleteImmediately) DeleteTransferFiles(item, completed);
            Notify(item);
        }

        public void ApplySettings(AppSettings value)
        {
            PortMapping stop = null;
            lock (sync)
            {
                settings = value.Copy();
                if (!settings.UpnpEnabled && portMapping != null) { stop = portMapping; portMapping = null; }
                if (settings.UpnpEnabled && torrentUsers.Count > 0 && portMapping == null)
                {
                    portMapping = new PortMapping(torrentTcpPort, torrentUdpPort, daemonDht, settings.Language);
                    portMapping.Start();
                }
                daemonSettingsPending = daemon != null && (daemonDht != settings.DhtEnabled || daemonLpd != settings.LocalPeerDiscovery);
                if (daemonSettingsPending && (daemonRefresh == null || daemonRefresh.IsCompleted))
                    daemonRefresh = Task.Run((Func<Task>)RefreshDaemonWhenIdleAsync);
            }
            if (stop != null) mappingCleanup = StopMappingAsync(stop);
        }

        private static async Task StopMappingAsync(PortMapping mapping)
        {
            await Task.WhenAny(mapping.StopAsync(), Task.Delay(2000)).ConfigureAwait(false);
        }

        private async Task RefreshDaemonWhenIdleAsync()
        {
            while (!disposed)
            {
                bool idle;
                lock (sync) { if (!daemonSettingsPending) return; idle = transfers.Count == 0; }
                if (idle)
                {
                    await daemonLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        lock (sync)
                        {
                            if (transfers.Count == 0)
                            {
                                if (daemonJob != null) { daemonJob.Dispose(); daemonJob = null; }
                                if (daemon != null) { daemon.Dispose(); daemon = null; }
                                daemonSettingsPending = false;
                                return;
                            }
                        }
                    }
                    finally { daemonLock.Release(); }
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        private AppSettings Settings() { lock (sync) return settings.Copy(); }
        private void Notify(DownloadItem item)
        {
            Action<DownloadItem> handler = Changed;
            if (handler != null && !disposed) handler(item);
        }

        private async Task RunAsync(Transfer transfer)
        {
            DownloadItem item = transfer.Item;
            CancellationToken token = transfer.Cancellation.Token;
            bool complete = false;
            try
            {
                AppSettings current = Settings();
                foreach (QueueSettings queue in current.Queues)
                {
                    if (queue.Id != item.QueueId) continue;
                    current.RetryCount = queue.RetryCount;
                    current.RetryDelaySeconds = queue.RetryDelaySeconds;
                    break;
                }
                if (item.Schedule != null)
                {
                    current.RetryCount = item.Schedule.RetryCount;
                    current.RetryDelaySeconds = item.Schedule.RetryDelaySeconds;
                }
                if (item.Directory.Length == 0) item.Directory = current.DownloadDirectory;
                Directory.CreateDirectory(item.Directory);
                bool ordinaryFile = item.Kind != "media" && item.Kind != "audio" && !IsTorrent(item);
                bool automaticName = item.FileName.Length == 0;
                if (ordinaryFile)
                {
                    if (transfer.PreDownload)
                    {
                        DownloadMetadata source = await InspectMetadataAsync(item, current, token).ConfigureAwait(false);
                        string type = source.ContentType.Split(';')[0].Trim().ToLowerInvariant();
                        if (type == "application/dash+xml" || type == "application/vnd.apple.mpegurl" || type == "application/x-mpegurl" || type == "audio/mpegurl" || type == "audio/x-mpegurl")
                        {
                            item.State = DownloadState.Paused;
                            item.Error = Localization.Text(current.Language, "Streaming media starts after confirmation.", "流媒体需确认后开始。");
                            return;
                        }
                        if (automaticName && source.FileName.Length > 0) item.FileName = source.FileName;
                    }
                    else if (automaticName && item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                        (item.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
                        item.FileName = await ServerFileNameAsync(item, current, token).ConfigureAwait(false);
                    PrepareFileName(item);
                    if (!transfer.PreDownload && !await ResolveFileConflictAsync(item, current).ConfigureAwait(false)) return;
                }
                PrepareWorkingDirectory(item, current);
                if (item.Kind == "media" || item.Kind == "audio")
                {
                    await RunMediaAsync(item, current, token).ConfigureAwait(false);
                }
                else if (item.Url.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase) || !item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || !IsTorrent(item) &&
                    (item.UseCurl && (item.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) || HasRequestCredentials(item, current)))
                    await RunCurlAsync(item, current, token, automaticName).ConfigureAwait(false);
                else await RunAriaAsync(transfer, current, token, automaticName).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (transfer.PreDownload) { item.State = DownloadState.Paused; return; }
                if (item.State == DownloadState.Skipped) return;
                if (ordinaryFile)
                {
                    if (!await ResolveFileConflictAsync(item, current).ConfigureAwait(false)) return;
                    token.ThrowIfCancellationRequested();
                    if (!await CommitFileAsync(item, PartialPath(item), item, current, token).ConfigureAwait(false)) return;
                    item.BytesReceived = item.TotalBytes = new FileInfo(item.OutputPath).Length;
                }
                if (!await MediaProcessing.ConvertDownloadedImageAsync(item, current, token, ConflictResolver).ConfigureAwait(false))
                {
                    item.State = DownloadState.Skipped;
                    return;
                }
                if (File.Exists(item.OutputPath) && !item.OwnedFiles.Contains(item.OutputPath)) item.OwnedFiles.Add(item.OutputPath);
                if (!item.CompletedAt.HasValue) item.CompletedAt = DateTime.Now;
                try { await MediaFileInformation.CollectAsync(item, current, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!token.IsCancellationRequested) throw; }
                DeleteWorkingDirectory(item);
                complete = true;
            }
            catch (OperationCanceledException error)
            {
                if (!token.IsCancellationRequested) { item.State = DownloadState.Error; item.Error = error.Message; }
                else if (item.State == DownloadState.Running || item.State == DownloadState.Seeding) item.State = DownloadState.Paused;
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    item.State = DownloadState.Error;
                    item.Error = error.Message;
                }
            }
            finally
            {
                DownloadItem restart;
                lock (sync)
                {
                    transfers.Remove(item.Id);
                    item.BytesPerSecond = 0;
                    item.UploadBytesPerSecond = 0;
                    item.Phase = "";
                    if (complete && !transfer.Removed) item.State = DownloadState.Complete;
                    restart = disposed || complete ? null : transfer.Restart;
                }
                transfer.Cancellation.Dispose();
                if (transfer.DeleteFiles) DeleteTransferFiles(item, transfer.DeleteCompletedOutput);
                Notify(item);
                if (restart != null) Start(restart);
            }
        }

        private async Task EnsureDaemonAsync(AppSettings current, CancellationToken token)
        {
            await daemonLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (daemon != null && !daemon.HasExited) return;
                if (daemonJob != null) daemonJob.Dispose();
                if (daemon != null) daemon.Dispose();
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                rpcAddress = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/jsonrpc";
                rpcSecret = Guid.NewGuid().ToString("N");
                current = Settings();
                listener = new TcpListener(IPAddress.Any, 0); listener.Start(); torrentTcpPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                using (UdpClient socket = new UdpClient(0)) torrentUdpPort = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
                daemonDht = current.DhtEnabled; daemonLpd = current.LocalPeerDiscovery;
                List<string> args = new List<string> { "--no-conf=true", "--enable-rpc=true", "--rpc-listen-all=false", "--rpc-allow-origin-all=false",
                    "--rpc-listen-port=" + port.ToString(CultureInfo.InvariantCulture), "--rpc-secret=" + rpcSecret, "--max-concurrent-downloads=999",
                    "--continue=true", "--file-allocation=none", "--summary-interval=0", "--console-log-level=error", "--enable-color=false", "--download-result=hide",
                    "--enable-dht=" + Lower(current.DhtEnabled), "--enable-dht6=" + Lower(current.DhtEnabled), "--bt-enable-lpd=" + Lower(current.LocalPeerDiscovery),
                    "--listen-port=" + torrentTcpPort.ToString(CultureInfo.InvariantCulture), "--dht-listen-port=" + torrentUdpPort.ToString(CultureInfo.InvariantCulture),
                    "--dht-file-path=" + Path.Combine(dataDirectory, "dht.dat"), "--dht-file-path6=" + Path.Combine(dataDirectory, "dht6.dat") };
                daemonJob = new ProcessJob();
                daemon = new Process { StartInfo = new ProcessStartInfo(MediaTools.EnginePath("aria2c.exe"), EngineProcess.Arguments(args))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
                daemon.OutputDataReceived += delegate { };
                daemon.ErrorDataReceived += delegate { };
                daemon.Start();
                try { daemonJob.Assign(daemon); }
                catch { daemon.Kill(); throw; }
                daemon.BeginOutputReadLine();
                daemon.BeginErrorReadLine();
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (daemon.HasExited) throw new IOException("aria2 启动失败，退出代码：" + daemon.ExitCode.ToString(CultureInfo.InvariantCulture));
                    try { await RpcAsync("aria2.getVersion", token).ConfigureAwait(false); return; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                throw new IOException("aria2 本地控制端口启动超时。");
            }
            finally { daemonLock.Release(); }
        }

        private async Task<object> RpcAsync(string method, CancellationToken token, params object[] values)
        {
            List<object> parameters = new List<object> { "token:" + rpcSecret };
            parameters.AddRange(values);
            string json = new JavaScriptSerializer().Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = method, @params = parameters });
            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await rpcClient.PostAsync(rpcAddress, content, token).ConfigureAwait(false))
            {
                Dictionary<string, object> result = JsonObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                Dictionary<string, object> error = JsonObject.Object(result, "error");
                if (error != null) throw new AriaException(JsonObject.String(error, "message"));
                response.EnsureSuccessStatusCode();
                return result["result"];
            }
        }
        private sealed class AriaException : IOException { internal AriaException(string message) : base(message) { } }
        private static string Lower(bool value) { return value ? "true" : "false"; }

        private Dictionary<string, object> AriaOptions(DownloadItem item, AppSettings current)
        {
            SiteRule site = MediaTools.Site(item, current);
            int connections = site != null && site.SingleConnection ? 1 : Math.Max(1, Math.Min(16, item.Connections > 0 ? item.Connections : current.Connections));
            Dictionary<string, object> options = new Dictionary<string, object> {
                { "dir", item.WorkingDirectory }, { "split", connections.ToString(CultureInfo.InvariantCulture) }, { "max-connection-per-server", connections.ToString(CultureInfo.InvariantCulture) },
                { "max-download-limit", item.SpeedLimit.ToString(CultureInfo.InvariantCulture) }, { "max-tries", Math.Max(1, current.RetryCount + 1).ToString(CultureInfo.InvariantCulture) },
                { "retry-wait", current.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture) }, { "remote-time", Lower(current.UseServerTimestamp) },
                { "connect-timeout", current.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) }, { "timeout", current.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) },
                { "ftp-pasv", Lower(current.FtpPassive) },
                { "auto-file-renaming", "false" }, { "allow-overwrite", "false" } };
            if (!IsTorrent(item)) options["out"] = Path.GetFileName(PartialPath(item));
            if (item.Referer.Length > 0) options["referer"] = item.Referer;
            if (item.UserAgent.Length > 0) options["user-agent"] = item.UserAgent;
            if (item.Headers.Count > 0)
            {
                List<string> headers = new List<string>();
                foreach (KeyValuePair<string, string> header in item.Headers) headers.Add(header.Key + ": " + header.Value);
                options["header"] = headers;
            }
            string proxy = site != null && site.Proxy.Length > 0 ? site.Proxy : current.Proxy;
            if (proxy.Length > 0) options["all-proxy"] = proxy;
            if (site != null && site.Username.Length > 0)
            {
                options["http-user"] = options["ftp-user"] = site.Username;
                options["http-passwd"] = options["ftp-passwd"] = site.Password;
            }
            if (item.SelectedFiles.Length > 0) options["select-file"] = item.SelectedFiles;
            if (!item.Seed || !current.SeedAfterDownload) options["seed-time"] = "0";
            return options;
        }

        private async Task RunAriaAsync(Transfer transfer, AppSettings current, CancellationToken token, bool automaticName)
        {
            DownloadItem item = transfer.Item;
            bool torrent = IsTorrent(item);
            bool finished = false;
            if (torrent && !current.TorrentEnabled) throw new InvalidOperationException("BitTorrent 下载已禁用。");
            await EnsureDaemonAsync(current, token).ConfigureAwait(false);
            if (torrent)
            {
                lock (sync)
                {
                    torrentUsers.Add(item.Id);
                    if (settings.UpnpEnabled && portMapping == null)
                    {
                        portMapping = new PortMapping(torrentTcpPort, torrentUdpPort, daemonDht, settings.Language);
                        portMapping.Start();
                    }
                }
            }
            Dictionary<string, object> options = AriaOptions(item, current);
            if (!torrent && (item.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
            {
                DownloadMetadata source = await PrepareHttpResumeAsync(item, current, token).ConfigureAwait(false);
                if (item.TotalBytes > 0 && item.BytesReceived == item.TotalBytes && File.Exists(PartialPath(item)) && new FileInfo(PartialPath(item)).Length == item.TotalBytes) return;
                if (source.ETag.Length > 0 && !source.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) options["header"] = new string[] { "If-Match: " + source.ETag };
                else if (source.LastModified.Length > 0) options["header"] = new string[] { "If-Unmodified-Since: " + source.LastModified };
            }
            try
            {
                if (item.EngineId.Length > 0)
                {
                    try
                    {
                        Dictionary<string, object> old = (Dictionary<string, object>)await RpcAsync("aria2.tellStatus", token, item.EngineId).ConfigureAwait(false);
                        string state = JsonObject.String(old, "status");
                        if (state == "error" || state == "removed" || state == "complete") item.EngineId = "";
                        else
                        {
                            await RpcAsync("aria2.changeOption", token, item.EngineId, options).ConfigureAwait(false);
                            if (state == "paused") await RpcAsync("aria2.unpause", token, item.EngineId).ConfigureAwait(false);
                        }
                    }
                    catch (AriaException) { item.EngineId = ""; }
                }
                if (item.EngineId.Length == 0)
                {
                    if (File.Exists(item.Url) && item.Url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                        item.EngineId = (string)await RpcAsync("aria2.addTorrent", token, Convert.ToBase64String(File.ReadAllBytes(item.Url)), new string[0], options).ConfigureAwait(false);
                    else item.EngineId = (string)await RpcAsync("aria2.addUri", token, new string[] { item.Url }, options).ConfigureAwait(false);
                }
                long globalLimit = -1, transferLimit = item.SpeedLimit;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    AppSettings latest = Settings();
                    if (globalLimit != latest.SpeedLimit)
                    {
                        globalLimit = latest.SpeedLimit;
                        await RpcAsync("aria2.changeGlobalOption", token, new Dictionary<string, object> { { "max-overall-download-limit", globalLimit.ToString(CultureInfo.InvariantCulture) } }).ConfigureAwait(false);
                    }
                    if (transferLimit != item.SpeedLimit)
                    {
                        transferLimit = item.SpeedLimit;
                        await RpcAsync("aria2.changeOption", token, item.EngineId, new Dictionary<string, object> { { "max-download-limit", transferLimit.ToString(CultureInfo.InvariantCulture) } }).ConfigureAwait(false);
                    }
                    Dictionary<string, object> status = (Dictionary<string, object>)await RpcAsync("aria2.tellStatus", token, item.EngineId).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    object followed;
                    bool hasSuccessor = false;
                    if (status.TryGetValue("followedBy", out followed) && followed is System.Collections.IEnumerable)
                    {
                        foreach (object next in (System.Collections.IEnumerable)followed)
                        {
                            item.EngineId = Convert.ToString(next, CultureInfo.InvariantCulture);
                            hasSuccessor = true;
                            break;
                        }
                    }
                    if (hasSuccessor) continue;
                    if (latest.ShowDirectUrls && !torrent && JsonObject.String(status, "status") == "active")
                    {
                        try
                        {
                            foreach (Dictionary<string, object> file in (System.Collections.IEnumerable)await RpcAsync("aria2.getServers", token, item.EngineId).ConfigureAwait(false))
                                foreach (Dictionary<string, object> server in JsonObject.Objects(file, "servers"))
                                {
                                    string direct = JsonObject.String(server, "currentUri");
                                    if (direct.Length > 0) { item.DirectUrl = AddressWithoutCredentials(direct); break; }
                                }
                        }
                        catch (AriaException) { }
                    }
                    item.TotalBytes = (long)JsonObject.Number(status, "totalLength");
                    item.BytesReceived = (long)JsonObject.Number(status, "completedLength");
                    item.BytesPerSecond = (long)JsonObject.Number(status, "downloadSpeed");
                    item.Phase = "download";
                    if (torrent)
                    {
                        item.UploadedBytes = (long)JsonObject.Number(status, "uploadLength");
                        item.UploadBytesPerSecond = (long)JsonObject.Number(status, "uploadSpeed");
                    }
                    string state = JsonObject.String(status, "status");
                    List<string> paths = new List<string>();
                    List<TorrentFileProgress> files = new List<TorrentFileProgress>();
                    foreach (Dictionary<string, object> file in JsonObject.Objects(status, "files"))
                    {
                        string path = JsonObject.String(file, "path");
                        bool selected = JsonObject.String(file, "selected") != "false";
                        if (path.Length > 0 && selected) paths.Add(path);
                        if (torrent) files.Add(new TorrentFileProgress {
                            Index = (int)JsonObject.Number(file, "index"), Path = path, Selected = selected,
                            TotalBytes = (long)JsonObject.Number(file, "length"), CompletedBytes = (long)JsonObject.Number(file, "completedLength")
                        });
                    }
                    if (torrent) item.TorrentFiles = files;
                    if (torrent && paths.Count == 1) item.FileName = Path.GetFileName(paths[0]);
                    else if (torrent)
                    {
                        Dictionary<string, object> bt = JsonObject.Object(status, "bittorrent");
                        Dictionary<string, object> info = bt == null ? null : JsonObject.Object(bt, "info");
                        if (info != null && JsonObject.String(info, "name").Length > 0) item.FileName = JsonObject.String(info, "name");
                    }
                    if (state == "error")
                    {
                        string error = JsonObject.String(status, "errorMessage"), partial = PartialPath(item);
                        if (!torrent && item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                            (item.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) &&
                            JsonObject.String(status, "following").Length == 0 && JsonObject.String(status, "belongsTo").Length == 0 &&
                            JsonObject.String(status, "errorCode") == "1" && error.StartsWith("SSL/TLS handshake failure:", StringComparison.Ordinal) && error.Contains("(80090302)") &&
                            item.BytesReceived == 0 && (!File.Exists(partial) || new FileInfo(partial).Length == 0))
                        {
                            await RpcAsync("aria2.removeDownloadResult", token, item.EngineId).ConfigureAwait(false);
                            item.EngineId = ""; item.UseCurl = true;
                            File.Delete(partial + ".aria2");
                            await RunCurlAsync(item, current, token, automaticName).ConfigureAwait(false);
                            return;
                        }
                        throw new IOException(error);
                    }
                    if (state == "removed") throw new IOException("下载任务已从引擎移除。");
                    if (torrent && paths.Count == 1 && Path.GetFileName(paths[0]).StartsWith("[METADATA]", StringComparison.Ordinal))
                    {
                        item.BytesReceived = item.TotalBytes = 0;
                        item.Phase = "metadata";
                        item.TorrentFiles = new List<TorrentFileProgress>();
                        Notify(item);
                        await Task.Delay(100, token).ConfigureAwait(false);
                        continue;
                    }
                    if (torrent && (state == "complete" || (item.TotalBytes > 0 && item.BytesReceived >= item.TotalBytes)))
                    {
                        if (!item.CompletedAt.HasValue && !await CommitOutputsAsync(item, paths, current, token).ConfigureAwait(false))
                        {
                            await RpcAsync(state == "complete" ? "aria2.removeDownloadResult" : "aria2.forceRemove", token, item.EngineId).ConfigureAwait(false);
                            item.EngineId = "";
                            return;
                        }
                        item.State = DownloadState.Seeding;
                        item.Phase = "";
                        if (!item.CompletedAt.HasValue) item.CompletedAt = DateTime.Now;
                    }
                    if (state == "complete") { finished = true; return; }
                    Notify(item);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
            finally
            {
                PortMapping stop = null;
                if (torrent)
                {
                    lock (sync)
                    {
                        torrentUsers.Remove(item.Id);
                        if (torrentUsers.Count == 0 && portMapping != null) { stop = portMapping; portMapping = null; }
                    }
                    if (stop != null) StopMappingAsync(stop).GetAwaiter().GetResult();
                }
                if (!finished && item.EngineId.Length > 0)
                {
                    try { RpcAsync(transfer.Removed ? "aria2.forceRemove" : "aria2.forcePause", CancellationToken.None, item.EngineId).GetAwaiter().GetResult(); }
                    catch (Exception error) { if (!(error is IOException || error is HttpRequestException || error is OperationCanceledException || error is ObjectDisposedException)) throw; }
                    if (transfer.Removed) item.EngineId = "";
                }
            }
        }

        internal static bool IsTorrent(DownloadItem item)
        {
            return item.Kind == "torrent" || item.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || item.Url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        private static string PartialPath(DownloadItem item)
        {
            return item.WorkingDirectory.Length > 0 ? Path.Combine(item.WorkingDirectory, "payload.part") : Path.Combine(item.Directory, ".qdm-" + item.Id + ".part");
        }

        private static void PrepareWorkingDirectory(DownloadItem item, AppSettings current)
        {
            string oldPartial = PartialPath(item);
            if (item.WorkingDirectory.Length == 0)
                item.WorkingDirectory = Path.GetFullPath(Path.Combine(current.TemporaryDirectory.Length > 0 ? current.TemporaryDirectory : item.Directory, ".qdm-" + item.Id));
            string marker = Path.Combine(item.WorkingDirectory, ".owner");
            if (Directory.Exists(item.WorkingDirectory))
            {
                if (!File.Exists(marker) || File.ReadAllText(marker) != item.Id) throw new IOException("临时目录不属于当前任务。");
            }
            else
            {
                Directory.CreateDirectory(item.WorkingDirectory);
                File.WriteAllText(marker, item.Id);
            }
            string partial = PartialPath(item);
            if (oldPartial != partial && File.Exists(oldPartial))
            {
                File.Copy(oldPartial, partial, false); File.Delete(oldPartial);
                if (File.Exists(oldPartial + ".aria2")) { File.Copy(oldPartial + ".aria2", partial + ".aria2", false); File.Delete(oldPartial + ".aria2"); }
            }
        }

        private static void DeleteWorkingDirectory(DownloadItem item)
        {
            if (item.WorkingDirectory.Length == 0 || !Directory.Exists(item.WorkingDirectory)) return;
            string directory = Path.GetFullPath(item.WorkingDirectory);
            string marker = Path.Combine(directory, ".owner");
            if (Path.GetFileName(directory) != ".qdm-" + item.Id || !File.Exists(marker) || File.ReadAllText(marker) != item.Id)
                throw new IOException("拒绝删除不属于当前任务的临时目录。");
            Directory.Delete(directory, true);
        }

        private async Task<bool> CommitFileAsync(DownloadItem item, string source, DownloadItem target, AppSettings current, CancellationToken token)
        {
            Directory.CreateDirectory(target.Directory);
            string commit = Path.Combine(target.Directory, ".qdm-" + item.Id + ".commit");
            try
            {
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true))
                using (FileStream output = new FileStream(commit, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(commit, File.GetLastWriteTimeUtc(source));
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!await ResolveFileConflictAsync(target, current).ConfigureAwait(false)) { item.State = DownloadState.Skipped; return false; }
                    bool retry = false;
                    try
                    {
                        if (File.Exists(target.OutputPath) && EffectiveFileAction(target, current) == ExistingFileAction.Overwrite)
                            File.Replace(commit, target.OutputPath, null);
                        else File.Move(commit, target.OutputPath);
                    }
                    catch (IOException error)
                    {
                        int code = error.HResult & 0xffff;
                        if (File.Exists(commit) && (code == 80 || code == 183 || (code == 2 && !File.Exists(target.OutputPath)))) retry = true;
                        else throw;
                    }
                    if (retry) continue;
                    if (!item.OwnedFiles.Contains(target.OutputPath)) item.OwnedFiles.Add(target.OutputPath);
                    return true;
                }
            }
            finally { if (File.Exists(commit)) File.Delete(commit); }
        }

        private async Task<bool> CommitOutputsAsync(DownloadItem item, List<string> paths, AppSettings current, CancellationToken token)
        {
            string working = Path.GetFullPath(item.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            List<KeyValuePair<string, DownloadItem>> outputs = new List<KeyValuePair<string, DownloadItem>>();
            string mediaName = null, renamedMediaName = null;
            foreach (string path in paths)
            {
                string source = Path.GetFullPath(path);
                if (!source.StartsWith(working, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载引擎返回了临时目录以外的文件：" + source);
                string relative = source.Substring(working.Length);
                DownloadItem target = item.Copy();
                target.Directory = Path.GetDirectoryName(Path.Combine(item.Directory, relative));
                target.FileName = Path.GetFileName(relative);
                if (mediaName != null && target.FileName.StartsWith(mediaName + ".", StringComparison.OrdinalIgnoreCase))
                    target.FileName = renamedMediaName + target.FileName.Substring(mediaName.Length);
                string originalName = target.FileName;
                if (!await ResolveFileConflictAsync(target, current).ConfigureAwait(false)) { item.State = DownloadState.Skipped; return false; }
                if (outputs.Count == 0 && (item.Kind == "media" || item.Kind == "audio"))
                {
                    mediaName = Path.GetFileNameWithoutExtension(originalName);
                    renamedMediaName = Path.GetFileNameWithoutExtension(target.FileName);
                }
                outputs.Add(new KeyValuePair<string, DownloadItem>(source, target));
            }
            for (int index = 0; index < outputs.Count; index++)
            {
                KeyValuePair<string, DownloadItem> output = outputs[index];
                string oldName = Path.GetFileNameWithoutExtension(output.Value.FileName);
                if (!await CommitFileAsync(item, output.Key, output.Value, current, token).ConfigureAwait(false)) return false;
                string newName = Path.GetFileNameWithoutExtension(output.Value.FileName);
                if (index == 0 && mediaName != null && oldName != newName)
                    for (int sidecar = 1; sidecar < outputs.Count; sidecar++)
                        if (outputs[sidecar].Value.FileName.StartsWith(oldName + ".", StringComparison.OrdinalIgnoreCase))
                            outputs[sidecar].Value.FileName = newName + outputs[sidecar].Value.FileName.Substring(oldName.Length);
            }
            if (outputs.Count == 1 || item.Kind == "media" || item.Kind == "audio")
            {
                if (outputs.Count > 0) { item.Directory = outputs[0].Value.Directory; item.FileName = outputs[0].Value.FileName; }
            }
            return true;
        }

        private static void DeleteTransferFiles(DownloadItem item, bool completed)
        {
            string partial = PartialPath(item);
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(partial + ".aria2")) File.Delete(partial + ".aria2");
            DeleteWorkingDirectory(item);
            foreach (string path in item.OwnedFiles) if (File.Exists(path)) File.Delete(path);
            if (completed && item.OwnedFiles.Count == 0 && File.Exists(item.OutputPath)) File.Delete(item.OutputPath);
            item.OwnedFiles.Clear();
        }

        private static void PrepareFileName(DownloadItem item)
        {
            if (item.FileName.Length == 0)
            {
                Uri uri = new Uri(item.Url);
                item.FileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                if (item.FileName.Length == 0) item.FileName = "download";
            }
            item.FileName = Path.GetFileName(item.FileName);
        }

        internal static ExistingFileAction EffectiveFileAction(DownloadItem item, AppSettings current)
        {
            ScheduleSettings schedule = item.Schedule;
            if (schedule == null)
                foreach (QueueSettings queue in current.Queues)
                    if (queue.Id == item.QueueId) { schedule = queue; break; }
            if (schedule != null && schedule.RenameExisting) return ExistingFileAction.Rename;
            return item.ConflictAction ?? current.ExistingFileAction;
        }

        private async Task<bool> ResolveFileConflictAsync(DownloadItem item, AppSettings current)
        {
            if (!File.Exists(item.OutputPath)) return true;
            ExistingFileAction action = EffectiveFileAction(item, current);
            if (action == ExistingFileAction.Ask)
            {
                Func<DownloadItem, Task<ExistingFileAction>> resolver = ConflictResolver;
                if (resolver == null) throw new InvalidOperationException("目标文件已存在，需要选择重命名、覆盖或跳过。");
                action = await resolver(item).ConfigureAwait(false);
                if (action == ExistingFileAction.Ask) throw new OperationCanceledException();
                item.ConflictAction = action;
            }
            if (action == ExistingFileAction.Skip)
            {
                item.State = DownloadState.Skipped;
                return false;
            }
            if (action == ExistingFileAction.Rename)
            {
                string name = Path.GetFileNameWithoutExtension(item.FileName), extension = Path.GetExtension(item.FileName);
                int index = 1;
                while (File.Exists(item.OutputPath)) item.FileName = name + " (" + (index++).ToString(CultureInfo.InvariantCulture) + ")" + extension;
            }
            return true;
        }

        private async Task<string> ServerFileNameAsync(DownloadItem item, AppSettings current, CancellationToken token)
        {
            try
            {
                string name = (await InspectMetadataAsync(item, current, token).ConfigureAwait(false)).FileName;
                if (name.Length > 0) return name;
            }
            catch (IOException) { }
            string headerPath = Path.Combine(dataDirectory, item.Id + ".metadata-headers");
            List<string> args = new List<string> { "--disable", "--silent", "--show-error", "--max-time", "15", "--range", "0-0", "--max-filesize", "1", "--output", "NUL" };
            try
            {
                try { await CurlRequestAsync(args, item, current, headerPath, null, token).ConfigureAwait(false); }
                catch (IOException) { }
                return File.Exists(headerPath) ? DispositionName(File.ReadAllText(headerPath)) : "";
            }
            finally { if (File.Exists(headerPath)) File.Delete(headerPath); }
        }

        public static async Task<DownloadMetadata> InspectMetadataAsync(DownloadItem item, AppSettings current, CancellationToken token)
        {
            DownloadItem request = item.Copy();
            List<string> args = new List<string> { "--disable", "--head", "--fail", "--silent", "--show-error", "--max-time", "15", "--output", "NUL" };
            if (item.Url.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase)) args.Add("--ssl-reqd");
            string headerPath = Path.GetTempFileName(), headers;
            try
            {
                await CurlRequestAsync(args, request, current, headerPath, null, token).ConfigureAwait(false);
                headers = File.ReadAllText(headerPath);
            }
            finally { File.Delete(headerPath); }
            DownloadMetadata metadata = new DownloadMetadata { FileName = DispositionName(headers), SourceUrl = item.Url, DirectUrl = request.DirectUrl };
            using (SHA256 hash = SHA256.Create()) metadata.SourceAddressHash = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(request.DirectUrl)));
            using (StringReader reader = new StringReader(headers))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) { metadata.ContentType = metadata.ETag = metadata.LastModified = ""; metadata.TotalBytes = -1; metadata.AcceptRanges = false; }
                    else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) metadata.ContentType = line.Substring(13).Trim();
                    else if (line.StartsWith("ETag:", StringComparison.OrdinalIgnoreCase)) metadata.ETag = line.Substring(5).Trim();
                    else if (line.StartsWith("Last-Modified:", StringComparison.OrdinalIgnoreCase)) metadata.LastModified = line.Substring(14).Trim();
                    else if (line.StartsWith("Accept-Ranges:", StringComparison.OrdinalIgnoreCase)) metadata.AcceptRanges = line.Substring(14).Trim().Equals("bytes", StringComparison.OrdinalIgnoreCase);
                    else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        long length;
                        if (Int64.TryParse(line.Substring(15).Trim(), out length)) metadata.TotalBytes = length;
                    }
                }
            }
            return metadata;
        }

        private async Task<DownloadMetadata> PrepareHttpResumeAsync(DownloadItem item, AppSettings current, CancellationToken token)
        {
            string partial = PartialPath(item), record = Path.Combine(item.WorkingDirectory, "source.json");
            JavaScriptSerializer json = new JavaScriptSerializer();
            bool resume = File.Exists(partial) && new FileInfo(partial).Length > 0;
            DownloadMetadata previous = resume && File.Exists(record) ? json.Deserialize<DownloadMetadata>(File.ReadAllText(record)) : null;
            DownloadMetadata source;
            try { source = await InspectMetadataAsync(item, current, token).ConfigureAwait(false); }
            catch (IOException)
            {
                if (previous != null && (previous.ETag.Length > 0 || previous.LastModified.Length > 0)) throw;
                source = new DownloadMetadata { SourceUrl = item.Url };
            }
            if (resume)
            {
                bool same = previous != null && source.AcceptRanges && previous.SourceUrl == source.SourceUrl && source.SourceAddressHash.Length > 0 &&
                    previous.SourceAddressHash == source.SourceAddressHash && previous.TotalBytes == source.TotalBytes &&
                    (source.ETag.Length > 0 && !source.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? previous.ETag == source.ETag :
                    source.LastModified.Length > 0 && previous.LastModified == source.LastModified);
                if (!same)
                {
                    if (item.EngineId.Length > 0)
                    {
                        if (daemon != null && !daemon.HasExited)
                        {
                            try { await RpcAsync("aria2.forceRemove", token, item.EngineId).ConfigureAwait(false); }
                            catch (AriaException) { }
                        }
                        item.EngineId = "";
                    }
                    File.Delete(partial);
                    File.Delete(partial + ".aria2");
                    item.BytesReceived = 0;
                }
            }
            File.WriteAllText(record, json.Serialize(source));
            return source;
        }

        private static string DispositionName(string headers)
        {
            string name = "";
            using (StringReader reader = new StringReader(headers))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) name = "";
                    else if (line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
                    {
                        System.Net.Http.Headers.ContentDispositionHeaderValue disposition;
                        if (System.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(line.Substring(20).Trim(), out disposition))
                            name = disposition.FileNameStar ?? disposition.FileName ?? "";
                    }
                }
            }
            name = Path.GetFileName(name.Trim('"'));
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.TrimEnd('.', ' ');
        }

        private static bool HasRequestCredentials(DownloadItem item, AppSettings current)
        {
            SiteRule site = MediaTools.Site(item, current);
            return item.Headers.Count > 0 || item.Referer.Length > 0 || new Uri(item.Url).UserInfo.Length > 0 || site != null && site.Username.Length > 0;
        }

        private static bool SameOrigin(Uri first, Uri second)
        {
            return first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;
        }

        private static string AddressWithoutCredentials(string value)
        {
            Uri address;
            if (!Uri.TryCreate(value, UriKind.Absolute, out address) || address.UserInfo.Length == 0) return value;
            return new UriBuilder(address) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        }

        private static async Task CurlRequestAsync(List<string> options, DownloadItem item, AppSettings current, string headerPath, Action tick, CancellationToken token)
        {
            Uri original = new Uri(item.Url), address = original;
            List<string> requestOptions = new List<string>(options);
            bool redirectedToGet = false;
            for (int redirect = 0; ; redirect++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(headerPath)) File.Delete(headerPath);
                List<string> args = new List<string>(requestOptions);
                args.AddRange(new string[] { "--location", "--max-redirs", "0", "--dump-header", headerPath });
                CurlAuthentication(args, item, current, SameOrigin(original, address), !redirectedToGet);
                args.AddRange(new string[] { "--url", address.AbsoluteUri });
                item.DirectUrl = AddressWithoutCredentials(address.AbsoluteUri);
                try
                {
                    await EngineProcess.RunAsync(MediaTools.EnginePath("curl.exe"), args, null, tick, token, false).ConfigureAwait(false);
                    return;
                }
                catch (IOException)
                {
                    int status = 0;
                    string location = "";
                    if (File.Exists(headerPath))
                        foreach (string line in File.ReadAllLines(headerPath))
                        {
                            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] parts = line.Split(' ');
                                status = parts.Length > 1 ? (int)ParseNumber(parts[1]) : 0;
                                location = "";
                            }
                            else if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase)) location = line.Substring(9).Trim();
                        }
                    if (location.Length == 0 || status != 301 && status != 302 && status != 303 && status != 307 && status != 308) throw;
                    if (redirect >= 49) throw new IOException("重定向次数过多。 / Too many redirects.");
                    int method = requestOptions.IndexOf("--request");
                    if (status <= 303 && method >= 0 && requestOptions[method + 1].Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        requestOptions.RemoveRange(method, 2);
                        int body = requestOptions.IndexOf("--data-binary");
                        if (body >= 0) requestOptions.RemoveRange(body, 2);
                        redirectedToGet = true;
                    }
                    address = new Uri(address, location);
                    if (address.Scheme != "http" && address.Scheme != "https") throw new IOException("重定向目标必须使用 HTTP 或 HTTPS。 / Redirect target must use HTTP or HTTPS.");
                }
            }
        }

        private static void CurlAuthentication(List<string> args, DownloadItem item, AppSettings current, bool includeCredentials, bool includeContentHeaders)
        {
            args.AddRange(new string[] { "--connect-timeout", current.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) });
            if (item.Url.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase))
            {
                if (current.FtpPassive) args.Add("--ftp-pasv");
                else args.AddRange(new string[] { "--ftp-port", "-" });
            }
            SiteRule site = MediaTools.Site(item, current);
            string proxy = site != null && site.Proxy.Length > 0 ? site.Proxy : current.Proxy;
            if (proxy.Length > 0) args.AddRange(new string[] { "--proxy", proxy });
            if (includeCredentials && site != null && site.Username.Length > 0) args.AddRange(new string[] { "--user", site.Username + ":" + site.Password });
            if (includeCredentials && item.Referer.Length > 0) args.AddRange(new string[] { "--referer", item.Referer });
            if (item.UserAgent.Length > 0) args.AddRange(new string[] { "--user-agent", item.UserAgent });
            if (includeCredentials) foreach (KeyValuePair<string, string> header in item.Headers)
            {
                if (!includeContentHeaders && (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Digest", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase))) continue;
                args.AddRange(new string[] { "--header", header.Key + ": " + header.Value });
            }
        }

        private async Task RunCurlAsync(DownloadItem item, AppSettings current, CancellationToken token, bool automaticName)
        {
            string partial = PartialPath(item);
            string headers = Path.Combine(dataDirectory, item.Id + ".headers");
            string body = Path.Combine(dataDirectory, item.Id + ".body");
            List<string> args = new List<string> { "--disable", "--fail", "--silent", "--show-error", "--output", partial };
            if (item.Url.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase)) args.Add("--ssl-reqd");
            if (item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new string[] { "--continue-at", "-", "--retry", current.RetryCount.ToString(CultureInfo.InvariantCulture),
                    "--retry-delay", current.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture) });
                if (item.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || item.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    DownloadMetadata source = await PrepareHttpResumeAsync(item, current, token).ConfigureAwait(false);
                    if (item.TotalBytes > 0 && item.BytesReceived == item.TotalBytes && File.Exists(partial) && new FileInfo(partial).Length == item.TotalBytes) return;
                    if (source.ETag.Length > 0 && !source.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) args.AddRange(new string[] { "--header", "If-Match: " + source.ETag });
                    else if (source.LastModified.Length > 0) args.AddRange(new string[] { "--header", "If-Unmodified-Since: " + source.LastModified });
                }
            }
            else
            {
                args.AddRange(new string[] { "--request", item.Method });
                if (item.Body.Length > 0)
                {
                    File.WriteAllText(body, item.Body, new UTF8Encoding(false));
                    args.AddRange(new string[] { "--data-binary", "@" + body });
                }
            }
            long limit = item.SpeedLimit > 0 ? item.SpeedLimit : current.SpeedLimit;
            if (limit > 0) args.AddRange(new string[] { "--limit-rate", limit.ToString(CultureInfo.InvariantCulture) });
            if (current.UseServerTimestamp) args.Add("--remote-time");
            long previous = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            long resumeStart = item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? previous : 0;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Action progress = delegate
                {
                    long length = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                    item.BytesPerSecond = (long)(Math.Max(0, length - previous) / Math.Max(0.001, watch.Elapsed.TotalSeconds));
                    item.BytesReceived = length;
                    long total = CurlTotal(headers, resumeStart);
                    if (total > 0) item.TotalBytes = total;
                    previous = length;
                    watch.Restart();
                    Notify(item);
                };
                bool restart = false;
                try { await CurlRequestAsync(args, item, current, headers, progress, token).ConfigureAwait(false); }
                catch (EngineProcessException error)
                {
                    if (!item.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || resumeStart == 0 || error.ExitCode != 33) throw;
                    File.Delete(partial); previous = resumeStart = item.BytesReceived = 0; watch.Restart();
                    restart = true;
                }
                if (restart) await CurlRequestAsync(args, item, current, headers, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                item.BytesReceived = item.TotalBytes = new FileInfo(partial).Length;
                if (automaticName && File.Exists(headers))
                {
                    string serverName = DispositionName(File.ReadAllText(headers));
                    if (serverName.Length > 0) item.FileName = serverName;
                }
            }
            finally
            {
                if (File.Exists(headers)) File.Delete(headers);
                if (File.Exists(body)) File.Delete(body);
            }
        }

        private async Task RunMediaAsync(DownloadItem item, AppSettings current, CancellationToken token)
        {
            if (item.MediaExactMatch && item.MediaHeight > 0 && item.Kind != "audio" && !item.MediaOnlyAssets)
            {
                MediaInfo info = await MediaTools.InspectAsync(item, current, token).ConfigureAwait(false);
                bool found = false;
                foreach (MediaFormatInfo format in info.Formats)
                    if (format.Height == item.MediaHeight) { found = true; break; }
                if (!found) { item.State = DownloadState.Skipped; return; }
            }
            bool receivedFile = false;
            string mediaPath = null;
            DownloadItem staged = item.Copy(); staged.Directory = item.WorkingDirectory;
            List<string> arguments = MediaTools.DownloadArguments(staged, current);
            item.AnimationFrame = null;
            try
            {
            await MediaTools.RunAsync(staged, arguments, delegate(string line)
            {
                if (line.StartsWith("QDM_ANIMATION:", StringComparison.Ordinal))
                {
                    if (!current.Media.AnimateDownload || line.Length > 1333350) return;
                    try
                    {
                        byte[] frame = Convert.FromBase64String(line.Substring(14));
                        if (frame.Length < 54 || frame.Length > 1000000 || frame[0] != 'B' || frame[1] != 'M' || BitConverter.ToInt32(frame, 2) != frame.Length) return;
                        using (var stream = new MemoryStream(frame))
                        using (var image = System.Drawing.Image.FromStream(stream))
                            if (image.Width > 320 || image.Height > 180) return;
                        item.AnimationFrame = frame;
                        Notify(item);
                    }
                    catch (FormatException) { }
                    catch (ArgumentException) { }
                    catch (OutOfMemoryException) { }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                }
                else if (line.StartsWith("QDM_INFO:", StringComparison.Ordinal))
                {
                    Dictionary<string, object> info = JsonObject.Parse(line.Substring(9));
                    item.MediaTitle = JsonObject.String(info, "title");
                    item.ThumbnailUrl = JsonObject.String(info, "thumbnail");
                    item.MediaDurationSeconds = JsonObject.Number(info, "duration");
                    item.MediaQuality = JsonObject.String(info, "quality");
                    if (JsonObject.Number(info, "height") > 0) item.MediaQuality = JsonObject.String(info, "height") + "p";
                    if (info.ContainsKey("views") && info["views"] != null) item.MediaViewCount = (long)JsonObject.Number(info, "views");
                    Notify(item);
                }
                else if (line.StartsWith("QDM_MEDIA_PROGRESS:", StringComparison.Ordinal))
                {
                    Dictionary<string, object> progress = JsonObject.Parse(line.Substring(19));
                    long downloaded = ParseNumber(JsonObject.String(progress, "downloaded"));
                    long total = ParseNumber(JsonObject.String(progress, "total"));
                    bool finished = JsonObject.String(progress, "status") == "finished";
                    if (finished && total == 0) total = downloaded;
                    string video = JsonObject.String(progress, "vcodec"), audio = JsonObject.String(progress, "acodec");
                    bool videoTrack = video.Length > 0 && video != "none" && video != "NA";
                    bool audioTrack = video == "none" && audio.Length > 0 && audio != "none" && audio != "NA";
                    if (videoTrack) { item.MediaVideoBytes = downloaded; item.MediaVideoTotal = total; }
                    if (audioTrack) { item.MediaAudioBytes = downloaded; item.MediaAudioTotal = total; }
                    if (videoTrack || audioTrack)
                    {
                        item.BytesReceived = item.MediaVideoBytes + item.MediaAudioBytes;
                        item.TotalBytes = item.MediaVideoTotal + item.MediaAudioTotal;
                    }
                    else { item.BytesReceived = downloaded; item.TotalBytes = total; }
                    item.BytesPerSecond = finished ? 0 : ParseNumber(JsonObject.String(progress, "speed"));
                    item.Phase = "download";
                    Notify(item);
                }
                else if (line.StartsWith("QDM_POSTPROCESS:", StringComparison.Ordinal))
                {
                    Dictionary<string, object> progress = JsonObject.Parse(line.Substring(16));
                    string state = JsonObject.String(progress, "status");
                    if (state == "started" || state == "processing" || state == "finished")
                    {
                        item.Phase = "processing";
                        item.BytesPerSecond = 0;
                        Notify(item);
                    }
                }
                else if (line.StartsWith("QDM_FILE:", StringComparison.Ordinal))
                {
                    string path = Path.GetFullPath(line.Substring(9));
                    mediaPath = path;
                    if (File.Exists(path)) { item.BytesReceived = item.TotalBytes = new FileInfo(path).Length; receivedFile = true; }
                }
            }, token, false).ConfigureAwait(false);
            }
            finally { item.AnimationFrame = null; }
            if (!receivedFile && !item.MediaOnlyAssets) throw new IOException("媒体下载未生成文件；请检查格式和大小、时长筛选条件。");
            List<string> paths = new List<string>();
            if (receivedFile) paths.Add(mediaPath);
            foreach (string path in Directory.GetFiles(item.WorkingDirectory, "*", SearchOption.AllDirectories))
                if (!path.Equals(mediaPath, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) != ".owner" && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
            if (paths.Count == 0) throw new IOException("媒体下载未生成所选字幕或封面文件。");
            if (item.MediaOnlyAssets)
            {
                long length = 0;
                foreach (string path in paths) length += new FileInfo(path).Length;
                item.BytesReceived = item.TotalBytes = length;
            }
            await CommitOutputsAsync(item, paths, current, token).ConfigureAwait(false);
        }

        private static long CurlTotal(string path, long resumeStart)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                long total = 0;
                using (StreamReader reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) total = 0;
                        else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) total = ParseNumber(line.Substring(15).Trim()) + resumeStart;
                        else if (line.StartsWith("Content-Range:", StringComparison.OrdinalIgnoreCase) && line.LastIndexOf('/') >= 0) total = ParseNumber(line.Substring(line.LastIndexOf('/') + 1));
                    }
                }
                return total;
            }
            catch (IOException) { return 0; }
        }

        private static long ParseNumber(string text)
        {
            double value;
            return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 ? (long)value : 0;
        }

        public void Dispose()
        {
            List<Task> tasks = new List<Task>();
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                foreach (Transfer transfer in new List<Transfer>(transfers.Values))
                {
                    transfer.Item.State = DownloadState.Paused;
                    transfer.Cancellation.Cancel();
                    tasks.Add(transfer.Task);
                }
            }
            Task.WaitAll(tasks.ToArray(), 6000);
            PortMapping mapping;
            lock (sync) { mapping = portMapping; portMapping = null; }
            if (mapping != null) StopMappingAsync(mapping).GetAwaiter().GetResult();
            if (mappingCleanup != null) Task.WhenAny(mappingCleanup, Task.Delay(2000)).GetAwaiter().GetResult();
            if (daemonJob != null) daemonJob.Dispose();
            if (daemon != null) daemon.Dispose();
            rpcClient.Dispose();
        }
    }
}
