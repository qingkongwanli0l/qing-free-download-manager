using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public sealed class SavedState
    {
        public AppSettings Settings { get; set; }
        public List<DownloadItem> Downloads { get; set; }
        public SavedState() { Settings = new AppSettings(); Downloads = new List<DownloadItem>(); }
    }

    public sealed class Storage
    {
        public string DirectoryPath { get; private set; }
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        readonly string path;

        public Storage(string directory)
        {
            DirectoryPath = directory;
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "downloads.json");
        }

        static string Protect(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string value)
        {
            if (String.IsNullOrEmpty(value) || !value.StartsWith("dpapi:", StringComparison.Ordinal)) return value ?? "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(6)), null, DataProtectionScope.CurrentUser));
        }

        static void Secrets(SavedState state, Func<string, string> transform)
        {
            state.Settings.Proxy = transform(state.Settings.Proxy);
            foreach (DebridAccount account in state.Settings.DebridAccounts) account.ApiToken = transform(account.ApiToken);
            foreach (SiteRule rule in state.Settings.SiteRules)
            {
                rule.Proxy = transform(rule.Proxy);
                rule.Password = transform(rule.Password);
            }
            foreach (DownloadItem item in state.Downloads)
            {
                item.Body = transform(item.Body);
                foreach (string key in new List<string>(item.Headers.Keys)) item.Headers[key] = transform(item.Headers[key]);
            }
        }

        public SavedState Load(string initialLanguage = null)
        {
            if (!File.Exists(path))
            {
                SavedState initial = new SavedState();
                if (Localization.IsSupported(initialLanguage)) initial.Settings.Language = Localization.Normalize(initialLanguage);
                initial.Settings.Categories = CategorySettings.Defaults(initial.Settings.Language);
                return initial;
            }
            SavedState state = json.Deserialize<SavedState>(File.ReadAllText(path, Encoding.UTF8));
            Secrets(state, Unprotect);
            foreach (DownloadItem item in state.Downloads)
            {
                item.EngineId = "";
                item.BytesPerSecond = 0;
                if (item.State == DownloadState.Running || item.State == DownloadState.Seeding) item.State = DownloadState.Paused;
                if (state.Settings.AutoStartOnLaunch && item.Enabled && item.State != DownloadState.Complete && item.State != DownloadState.History && item.State != DownloadState.Skipped) item.State = DownloadState.Queued;
            }
            return state;
        }

        public void Save(AppSettings settings, List<DownloadItem> downloads)
        {
            SavedState state = new SavedState { Settings = settings.Copy(), Downloads = downloads.ConvertAll(x => x.Copy()) };
            Secrets(state, Protect);
            string temporary = path + ".new";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json.Serialize(state));
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }

        public void Export(string file, AppSettings settings, List<DownloadItem> downloads, bool includeSettings, bool includeCurrent, bool includeHistory)
        {
            SavedState state = new SavedState { Settings = settings.Copy(), Downloads = downloads.Where(x => x.State == DownloadState.History ? includeHistory : includeCurrent).Select(x => x.Copy()).ToList() };
            Secrets(state, value => "");
            foreach (DownloadItem item in state.Downloads)
            {
                item.Headers.Clear(); item.EngineId = ""; item.WorkingDirectory = ""; item.OwnedFiles.Clear(); item.BytesPerSecond = 0; item.ForceStart = false;
                if (item.State != DownloadState.Complete && item.State != DownloadState.History) item.State = DownloadState.Paused;
            }
            if (!includeSettings) state.Settings = null;
            File.WriteAllText(file, json.Serialize(new StateArchive { Version = 1, IncludesSettings = includeSettings, IncludesCurrent = includeCurrent, IncludesHistory = includeHistory, State = state }), new UTF8Encoding(false));
        }

        public StateArchive ReadArchive(string file)
        {
            StateArchive archive = json.Deserialize<StateArchive>(File.ReadAllText(file, Encoding.UTF8));
            if (archive == null || archive.Version != 1 || archive.State == null || archive.State.Downloads == null || archive.IncludesSettings && archive.State.Settings == null)
                throw new InvalidDataException("This is not a supported Qing backup.");
            foreach (DownloadItem item in archive.State.Downloads)
            {
                item.EngineId = ""; item.WorkingDirectory = ""; item.OwnedFiles.Clear(); item.BytesPerSecond = 0; item.ForceStart = false;
                if (item.State != DownloadState.Complete && item.State != DownloadState.History) item.State = DownloadState.Paused;
            }
            return archive;
        }
    }

    public sealed class StateArchive
    {
        public int Version { get; set; }
        public bool IncludesSettings { get; set; }
        public bool IncludesCurrent { get; set; }
        public bool IncludesHistory { get; set; }
        public SavedState State { get; set; }
    }
}
