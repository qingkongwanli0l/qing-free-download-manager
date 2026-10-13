using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public static class Localization
    {
        public static readonly string[] Codes = { "en", "zh-CN", "zh-TW", "de", "fr", "es", "pt-BR" };
        public static readonly string[] Names = { "English", "简体中文", "繁體中文", "Deutsch", "Français", "Español", "Português (Brasil)" };
        static readonly Dictionary<string, Dictionary<string, string>> translations = new Dictionary<string, Dictionary<string, string>>
        {
            { "zh-TW", Load("zh-TW") }, { "de", Load("de") }, { "fr", Load("fr") }, { "es", Load("es") }, { "pt-BR", Load("pt-BR") }
        };

        static Dictionary<string, string> Load(string language)
        {
            using (Stream stream = typeof(Localization).Assembly.GetManifestResourceStream("QingFDM.Languages." + language + ".json"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }

        public static string Normalize(string language)
        {
            string value = (language ?? "").Replace('_', '-');
            if (value.Equals("de", StringComparison.OrdinalIgnoreCase) || value.StartsWith("de-", StringComparison.OrdinalIgnoreCase)) return "de";
            if (value.Equals("fr", StringComparison.OrdinalIgnoreCase) || value.StartsWith("fr-", StringComparison.OrdinalIgnoreCase)) return "fr";
            if (value.Equals("es", StringComparison.OrdinalIgnoreCase) || value.StartsWith("es-", StringComparison.OrdinalIgnoreCase)) return "es";
            if (value.Equals("pt", StringComparison.OrdinalIgnoreCase) || value.StartsWith("pt-", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
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
            Dictionary<string, string> table;
            if (translations.TryGetValue(code, out table))
            {
                string translated;
                if (table.TryGetValue(english + "\u001f" + simplified, out translated) || table.TryGetValue(english, out translated)) return translated;
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
            Dictionary<string, string> table;
            if (String.IsNullOrEmpty(message) || !translations.TryGetValue(Normalize(language), out table)) return message;
            string result = message;
            int matched = 0;
            foreach (var entry in table)
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
