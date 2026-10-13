using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public sealed class MediaInfo
    {
        public string Title { get; set; }
        public double Duration { get; set; }
        public string Thumbnail { get; set; }
        public long? ViewCount { get; set; }
        public List<MediaFormatInfo> Formats { get; set; }
        public List<string> SubtitleLanguages { get; set; }
        public List<MediaEntryInfo> Entries { get; set; }
        public MediaInfo()
        {
            Title = Thumbnail = "";
            Formats = new List<MediaFormatInfo>();
            SubtitleLanguages = new List<string>();
            Entries = new List<MediaEntryInfo>();
        }
    }

    public sealed class MediaFormatInfo
    {
        public string Id { get; set; }
        public string Extension { get; set; }
        public string Resolution { get; set; }
        public string AudioCodec { get; set; }
        public string VideoCodec { get; set; }
        public long Size { get; set; }
        public int Height { get; set; }
        public double AudioBitrate { get; set; }
        public string Language { get; set; }
    }

    public sealed class MediaEntryInfo
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public int Index { get; set; }
    }

    public static class MediaTools
    {
        public static async Task<MediaInfo> InspectAsync(DownloadItem item, AppSettings settings, CancellationToken token)
        {
            List<string> args = new List<string> { "--ignore-config", "--dump-single-json", "--flat-playlist", "--skip-download", "--no-warnings" };
            AddAuthentication(args, item, settings);
            args.Add("--");
            args.Add(item.Url);
            ProcessResult result = await RunAsync(item, args, null, token, true).ConfigureAwait(false);
            Dictionary<string, object> json = JsonObject.Parse(result.Output);
            MediaInfo info = new MediaInfo { Title = JsonObject.String(json, "title"), Duration = JsonObject.Number(json, "duration"), Thumbnail = JsonObject.String(json, "thumbnail") };
            if (json.ContainsKey("view_count") && json["view_count"] != null) info.ViewCount = (long)JsonObject.Number(json, "view_count");
            foreach (Dictionary<string, object> format in JsonObject.Objects(json, "formats"))
            {
                info.Formats.Add(new MediaFormatInfo { Id = JsonObject.String(format, "format_id"), Extension = JsonObject.String(format, "ext"),
                    Resolution = JsonObject.String(format, "resolution"), AudioCodec = JsonObject.String(format, "acodec"),
                    VideoCodec = JsonObject.String(format, "vcodec"), Size = (long)(JsonObject.Number(format, "filesize") > 0 ? JsonObject.Number(format, "filesize") : JsonObject.Number(format, "filesize_approx")),
                    Height = (int)JsonObject.Number(format, "height"), AudioBitrate = JsonObject.Number(format, "abr"), Language = JsonObject.String(format, "language") });
            }
            foreach (string key in new string[] { "subtitles", "automatic_captions" })
            {
                Dictionary<string, object> languages = JsonObject.Object(json, key);
                if (languages != null) foreach (string language in languages.Keys) if (!info.SubtitleLanguages.Contains(language)) info.SubtitleLanguages.Add(language);
            }
            int index = 0;
            foreach (Dictionary<string, object> entry in JsonObject.Objects(json, "entries"))
            {
                index++;
                string url = JsonObject.String(entry, "url");
                if (url.Length == 0) url = JsonObject.String(entry, "webpage_url");
                int originalIndex = (int)JsonObject.Number(entry, "playlist_index");
                if (url.Length > 0) info.Entries.Add(new MediaEntryInfo { Title = JsonObject.String(entry, "title"), Url = url, Index = originalIndex > 0 ? originalIndex : index });
            }
            return info;
        }

        public static async Task ConvertAsync(string input, string output, bool audioOnly, int audioBitrate, Action<long> progressMilliseconds, CancellationToken token, bool overwrite = false)
        {
            if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) throw new IOException("转换输出不能与源文件相同。");
            if (File.Exists(output) && !overwrite) throw new IOException("转换输出文件已存在。");
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), ".qdm-convert-" + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
            List<string> args = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", input };
            if (audioOnly) args.AddRange(new string[] { "-vn", "-b:a", audioBitrate.ToString(CultureInfo.InvariantCulture) + "k" });
            args.AddRange(new string[] { "-progress", "pipe:1", "-nostats", temporary });
            try
            {
                await EngineProcess.RunAsync(EnginePath("ffmpeg.exe"), args, delegate(string line)
                {
                    long time;
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && Int64.TryParse(line.Substring(12), out time) && progressMilliseconds != null) progressMilliseconds(time / 1000);
                }, null, token, false).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (File.Exists(output))
                {
                    if (!overwrite) throw new IOException("转换输出文件已存在。");
                    File.Replace(temporary, output, null);
                }
                else File.Move(temporary, output);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string EnginePath(string name)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engines", name);
            if (!File.Exists(path)) throw new FileNotFoundException("下载组件未安装：" + name, path);
            return path;
        }

        internal static bool IsYouTubeUrl(string url)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "http" && address.Scheme != "https") return false;
            return address.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || address.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
        }

        internal static SiteRule Site(DownloadItem item, AppSettings settings)
        {
            Uri uri;
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out uri)) return null;
            foreach (SiteRule rule in settings.SiteRules)
            {
                string host = (rule.HostPattern ?? "").Trim().TrimEnd('.');
                if (host.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = host.Substring(1);
                    if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || uri.Host.Equals(host.Substring(2), StringComparison.OrdinalIgnoreCase)) return rule;
                }
                else if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return rule;
            }
            return null;
        }

        internal static void AddAuthentication(List<string> args, DownloadItem item, AppSettings settings)
        {
            args.AddRange(new string[] { "--no-js-runtimes", "--js-runtimes", "quickjs:" + EnginePath("qjs.exe") });
            args.AddRange(new string[] { "--socket-timeout", settings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) });
            SiteRule site = Site(item, settings);
            string proxy = site != null && site.Proxy.Length > 0 ? site.Proxy : settings.Proxy;
            if (proxy.Length > 0) args.AddRange(new string[] { "--proxy", proxy });
            if (site != null && site.Username.Length > 0) args.AddRange(new string[] { "--username", site.Username, "--password", site.Password });
            if (item.UserAgent.Length > 0) args.AddRange(new string[] { "--user-agent", item.UserAgent });
            if (item.Headers.Count > 0 || item.Referer.Length > 0)
            {
                string plugins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
                if (!File.Exists(Path.Combine(plugins, "qing", "yt_dlp_plugins", "postprocessor", "origin_scope.py")))
                    throw new FileNotFoundException("媒体请求认证组件未安装。 / Media request authentication component is missing.");
                args.AddRange(new string[] { "--no-plugin-dirs", "--plugin-dirs", plugins, "--use-postprocessor", "QingOriginScope" });
            }
        }

        internal static Task<ProcessResult> RunAsync(DownloadItem item, List<string> args, Action<string> onLine, CancellationToken token, bool capture)
        {
            Dictionary<string, string> environment = null;
            if (item.Headers.Count > 0 || item.Referer.Length > 0)
            {
                Dictionary<string, string> headers = new Dictionary<string, string>(item.Headers, StringComparer.OrdinalIgnoreCase);
                if (item.Referer.Length > 0) headers["Referer"] = item.Referer;
                environment = new Dictionary<string, string> { { "QDM_REQUEST_SCOPE", new JavaScriptSerializer().Serialize(new { url = item.Url, headers = headers }) } };
            }
            if (args.Contains("QingAnimation"))
            {
                if (environment == null) environment = new Dictionary<string, string>();
                environment["QDM_ANIMATION_FFMPEG"] = EnginePath("ffmpeg.exe");
            }
            return EngineProcess.RunAsync(EnginePath("yt-dlp.exe"), args, onLine, null, token, capture, environment);
        }

        internal static List<string> DownloadArguments(DownloadItem item, AppSettings settings)
        {
            MediaSettings media = settings.Media;
            SiteRule site = Site(item, settings);
            bool youtube = IsYouTubeUrl(item.Url);
            int connections = site != null && site.SingleConnection ? 1 : Math.Max(1, Math.Min(youtube ? 64 : 16,
                item.Connections > 0 ? item.Connections : youtube ? media.YouTubeConnections : settings.Connections));
            List<string> args = new List<string> { "--ignore-config", "--newline", "--progress", "--no-simulate", "--continue", "--no-playlist",
                "--progress-template", "download:QDM_MEDIA_PROGRESS:{\"downloaded\":%(progress.downloaded_bytes|0)j,\"total\":%(progress.total_bytes,progress.total_bytes_estimate|0)j,\"speed\":%(progress.speed|0)j,\"vcodec\":%(info.vcodec|null)j,\"acodec\":%(info.acodec|null)j,\"format_id\":%(info.format_id|null)j,\"status\":%(progress.status|null)j}",
                "--progress-template", "postprocess:QDM_POSTPROCESS:{\"status\":%(progress.status|null)j,\"postprocessor\":%(progress.postprocessor|null)j}",
                "--print", "after_move:QDM_FILE:%(filepath)s", "--ffmpeg-location", Path.GetDirectoryName(EnginePath("ffmpeg.exe")),
                "--print", "before_dl:QDM_INFO:{\"title\":%(title|null)j,\"thumbnail\":%(thumbnail|null)j,\"duration\":%(duration|0)j,\"quality\":%(format_note|null)j,\"height\":%(height|0)j,\"views\":%(view_count|null)j}",
                "--retries", settings.RetryCount.ToString(CultureInfo.InvariantCulture), "--fragment-retries", settings.RetryCount.ToString(CultureInfo.InvariantCulture),
                "--concurrent-fragments", connections.ToString(CultureInfo.InvariantCulture), "--abort-on-unavailable-fragments",
                "--retry-sleep", settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture), "--retry-sleep", "fragment:" + settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture) };
            args.Add(settings.UseServerTimestamp ? "--mtime" : "--no-mtime");
            bool audio = item.Kind == "audio" || item.MediaFormat == "audio";
            string format = item.MediaFormat.Length > 0 && item.MediaFormat != "audio" ? item.MediaFormat : audio ? "bestaudio/best" : media.VideoFormat;
            if (!audio && item.MediaHeight > 0 && item.MediaFormat.Length == 0)
            {
                string filter = "[height" + (item.MediaExactMatch ? "=" : "<=") + item.MediaHeight.ToString(CultureInfo.InvariantCulture) + "]";
                format = "bestvideo" + filter + "+bestaudio/best" + filter;
                if (!item.MediaExactMatch) format += "/bestvideo+bestaudio/best";
            }
            if (audio && !item.MediaOnlyAssets)
            {
                string audioFormat = item.MediaAudioFormat.Length > 0 ? item.MediaAudioFormat : media.AudioFormat;
                args.AddRange(new string[] { "--extract-audio", "--audio-format", audioFormat == "original" ? "best" : audioFormat });
                if (audioFormat != "original") args.AddRange(new string[] { "--audio-quality", (item.MediaAudioBitrate > 0 ? item.MediaAudioBitrate : media.AudioBitrate).ToString(CultureInfo.InvariantCulture) + "K" });
            }
            if (!audio && !item.MediaOnlyAssets && item.MediaContainer.Length > 0)
            {
                args.AddRange(new string[] { "--merge-output-format", item.MediaContainer, "--remux-video", item.MediaContainer });
            }
            if (media.AudioLanguage.Length > 0)
            {
                foreach (char ch in media.AudioLanguage) if (!Char.IsLetterOrDigit(ch) && ch != '-' && ch != '_') throw new ArgumentException("音轨语言应填写语言代码，例如 zh-CN 或 en。");
                format = "(" + (format.Length > 0 ? format : "bestvideo+bestaudio/best") + ")[language^=?" + media.AudioLanguage + "]";
            }
            if (format.Length > 0 && !item.MediaOnlyAssets) args.AddRange(new string[] { "-f", format });
            string template = item.FileName.Length > 0 ? item.FileName.Replace("%", "%%") : media.FilenameTemplate;
            template = item.MediaFileNamePrefix.Replace("%", "%%") + template;
            args.AddRange(new string[] { "--windows-filenames", "-o", Path.Combine(item.Directory, template) });
            bool thumbnail = item.SaveThumbnail ?? media.SaveThumbnail;
            if (item.MediaOnlyAssets)
            {
                if (!item.SaveSubtitles && !thumbnail) throw new ArgumentException("至少选择封面或字幕。 / Choose a thumbnail or subtitles.");
                args.Add("--skip-download");
            }
            if (item.SaveSubtitles || media.EmbedSubtitles && !item.MediaOnlyAssets)
                args.AddRange(new string[] { "--write-subs", "--write-auto-subs", "--sub-langs", item.MediaSubtitleLanguage.Length > 0 ? item.MediaSubtitleLanguage : media.SubtitleLanguage });
            if (media.EmbedSubtitles && !item.MediaOnlyAssets) args.Add("--embed-subs");
            if (thumbnail || media.EmbedThumbnail && !item.MediaOnlyAssets) args.Add("--write-thumbnail");
            if (media.EmbedThumbnail && !item.MediaOnlyAssets) args.Add("--embed-thumbnail");
            if (media.EmbedMetadata && !item.MediaOnlyAssets) args.Add("--embed-metadata");
            List<string> thumbnailConversions = new List<string>();
            if (media.ConvertWebPToJpeg) thumbnailConversions.Add("webp>jpg");
            if (media.ConvertAvifToJpeg) thumbnailConversions.Add("avif>jpg");
            if (thumbnailConversions.Count > 0) args.AddRange(new string[] { "--convert-thumbnails", String.Join("/", thumbnailConversions) });
            if (media.MinimumSizeKb > 0 && !item.MediaOnlyAssets) args.AddRange(new string[] { "--min-filesize", media.MinimumSizeKb.ToString(CultureInfo.InvariantCulture) + "K" });
            if (media.MinimumDurationSeconds > 0) args.AddRange(new string[] { "--match-filter", "duration >= " + media.MinimumDurationSeconds.ToString(CultureInfo.InvariantCulture) });
            long limit = item.SpeedLimit > 0 && settings.SpeedLimit > 0 ? Math.Min(item.SpeedLimit, settings.SpeedLimit) : Math.Max(item.SpeedLimit, settings.SpeedLimit);
            if (limit > 0) args.AddRange(new string[] { "--limit-rate", limit.ToString(CultureInfo.InvariantCulture) });
            AddAuthentication(args, item, settings);
            if (media.AnimateDownload && item.Kind != "audio" && !item.MediaOnlyAssets)
            {
                string plugins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
                if (File.Exists(Path.Combine(plugins, "qing", "yt_dlp_plugins", "postprocessor", "animation_preview.py")))
                {
                    if (!args.Contains("--plugin-dirs")) args.AddRange(new[] { "--no-plugin-dirs", "--plugin-dirs", plugins });
                    args.AddRange(new[] { "--use-postprocessor", "QingAnimation" });
                }
            }
            args.Add("--");
            args.Add(item.Url);
            return args;
        }
    }

    internal static class JsonObject
    {
        internal static Dictionary<string, object> Parse(string json)
        {
            return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
        }
        internal static string String(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        internal static double Number(Dictionary<string, object> data, string key)
        {
            double number;
            return Double.TryParse(String(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : 0;
        }
        internal static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
        internal static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> data, string key)
        {
            object value;
            IEnumerable entries = data.TryGetValue(key, out value) ? value as IEnumerable : null;
            if (entries == null) yield break;
            foreach (object entry in entries)
            {
                Dictionary<string, object> item = entry as Dictionary<string, object>;
                if (item != null) yield return item;
            }
        }
    }

    internal sealed class ProcessResult
    {
        internal string Output;
    }

    internal sealed class EngineProcessException : IOException
    {
        internal int ExitCode { get; private set; }
        internal EngineProcessException(int exitCode, string message) : base(message) { ExitCode = exitCode; }
    }

    internal static class EngineProcess
    {
        internal static string Arguments(IEnumerable<string> arguments)
        {
            StringBuilder result = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append('"');
                int slashes = 0;
                foreach (char ch in argument)
                {
                    if (ch == '\\') { slashes++; continue; }
                    if (ch == '"') result.Append('\\', slashes * 2 + 1);
                    else result.Append('\\', slashes);
                    result.Append(ch);
                    slashes = 0;
                }
                result.Append('\\', slashes * 2).Append('"');
            }
            return result.ToString();
        }

        internal static async Task<ProcessResult> RunAsync(string executable, List<string> arguments, Action<string> onLine, Action tick, CancellationToken token, bool captureOutput, Dictionary<string, string> environment = null)
        {
            StringBuilder output = new StringBuilder();
            StringBuilder errors = new StringBuilder();
            using (Process process = new Process())
            using (ProcessJob job = new ProcessJob())
            {
                process.StartInfo = new ProcessStartInfo(executable, Arguments(arguments)) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                if (environment != null) foreach (KeyValuePair<string, string> value in environment) process.StartInfo.EnvironmentVariables[value.Key] = value.Value;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (captureOutput) output.AppendLine(e.Data);
                    if (onLine != null && !token.IsCancellationRequested) onLine(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("QDM_POSTPROCESS:", StringComparison.Ordinal))
                    {
                        if (onLine != null && !token.IsCancellationRequested) onLine(e.Data);
                        return;
                    }
                    if (errors.Length > 12000) errors.Remove(0, 6000);
                    errors.AppendLine(e.Data);
                };
                token.ThrowIfCancellationRequested();
                process.Start();
                try
                {
                    job.Assign(process);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    while (!process.HasExited)
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        if (tick != null) tick();
                    }
                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new EngineProcessException(process.ExitCode, Path.GetFileName(executable) + " (" + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + errors.ToString().Trim());
                    return new ProcessResult { Output = output.ToString() };
                }
                finally
                {
                    job.Dispose();
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                }
            }
        }
    }

    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr handle;
        internal ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            ExtendedLimits limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }
        internal void Assign(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
