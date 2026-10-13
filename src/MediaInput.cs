using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace QingDownloadManager
{
    internal static class MediaInput
    {
        public static List<string> ExtractUrls(string text)
        {
            var result = new List<string>();
            text = (text ?? "").Trim(); Uri direct;
            if (TryHttpUrl(text, out direct)) { result.Add(text); return result; }
            string rawTag = "";
            foreach (Match tag in Regex.Matches(text, @"<!--[\s\S]*?(?:-->|$)|<(?<close>/)?(?<tag>[A-Za-z][A-Za-z0-9:-]*)\b(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.CultureInvariant))
            {
                string name = tag.Groups["tag"].Value.ToLowerInvariant();
                bool closing = tag.Groups["close"].Success;
                if (rawTag.Length > 0) { if (closing && name == rawTag) rawTag = ""; continue; }
                if (closing || name.Length == 0) continue;
                if (name == "script" || name == "style" || name == "textarea" || name == "title") { rawTag = name; continue; }
                if (name != "iframe" && name != "div") continue;
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match attribute in Regex.Matches(tag.Groups["attrs"].Value, @"(?<name>[^\s=/>]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+)))?", RegexOptions.CultureInvariant))
                    if (!attributes.ContainsKey(attribute.Groups["name"].Value)) attributes.Add(attribute.Groups["name"].Value, WebUtility.HtmlDecode(attribute.Groups["value"].Value));
                string value = "", classes;
                if (name == "iframe") attributes.TryGetValue("src", out value);
                else if (attributes.TryGetValue("class", out classes) && Regex.IsMatch(classes, @"(?:^|\s)fb-(?:video|post)(?:\s|$)", RegexOptions.CultureInvariant)) attributes.TryGetValue("data-href", out value);
                Uri address;
                if (!TryFacebookUrl(value, out address)) continue;
                if (name == "iframe")
                {
                    if (address.AbsolutePath.Equals("/plugins/video.php", StringComparison.OrdinalIgnoreCase))
                    {
                        value = "";
                        foreach (string part in address.Query.TrimStart('?').Split('&'))
                        {
                            int equals = part.IndexOf('=');
                            if (equals > 0 && part.Substring(0, equals).Equals("href", StringComparison.OrdinalIgnoreCase)) { value = Uri.UnescapeDataString(part.Substring(equals + 1).Replace("+", " ")); break; }
                        }
                        if (!TryFacebookUrl(value, out address)) continue;
                    }
                    else if (!address.AbsolutePath.Equals("/video/embed", StringComparison.OrdinalIgnoreCase)) continue;
                }
                value = address.AbsoluteUri;
                if (!result.Contains(value)) result.Add(value);
            }
            return result;
        }

        private static bool TryHttpUrl(string value, out Uri address)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out address) && (address.Scheme == "http" || address.Scheme == "https") && address.Host.Length > 0;
        }

        private static bool TryFacebookUrl(string value, out Uri address)
        {
            value = (value ?? "").Trim(); if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            return TryHttpUrl(value, out address) && address.UserInfo.Length == 0 && (address.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase));
        }
    }
}
