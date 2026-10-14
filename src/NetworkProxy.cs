using System;
using System.Diagnostics;
using System.Net;

namespace QingDownloadManager
{
    internal sealed class NetworkProxy : IWebProxy, ICredentials
    {
        readonly Func<AppSettings> settings;
        internal NetworkProxy(Func<AppSettings> settings) { this.settings = settings; Credentials = this; }
        public ICredentials Credentials { get; set; }

        public NetworkCredential GetCredential(Uri uri, string authType)
        {
            if (uri.UserInfo.Length == 0) return null;
            string[] parts = uri.UserInfo.Split(new[] { ':' }, 2);
            return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        internal static string Resolve(Uri address, AppSettings settings, SiteRule site)
        {
            if (site != null && site.Proxy.Length > 0) return site.Proxy;
            if (settings.EffectiveProxyMode == "manual") return settings.Proxy;
            if (settings.EffectiveProxyMode != "system" || address.Scheme != "http" && address.Scheme != "https" && address.Scheme != "ftp" && address.Scheme != "ftps") return "";
            Uri proxy = WebRequest.GetSystemWebProxy().GetProxy(address);
            return proxy == null || proxy == address ? "" : proxy.AbsoluteUri;
        }

        public Uri GetProxy(Uri destination)
        {
            AppSettings current = settings();
            string proxy = Resolve(destination, current, MediaTools.Site(new DownloadItem { Url = destination.AbsoluteUri }, current));
            return proxy.Length == 0 ? destination : new Uri(proxy.Contains("://") ? proxy : "http://" + proxy);
        }

        public bool IsBypassed(Uri host) { return GetProxy(host) == host; }

        internal static void ClearEnvironment(ProcessStartInfo start)
        {
            foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "FTP_PROXY", "ALL_PROXY", "NO_PROXY" }) start.EnvironmentVariables.Remove(name);
        }
    }
}
