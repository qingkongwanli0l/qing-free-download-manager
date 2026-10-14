using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    internal sealed class CurlTransfer
    {
        private readonly DownloadItem item, request;
        private readonly AppSettings settings;
        private readonly string bridgePath;
        private volatile bool pauseRequested, paused;
        private long speedLimit;
        private int started;

        internal CurlTransfer(DownloadItem item, AppSettings settings, string bridgePath)
        {
            this.item = item; request = item.Copy(); this.settings = settings.Copy();
            this.bridgePath = Path.GetFullPath(bridgePath);
            speedLimit = item.SpeedLimit > 0 && settings.SpeedLimit > 0 ? Math.Min(item.SpeedLimit, settings.SpeedLimit) : Math.Max(item.SpeedLimit, settings.SpeedLimit);
        }

        internal bool IsPaused { get { return paused; } }
        internal void SetPaused(bool value) { pauseRequested = value; }
        internal void SetSpeedLimit(long effectiveBytesPerSecond) { Interlocked.Exchange(ref speedLimit, Math.Max(0, effectiveBytesPerSecond)); }

        internal Task<DownloadMetadata> RunAsync(string partialPath, DownloadMetadata source, Action progress, CancellationToken token)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A curl transfer can only run once.");
            DownloadMetadata expected = source == null ? null : CopyMetadata(source);
            return Task.Factory.StartNew(delegate { return Run(partialPath, expected, progress, token); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private DownloadMetadata Run(string partialPath, DownloadMetadata expected, Action progress, CancellationToken token)
        {
            Uri original = new Uri(request.Url);
            if (original.Scheme != "http" && original.Scheme != "https") throw new NotSupportedException("Controlled curl requires HTTP or HTTPS.");
            Native api = Native.Acquire(bridgePath);
            try
            {
                using (FileStream output = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                {
                    bool retryGet = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
                    long offset = retryGet ? output.Length : 0;
                    output.Position = offset;
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            Uri address = original;
                            string method = request.Method.ToUpperInvariant();
                            bool contentHeaders = true;
                            for (int redirect = 0; ; redirect++)
                            {
                                WaitPaused(progress, token);
                                Response response = RunHop(api, output, address, original, method, contentHeaders, offset, expected, progress, token);
                                if (IsRedirect(response.Status) && response.Location.Length > 0)
                                {
                                    if (redirect >= 49) throw new IOException("重定向次数过多。 / Too many redirects.");
                                    if (response.Status <= 303 && method == "POST") { method = "GET"; contentHeaders = false; offset = 0; }
                                    address = new Uri(address, response.Location);
                                    if (address.Scheme != "http" && address.Scheme != "https") throw new IOException("重定向目标必须使用 HTTP 或 HTTPS。 / Redirect target must use HTTP or HTTPS.");
                                    continue;
                                }
                                if (response.Status < 200 || response.Status >= 300) throw new RequestFailure("HTTP " + response.Status, 0, response.Status);
                                output.Flush();
                                if (response.Metadata.TotalBytes >= 0 && output.Length != response.Metadata.TotalBytes)
                                    throw new RequestFailure("响应长度与文件长度不一致。 / Incomplete response body.", 18, response.Status);
                                item.BytesReceived = output.Length; item.TotalBytes = output.Length; item.BytesPerSecond = 0;
                                response.Metadata.TotalBytes = output.Length;
                                if (progress != null) progress();
                                output.Close();
                                if (settings.UseServerTimestamp)
                                {
                                    DateTimeOffset timestamp;
                                    if (DateTimeOffset.TryParse(response.Metadata.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
                                        File.SetLastWriteTimeUtc(partialPath, timestamp.UtcDateTime);
                                }
                                return response.Metadata;
                            }
                        }
                        catch (RequestFailure error)
                        {
                            if (!retryGet || attempt >= settings.RetryCount || !error.Retryable) throw;
                            output.Flush();
                            if (output.Length > offset)
                            {
                                string record = Path.Combine(request.WorkingDirectory, "source.json");
                                if (!File.Exists(record)) throw;
                                expected = new JavaScriptSerializer().Deserialize<DownloadMetadata>(File.ReadAllText(record));
                                if (!HasValidator(expected)) throw;
                            }
                            offset = output.Length;
                            Stopwatch delay = Stopwatch.StartNew();
                            while (delay.Elapsed.TotalSeconds < settings.RetryDelaySeconds)
                            {
                                WaitPaused(progress, token);
                                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
                            }
                        }
                    }
                }
            }
            finally { paused = false; item.BytesPerSecond = 0; Native.Release(api); }
        }

        private Response RunHop(Native api, FileStream output, Uri address, Uri original, string method, bool contentHeaders, long offset,
            DownloadMetadata expected, Action progress, CancellationToken token)
        {
            IntPtr handle = api.Create();
            if (handle == IntPtr.Zero) throw new IOException("Unable to create curl request.");
            Response response = new Response();
            Exception callbackError = null;
            bool accepted = false;
            int sent = 0;
            byte[] body = method == "GET" ? new byte[0] : Encoding.UTF8.GetBytes(request.Body);
            Native.Io write = null, read = null, header = null;
            Native.Progress report = null;
            string direct = WithoutCredentials(address);
            try
            {
                write = delegate(IntPtr data, UIntPtr size, UIntPtr count, IntPtr user)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        int length = checked((int)(size.ToUInt64() * count.ToUInt64()));
                        if (response.Status >= 200 && response.Status < 300)
                        {
                            if (!accepted) throw new IOException("响应头尚未通过校验。 / Response headers were not validated.");
                            byte[] buffer = new byte[length]; Marshal.Copy(data, buffer, 0, length); output.Write(buffer, 0, length);
                        }
                        return new UIntPtr((uint)length);
                    }
                    catch (Exception error) { callbackError = error; return UIntPtr.Zero; }
                };
                read = delegate(IntPtr data, UIntPtr size, UIntPtr count, IntPtr user)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        int length = (int)Math.Min(size.ToUInt64() * count.ToUInt64(), (ulong)(body.Length - sent));
                        Marshal.Copy(body, sent, data, length); sent += length; return new UIntPtr((uint)length);
                    }
                    catch (Exception error) { callbackError = error; return new UIntPtr(0x10000000U); }
                };
                header = delegate(IntPtr data, UIntPtr size, UIntPtr count, IntPtr user)
                {
                    try
                    {
                        int length = checked((int)(size.ToUInt64() * count.ToUInt64()));
                        byte[] buffer = new byte[length]; Marshal.Copy(data, buffer, 0, length);
                        string line = Encoding.UTF8.GetString(buffer).TrimEnd('\r', '\n');
                        if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = line.Split(' ');
                            response = new Response(); accepted = false;
                            if (parts.Length > 1) Int32.TryParse(parts[1], out response.Status);
                        }
                        else if (line.Length == 0 && response.Status >= 200 && !response.CompleteHeaders)
                        {
                            response.CompleteHeaders = true;
                            response.Location = response.Value("Location");
                            if (response.Status >= 200 && response.Status < 300)
                            {
                                response.Metadata = Metadata(response, direct);
                                if (method == "GET" && offset > 0) ValidateResume(response, expected, offset);
                                token.ThrowIfCancellationRequested();
                                if (method != "GET" || offset == 0) output.SetLength(0);
                                output.Position = method == "GET" ? offset : 0;
                                if (method == "GET") File.WriteAllText(Path.Combine(request.WorkingDirectory, "source.json"), new JavaScriptSerializer().Serialize(response.Metadata));
                                else File.Delete(Path.Combine(request.WorkingDirectory, "source.json"));
                                accepted = true; item.DirectUrl = direct;
                                item.TotalBytes = response.Metadata.TotalBytes;
                            }
                        }
                        else
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0) response.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                        }
                        return new UIntPtr((uint)length);
                    }
                    catch (Exception error) { callbackError = error; return UIntPtr.Zero; }
                };
                report = delegate(IntPtr user, long total, long now, long uploaded, long uploadedNow) { return token.IsCancellationRequested ? 1 : 0; };
                Check(api.Callbacks(handle, write, read, header, report, IntPtr.Zero));
                SetString(api, handle, 10002, SameOrigin(original, address) ? address.AbsoluteUri : direct);
                Check(api.SetLong(handle, 52, 0));
                Check(api.SetLong(handle, 43, 0));
                Check(api.SetLong(handle, 265, 1));
                Check(api.SetLong(handle, 156, checked(settings.ConnectionTimeoutSeconds * 1000)));
                string ca = Environment.GetEnvironmentVariable("CURL_CA_BUNDLE");
                if (String.IsNullOrEmpty(ca)) ca = Path.Combine(Path.GetDirectoryName(bridgePath), "curl-ca-bundle.crt");
                if (!File.Exists(ca)) throw new IOException("找不到 curl CA 证书文件。 / curl CA bundle was not found.");
                SetString(api, handle, 10065, ca);
                SiteRule site = Site(original.Host);
                SetString(api, handle, 10004, NetworkProxy.Resolve(address, settings, site));
                SetString(api, handle, 10177, "");
                if (request.UserAgent.Length > 0) SetString(api, handle, 10018, request.UserAgent);
                if (SameOrigin(original, address))
                {
                    if (site != null && site.Username.Length > 0) SetString(api, handle, 10005, site.Username + ":" + site.Password);
                    if (request.Referer.Length > 0) SetString(api, handle, 10016, request.Referer);
                    foreach (KeyValuePair<string, string> value in request.Headers)
                        if (contentHeaders || !IsContentHeader(value.Key)) AddHeader(api, handle, value.Key + ": " + value.Value);
                }
                if (method == "GET")
                {
                    Check(api.SetLong(handle, 80, 1));
                    if (offset > 0)
                    {
                        AddHeader(api, handle, "Range: bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-");
                        if (expected != null && StrongETag(expected.ETag)) AddHeader(api, handle, "If-Match: " + expected.ETag);
                        else if (expected != null && expected.LastModified.Length > 0) AddHeader(api, handle, "If-Unmodified-Since: " + expected.LastModified);
                    }
                }
                else
                {
                    Check(api.SetLong(handle, 47, 1));
                    if (method != "POST") SetString(api, handle, 10036, method);
                    Check(api.SetOff(handle, 30120, body.Length));
                }
                long appliedSpeed = Interlocked.Read(ref speedLimit);
                Check(api.SetOff(handle, 30146, appliedSpeed));
                Check(api.Start(handle));
                int running = 1, result = -1;
                long previous = output.Position;
                Stopwatch sample = Stopwatch.StartNew();
                while (running != 0)
                {
                    token.ThrowIfCancellationRequested();
                    long desiredSpeed = Interlocked.Read(ref speedLimit);
                    if (desiredSpeed != appliedSpeed) { Check(api.SetOff(handle, 30146, desiredSpeed)); appliedSpeed = desiredSpeed; }
                    bool desiredPause = pauseRequested;
                    if (desiredPause != paused)
                    {
                        Check(api.Pause(handle, desiredPause ? 5 : 0)); paused = desiredPause;
                        item.BytesPerSecond = 0; previous = output.Position; sample.Restart();
                        if (progress != null) progress();
                    }
                    Check(api.Step(handle, 20, out running, out result));
                    if (callbackError != null) throw callbackError;
                    if (sample.ElapsedMilliseconds >= 250 || running == 0)
                    {
                        item.BytesReceived = output.Position;
                        item.BytesPerSecond = paused ? 0 : (long)(Math.Max(0, output.Position - previous) / Math.Max(0.001, sample.Elapsed.TotalSeconds));
                        previous = output.Position; sample.Restart();
                        if (progress != null) progress();
                    }
                    if (paused && token.WaitHandle.WaitOne(20)) token.ThrowIfCancellationRequested();
                    if (running == 0 && result != 0) throw new RequestFailure("curl " + result + ": " + CopyUtf8(api.Error(handle)), result, response.Status);
                }
                token.ThrowIfCancellationRequested();
                if (callbackError != null) throw callbackError;
                return response;
            }
            finally
            {
                api.Destroy(handle);
                GC.KeepAlive(write); GC.KeepAlive(read); GC.KeepAlive(header); GC.KeepAlive(report);
            }
        }

        private void WaitPaused(Action progress, CancellationToken token)
        {
            while (pauseRequested)
            {
                token.ThrowIfCancellationRequested();
                if (!paused) { paused = true; item.BytesPerSecond = 0; if (progress != null) progress(); }
                if (token.WaitHandle.WaitOne(20)) token.ThrowIfCancellationRequested();
            }
            paused = false; token.ThrowIfCancellationRequested();
        }

        private DownloadMetadata Metadata(Response response, string direct)
        {
            DownloadMetadata value = new DownloadMetadata { SourceUrl = request.Url, DirectUrl = direct, ContentType = response.Value("Content-Type"),
                ETag = response.Value("ETag"), LastModified = response.Value("Last-Modified"), AcceptRanges = response.Value("Accept-Ranges").Equals("bytes", StringComparison.OrdinalIgnoreCase) };
            using (SHA256 hash = SHA256.Create()) value.SourceAddressHash = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(direct)));
            long length;
            if (Int64.TryParse(response.Value("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length)) value.TotalBytes = length;
            ContentDispositionHeaderValue disposition;
            if (ContentDispositionHeaderValue.TryParse(response.Value("Content-Disposition"), out disposition))
            {
                value.FileName = Path.GetFileName((disposition.FileNameStar ?? disposition.FileName ?? "").Trim('"'));
                foreach (char ch in Path.GetInvalidFileNameChars()) value.FileName = value.FileName.Replace(ch, '_');
                value.FileName = value.FileName.TrimEnd('.', ' ');
            }
            return value;
        }

        private static void ValidateResume(Response response, DownloadMetadata expected, long offset)
        {
            DownloadMetadata actual = response.Metadata;
            Match range = Regex.Match(response.Value("Content-Range"), @"^bytes (\d+)-(\d+)/(\d+)$", RegexOptions.IgnoreCase);
            long first, last, total;
            if (expected == null || expected.SourceUrl != actual.SourceUrl || expected.SourceAddressHash != actual.SourceAddressHash || !HasValidator(expected) ||
                (StrongETag(expected.ETag) ? expected.ETag != actual.ETag : expected.LastModified != actual.LastModified) || response.Status != 206 || !range.Success ||
                !Int64.TryParse(range.Groups[1].Value, out first) || !Int64.TryParse(range.Groups[2].Value, out last) || !Int64.TryParse(range.Groups[3].Value, out total) ||
                first != offset || last < first || last != total - 1 || total != expected.TotalBytes || (actual.TotalBytes >= 0 && actual.TotalBytes != last - first + 1))
                throw new IOException("下载来源或续传范围已变化，未追加响应数据。 / Download source or resume range changed; no response bytes were appended.");
            actual.TotalBytes = total; actual.AcceptRanges = true;
        }

        private SiteRule Site(string host)
        {
            foreach (SiteRule rule in settings.SiteRules)
            {
                string pattern = (rule.HostPattern ?? "").Trim().TrimEnd('.');
                if (pattern.StartsWith("*.", StringComparison.Ordinal) ? host.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase) || host.Equals(pattern.Substring(2), StringComparison.OrdinalIgnoreCase) : host.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return rule;
            }
            return null;
        }

        private static bool IsRedirect(int status) { return status == 301 || status == 302 || status == 303 || status == 307 || status == 308; }
        private static bool StrongETag(string tag) { return tag.Length > 0 && !tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase); }
        private static bool HasValidator(DownloadMetadata value) { return value != null && (StrongETag(value.ETag) || value.LastModified.Length > 0); }
        private static bool SameOrigin(Uri first, Uri second) { return first.Scheme == second.Scheme && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port; }
        private static string WithoutCredentials(Uri address) { return address.UserInfo.Length == 0 ? address.AbsoluteUri : new UriBuilder(address) { UserName = "", Password = "" }.Uri.AbsoluteUri; }
        private static bool IsContentHeader(string name) { return name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Expect", StringComparison.OrdinalIgnoreCase) || name.Equals("Digest", StringComparison.OrdinalIgnoreCase) || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase); }
        private static DownloadMetadata CopyMetadata(DownloadMetadata value) { return new DownloadMetadata { FileName = value.FileName, ContentType = value.ContentType, TotalBytes = value.TotalBytes, SourceUrl = value.SourceUrl, SourceAddressHash = value.SourceAddressHash, DirectUrl = value.DirectUrl, ETag = value.ETag, LastModified = value.LastModified, AcceptRanges = value.AcceptRanges }; }
        private static void Check(int code) { if (code != 0) throw new IOException("curl native error " + code); }
        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        private static void SetString(Native api, IntPtr handle, int option, string value) { Check(api.SetString(handle, option, value == null ? null : Utf8(value))); }
        private static void AddHeader(Native api, IntPtr handle, string header) { Check(api.AddHeader(handle, Utf8(header))); }
        private static string CopyUtf8(IntPtr pointer) { if (pointer == IntPtr.Zero) return ""; int count = 0; while (Marshal.ReadByte(pointer, count) != 0) count++; byte[] bytes = new byte[count]; Marshal.Copy(pointer, bytes, 0, count); return Encoding.UTF8.GetString(bytes); }

        private sealed class Response
        {
            internal int Status;
            internal bool CompleteHeaders;
            internal string Location = "";
            internal DownloadMetadata Metadata;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal string Value(string name) { string value; return Headers.TryGetValue(name, out value) ? value : ""; }
        }

        private sealed class RequestFailure : IOException
        {
            internal readonly bool Retryable;
            internal RequestFailure(string message, int code, int status) : base(message) { Retryable = code == 18 || code == 28 || code == 52 || code == 55 || code == 56 || code == 16 || code == 92 || status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504; }
        }

        private sealed class Native
        {
            private static readonly object sync = new object();
            private static Native current;
            private static int users;
            private readonly IntPtr module;
            private readonly string path;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate UIntPtr Io(IntPtr data, UIntPtr size, UIntPtr count, IntPtr user);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Progress(IntPtr user, long total, long now, long uploaded, long uploadedNow);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitCall();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CleanupCall();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateCall();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyCall(IntPtr request);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LongCall(IntPtr request, int option, int value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OffCall(IntPtr request, int option, long value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StringCall(IntPtr request, int option, byte[] value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int HeaderCall(IntPtr request, byte[] value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CallbacksCall(IntPtr request, Io write, Io read, Io header, Progress progress, IntPtr user);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StartCall(IntPtr request);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StepCall(IntPtr request, int wait, out int running, out int result);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PauseCall(IntPtr request, int mask);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ErrorCall(IntPtr request);
            internal readonly CreateCall Create;
            internal readonly DestroyCall Destroy;
            internal readonly LongCall SetLong;
            internal readonly OffCall SetOff;
            internal readonly StringCall SetString;
            internal readonly HeaderCall AddHeader;
            internal readonly CallbacksCall Callbacks;
            internal readonly StartCall Start;
            internal readonly StepCall Step;
            internal readonly PauseCall Pause;
            internal readonly ErrorCall Error;
            private readonly CleanupCall cleanup;
            private readonly InitCall init;
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
            [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
            private T Function<T>(string name) { return (T)(object)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, name), typeof(T)); }
            private Native(string path)
            {
                this.path = path; module = LoadLibraryEx(path, IntPtr.Zero, 8);
                if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to load curl bridge.");
                try
                {
                    Create = Function<CreateCall>("qcurl_create"); Destroy = Function<DestroyCall>("qcurl_destroy");
                    SetLong = Function<LongCall>("qcurl_set_long"); SetOff = Function<OffCall>("qcurl_set_off_t"); SetString = Function<StringCall>("qcurl_set_string");
                    AddHeader = Function<HeaderCall>("qcurl_add_header"); Callbacks = Function<CallbacksCall>("qcurl_set_callbacks");
                    Start = Function<StartCall>("qcurl_start"); Step = Function<StepCall>("qcurl_step"); Pause = Function<PauseCall>("qcurl_pause"); Error = Function<ErrorCall>("qcurl_error");
                    cleanup = Function<CleanupCall>("qcurl_global_cleanup"); init = Function<InitCall>("qcurl_global_init");
                }
                catch { FreeLibrary(module); throw; }
            }
            internal static Native Acquire(string path)
            {
                lock (sync)
                {
                    if (current == null) current = new Native(path);
                    else if (!current.path.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Curl bridge path changed while transfers were active.");
                    if (users == 0) Check(current.init());
                    users++; return current;
                }
            }
            internal static void Release(Native api)
            {
                lock (sync) { if (--users == 0) api.cleanup(); }
            }
        }
    }
}
