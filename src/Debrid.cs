using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public sealed class DebridStatus
    {
        public string Username { get; set; }
        public string AccountType { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<DebridHost> Hosts { get; private set; }
        public DebridStatus() { Username = AccountType = ""; Hosts = new List<DebridHost>(); }
    }

    public sealed class DebridHost
    {
        public string Name { get; set; }
        public string Domains { get; set; }
        public string Status { get; set; }
        public string Quota { get; set; }
    }

    public sealed class DebridClient : IDisposable
    {
        readonly HttpClient client;
        public DebridClient(AppSettings settings) : this(new HttpClientHandler { AllowAutoRedirect = false, Proxy = new NetworkProxy(() => settings) }) { }
        internal DebridClient(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QingFDM/0.1");
        }

        public static string Name(string provider)
        {
            switch (provider) {
                case "alldebrid": return "AllDebrid";
                case "real-debrid": return "Real-Debrid";
                case "debrid-link": return "Debrid-Link";
                default: throw new ArgumentException("Unsupported Debrid provider.");
            }
        }

        public static string TokenPage(string provider)
        {
            switch (provider) {
                case "alldebrid": return "https://alldebrid.com/apikeys/";
                case "real-debrid": return "https://real-debrid.com/apitoken";
                case "debrid-link": return "https://debrid-link.com/webapp/apikey";
                default: throw new ArgumentException("Unsupported Debrid provider.");
            }
        }

        static string BaseUrl(string provider)
        {
            switch (provider) {
                case "alldebrid": return "https://api.alldebrid.com/";
                case "real-debrid": return "https://api.real-debrid.com/rest/1.0/";
                case "debrid-link": return "https://debrid-link.com/api/v2/";
                default: throw new ArgumentException("Unsupported Debrid provider.");
            }
        }

        async Task<object> Request(DebridAccount account, string path, Dictionary<string, string> fields, CancellationToken cancel)
        {
            if (!account.Enabled) throw new InvalidOperationException("This Debrid account is disabled.");
            if (String.IsNullOrWhiteSpace(account.ApiToken)) throw new InvalidOperationException("Enter your API token first.");
            using (var request = new HttpRequestMessage(fields == null ? HttpMethod.Get : HttpMethod.Post, BaseUrl(account.Provider) + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.ApiToken.Trim());
                if (fields != null) {
                    if (account.Provider == "debrid-link")
                        request.Content = new StringContent(new JavaScriptSerializer().Serialize(fields), Encoding.UTF8, "application/json");
                    else request.Content = new FormUrlEncodedContent(fields);
                }
                using (HttpResponseMessage response = await client.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    object value;
                    try { value = new JavaScriptSerializer().DeserializeObject(text); }
                    catch (ArgumentException) { throw new InvalidDataException(Name(account.Provider) + ": invalid JSON response (HTTP " + (int)response.StatusCode + ")."); }
                    var envelope = value as Dictionary<string, object>;
                    string error = "";
                    if (envelope != null) {
                        object errorValue;
                        if (envelope.TryGetValue("error", out errorValue)) {
                            var details = errorValue as Dictionary<string, object>;
                            error = details != null ? Text(details, "code") + ": " + Text(details, "message") : Convert.ToString(errorValue, CultureInfo.InvariantCulture);
                        }
                    }
                    if (!response.IsSuccessStatusCode || !String.IsNullOrEmpty(error))
                        throw new InvalidOperationException(Name(account.Provider) + ": " +
                            (String.IsNullOrWhiteSpace(error) ? "HTTP " + (int)response.StatusCode : error.Replace(account.ApiToken.Trim(), "[redacted]")));
                    if (account.Provider == "alldebrid") {
                        if (envelope == null || Text(envelope, "status") != "success") throw new InvalidDataException("AllDebrid: invalid response status.");
                        return Value(envelope, "data");
                    }
                    if (account.Provider == "debrid-link") {
                        if (envelope == null || !Flag(envelope, "success")) throw new InvalidDataException("Debrid-Link: invalid response status.");
                        return Value(envelope, "value");
                    }
                    return value;
                }
            }
        }

        public async Task<DebridStatus> RefreshAsync(DebridAccount account, CancellationToken cancel)
        {
            var status = new DebridStatus();
            if (account.Provider == "alldebrid") {
                var root = Object(await Request(account, "v4/user", null, cancel).ConfigureAwait(false));
                var user = Object(Value(root, "user"));
                status.Username = Text(user, "username");
                status.AccountType = Flag(user, "isTrial") ? "trial" : Flag(user, "isPremium") ? "premium" : "free";
                long expiration = Number(user, "premiumUntil");
                if (expiration > 0) status.ExpiresAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiration).ToLocalTime();
                root = Object(await Request(account, "v4.1/user/hosts", null, cancel).ConfigureAwait(false));
                foreach (var pair in Object(Value(root, "hosts"))) {
                    var host = Object(pair.Value);
                    status.Hosts.Add(new DebridHost {
                        Name = Text(host, "name"), Domains = Strings(Value(host, "domains")),
                        Status = Value(host, "status") == null ? "unknown" : Flag(host, "status") ? "online" : "offline",
                        Quota = Value(host, "quota") == null ? "" : Text(host, "quota") + " " + (Text(host, "quotaType") == "traffic" ? "MB" : "links")
                    });
                }
            }
            else if (account.Provider == "real-debrid") {
                var user = Object(await Request(account, "user", null, cancel).ConfigureAwait(false));
                status.Username = Text(user, "username"); status.AccountType = Text(user, "type");
                DateTime expiration;
                if (DateTime.TryParse(Text(user, "expiration"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiration))
                    status.ExpiresAt = expiration.ToLocalTime();
                var hosts = Object(await Request(account, "hosts/status", null, cancel).ConfigureAwait(false));
                foreach (var pair in hosts) {
                    var host = Object(pair.Value);
                    status.Hosts.Add(new DebridHost {
                        Name = Text(host, "name"), Domains = pair.Key,
                        Status = Number(host, "supported") == 0 ? "unsupported" : Text(host, "status") == "up" ? "online" : Text(host, "status") == "down" ? "offline" : "unknown", Quota = ""
                    });
                }
            }
            else if (account.Provider == "debrid-link") {
                var user = Object(await Request(account, "account/infos", null, cancel).ConfigureAwait(false));
                status.Username = Text(user, "username");
                long type = Number(user, "accountType");
                status.AccountType = type == 2 ? "lifetime" : type == 1 ? "premium" : "free";
                if (type == 1 && Number(user, "premiumLeft") > 0) status.ExpiresAt = DateTime.Now.AddSeconds(Number(user, "premiumLeft"));
                foreach (object entry in Array(await Request(account, "downloader/hosts", null, cancel).ConfigureAwait(false))) {
                    var host = Object(entry);
                    status.Hosts.Add(new DebridHost { Name = Text(host, "name"), Domains = Strings(Value(host, "domains")),
                        Status = Value(host, "status") == null ? "unknown" : Number(host, "status") >= 1 ? "online" : "offline", Quota = "" });
                }
            }
            else throw new ArgumentException("Unsupported Debrid provider.");
            status.Hosts.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return status;
        }

        public async Task<List<DownloadItem>> GenerateAsync(DebridAccount account, string url, string password, string directory, int connections, CancellationToken cancel)
        {
            Uri source;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out source) || (source.Scheme != "http" && source.Scheme != "https"))
                throw new ArgumentException("Enter an HTTP or HTTPS hoster link.");
            var fields = new Dictionary<string, string> {
                { account.Provider == "debrid-link" ? "url" : "link", source.AbsoluteUri.Normalize() }
            };
            if (!String.IsNullOrEmpty(password)) fields["password"] = password.Normalize();
            string endpoint = account.Provider == "alldebrid" ? "v4/link/unlock" : account.Provider == "real-debrid" ? "unrestrict/link" : "downloader/add";
            object response = await Request(account, endpoint, fields, cancel).ConfigureAwait(false);
            if (account.Provider == "alldebrid") {
                var data = Object(response);
                string delayed = Text(data, "delayed");
                if (!String.IsNullOrEmpty(delayed)) {
                    while (true) {
                        await Task.Delay(5000, cancel).ConfigureAwait(false);
                        var progress = Object(await Request(account, "v4/link/delayed", new Dictionary<string, string> { { "id", delayed } }, cancel).ConfigureAwait(false));
                        long state = Number(progress, "status");
                        if (state == 3) throw new InvalidOperationException("AllDebrid could not generate this delayed link.");
                        if (state == 2) { data["link"] = Text(progress, "link"); break; }
                        if (state != 1) throw new InvalidDataException("AllDebrid: invalid delayed-link status.");
                    }
                }
                if (String.IsNullOrEmpty(Text(data, "link")) && Value(data, "streams") != null)
                    throw new InvalidOperationException("AllDebrid returned media variants. Choose a download on the service website.");
            }
            var values = response as Dictionary<string, object>;
            var items = new List<DownloadItem>();
            foreach (object entry in values == null ? Array(response) : new object[] { values }) {
                var data = Object(entry);
                if (account.Provider == "debrid-link" && Flag(data, "isProcessing")) {
                    string id = Text(data, "id");
                    if (String.IsNullOrEmpty(id)) throw new InvalidDataException("Debrid-Link: processing link has no ID.");
                    do {
                        await Task.Delay(5000, cancel).ConfigureAwait(false);
                        var records = Array(await Request(account, "downloader/list?ids=" + Uri.EscapeDataString(id), null, cancel).ConfigureAwait(false));
                        if (records.Count != 1) throw new InvalidDataException("Debrid-Link: processing link was not returned.");
                        data = Object(records[0]);
                    } while (Flag(data, "isProcessing"));
                }
                if (account.Provider == "debrid-link" && Flag(data, "expired")) throw new InvalidOperationException("Debrid-Link returned an expired link. Generate it again.");
                string direct = Text(data, account.Provider == "real-debrid" ? "download" : account.Provider == "debrid-link" ? "downloadUrl" : "link");
                Uri generated;
                if (!Uri.TryCreate(direct, UriKind.Absolute, out generated) || (generated.Scheme != "http" && generated.Scheme != "https"))
                    throw new InvalidDataException(Name(account.Provider) + " did not return a direct HTTP download link.");
                string name = Text(data, account.Provider == "debrid-link" ? "name" : "filename");
                name = name.Replace('\\', '/');
                name = name.Substring(name.LastIndexOf('/') + 1);
                foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
                name = name.Trim().TrimEnd('.');
                if (String.IsNullOrWhiteSpace(name)) name = "download";
                long chunks = Number(data, account.Provider == "debrid-link" ? "chunk" : "chunks");
                items.Add(new DownloadItem { Url = generated.AbsoluteUri, FileName = name, Directory = directory, DirectoryExplicit = true,
                    TotalBytes = Math.Max(0, Number(data, account.Provider == "debrid-link" ? "size" : "filesize")),
                    Connections = chunks > 0 ? (int)Math.Min(Math.Max(1, connections), chunks) : Math.Max(1, connections) });
            }
            if (items.Count == 0) throw new InvalidDataException("The service returned no download links.");
            return items;
        }

        static object Value(Dictionary<string, object> source, string key) { object value; return source.TryGetValue(key, out value) ? value : null; }
        static string Text(Dictionary<string, object> source, string key) { return Convert.ToString(Value(source, key), CultureInfo.InvariantCulture); }
        static long Number(Dictionary<string, object> source, string key) { long value; return Int64.TryParse(Text(source, key), out value) ? value : 0; }
        static bool Flag(Dictionary<string, object> source, string key) { return System.Object.Equals(Value(source, key), true); }
        static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("The service returned an unexpected JSON object.");
            return result;
        }
        static IList Array(object value)
        {
            var result = value as IList;
            if (result == null) throw new InvalidDataException("The service returned an unexpected JSON list.");
            return result;
        }
        static string Strings(object value)
        {
            var items = new List<string>();
            if (value != null) foreach (object item in Array(value)) items.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return String.Join(", ", items);
        }
        public void Dispose() { client.Dispose(); }
    }
}

