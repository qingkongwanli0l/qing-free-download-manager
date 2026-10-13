using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public static class Localization
    {
        public static readonly string[] Codes = { "en", "zh-CN", "zh-TW" };
        public static readonly string[] Names = { "English", "简体中文", "繁體中文" };
        static readonly Dictionary<string, string> traditional = LoadTraditional();

        static Dictionary<string, string> LoadTraditional()
        {
            using (Stream stream = typeof(Localization).Assembly.GetManifestResourceStream("QingFDM.Languages.zh-TW.json"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }

        public static string Normalize(string language)
        {
            string value = (language ?? "").Replace('_', '-');
            if (value.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || value.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) || value.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
            return value.Equals("zh", StringComparison.OrdinalIgnoreCase) || value.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        }

        public static bool IsSupported(string language)
        {
            return Array.Exists(Codes, code => String.Equals(code, language, StringComparison.OrdinalIgnoreCase));
        }

        public static string Text(string language, string english, string simplified)
        {
            string code = Normalize(language);
            if (code == "zh-CN") return simplified;
            if (code == "zh-TW")
            {
                string translated;
                if (traditional.TryGetValue(english + "\u001f" + simplified, out translated) || traditional.TryGetValue(english, out translated)) return translated;
            }
            return english;
        }

        public static string State(string language, DownloadState state)
        {
            switch (state)
            {
                case DownloadState.Queued: return Text(language, "Queued", "等待");
                case DownloadState.Running: return Text(language, "Running", "下载中");
                case DownloadState.Paused: return Text(language, "Paused", "暂停");
                case DownloadState.Stopped: return Text(language, "Stopped", "停止");
                case DownloadState.Complete: return Text(language, "Complete", "完成");
                case DownloadState.Error: return Text(language, "Error", "错误");
                case DownloadState.Seeding: return Text(language, "Seeding", "做种");
                case DownloadState.History: return Text(language, "History", "历史");
                case DownloadState.Skipped: return Text(language, "Skipped", "跳过");
                default: return state.ToString();
            }
        }

        public static string Error(string language, string message)
        {
            if (Normalize(language) != "zh-TW" || String.IsNullOrEmpty(message)) return message;
            string result = message;
            int matched = 0;
            foreach (var entry in traditional)
            {
                if (!entry.Key.StartsWith("error:", StringComparison.Ordinal)) continue;
                string source = entry.Key.Substring(6);
                if (source.Length > matched && message.StartsWith(source, StringComparison.Ordinal))
                {
                    matched = source.Length;
                    result = entry.Value + message.Substring(source.Length);
                }
            }
            return result;
        }
    }
}
