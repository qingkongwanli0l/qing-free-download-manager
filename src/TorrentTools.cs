using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QingDownloadManager
{
    public sealed class TorrentFileInfo
    {
        public int Index { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
    }

    public sealed class TorrentInfo
    {
        public string Name { get; set; }
        public string InfoHash { get; set; }
        public List<TorrentFileInfo> Files { get; set; }
        public TorrentInfo() { Name = InfoHash = ""; Files = new List<TorrentFileInfo>(); }
    }

    public static class TorrentTools
    {
        public static async Task<TorrentInfo> InspectAsync(DownloadItem item, AppSettings settings, CancellationToken token)
        {
            string temporary = null;
            string path = item.Url;
            Uri uri;
            bool absolute = Uri.TryCreate(item.Url, UriKind.Absolute, out uri);
            if (absolute && uri.IsFile) path = uri.LocalPath;
            try
            {
                if (!File.Exists(path))
                {
                    if (!absolute || (uri.Scheme != "magnet" && uri.Scheme != "http" && uri.Scheme != "https")) throw new FileNotFoundException("Choose a torrent file or an HTTP, HTTPS or magnet address.", path);
                    temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QingFDM-metadata-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporary);
                    var args = new List<string> { "--no-conf=true", "--enable-rpc=false", "--enable-color=false", "--summary-interval=0", "--download-result=hide", "--console-log-level=warn", "--dir=" + temporary, "--max-concurrent-downloads=1", "--seed-time=0" };
                    if (uri.Scheme == "magnet")
                    {
                        int tcpPort;
                        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                        int udpPort;
                        using (var socket = new UdpClient(0)) udpPort = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
                        args.AddRange(new[] { "--bt-metadata-only=true", "--bt-save-metadata=true", "--listen-port=" + tcpPort.ToString(CultureInfo.InvariantCulture), "--dht-listen-port=" + udpPort.ToString(CultureInfo.InvariantCulture),
                            "--enable-dht=" + settings.DhtEnabled.ToString().ToLowerInvariant(), "--enable-dht6=" + settings.DhtEnabled.ToString().ToLowerInvariant(), "--bt-enable-lpd=" + settings.LocalPeerDiscovery.ToString().ToLowerInvariant(),
                            "--dht-file-path=" + System.IO.Path.Combine(temporary, "dht.dat"), "--dht-file-path6=" + System.IO.Path.Combine(temporary, "dht6.dat") });
                    }
                    else
                    {
                        args.AddRange(new[] { "--follow-torrent=false", "--out=metadata.torrent", "--auto-file-renaming=false", "--max-tries=" + (settings.RetryCount + 1).ToString(CultureInfo.InvariantCulture), "--retry-wait=" + settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture) });
                        SiteRule site = MediaTools.Site(item, settings);
                        string proxy = site != null && site.Proxy.Length > 0 ? site.Proxy : settings.Proxy;
                        if (proxy.Length > 0) args.Add("--all-proxy=" + proxy);
                        if (site != null && site.Username.Length > 0) args.AddRange(new[] { "--http-user=" + site.Username, "--http-passwd=" + site.Password });
                        if (item.Referer.Length > 0) args.Add("--referer=" + item.Referer);
                        if (item.UserAgent.Length > 0) args.Add("--user-agent=" + item.UserAgent);
                        foreach (var header in item.Headers) args.Add("--header=" + header.Key + ": " + header.Value);
                    }
                    args.Add("--"); args.Add(item.Url);
                    await EngineProcess.RunAsync(MediaTools.EnginePath("aria2c.exe"), args, null, null, token, false).ConfigureAwait(false);
                    string[] torrents = Directory.GetFiles(temporary, "*.torrent");
                    if (torrents.Length != 1) throw new IOException("Torrent metadata was not received.");
                    path = torrents[0];
                }
                var result = await EngineProcess.RunAsync(MediaTools.EnginePath("aria2c.exe"), new List<string> { "--no-conf=true", "--show-files=true", "--", path }, null, null, token, true).ConfigureAwait(false);
                return Parse(result.Output);
            }
            finally
            {
                if (temporary != null)
                {
                    foreach (string file in Directory.GetFiles(temporary)) File.Delete(file);
                    Directory.Delete(temporary);
                }
            }
        }

        private static TorrentInfo Parse(string listing)
        {
            var result = new TorrentInfo();
            TorrentFileInfo current = null;
            foreach (string line in listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Name: ", StringComparison.Ordinal)) result.Name = line.Substring(6);
                if (line.StartsWith("Info Hash: ", StringComparison.Ordinal)) result.InfoHash = line.Substring(11);
                Match file = Regex.Match(line, @"^\s*(\d+)\|(.*)$");
                if (file.Success)
                {
                    current = new TorrentFileInfo { Index = Int32.Parse(file.Groups[1].Value, CultureInfo.InvariantCulture), Path = file.Groups[2].Value, Size = -1 };
                    result.Files.Add(current);
                }
                else if (current != null)
                {
                    Match length = Regex.Match(line, @"^\s*\|.*\(([\d,]+)\)\s*$");
                    if (length.Success) current.Size = Int64.Parse(length.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                }
            }
            if (result.Files.Count == 0) throw new IOException("The torrent file list could not be read.");
            foreach (var file in result.Files) if (file.Size < 0) throw new IOException("The torrent file size could not be read.");
            return result;
        }
    }
}
