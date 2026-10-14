using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;

namespace QingDownloadManager
{
    internal enum DiagnosticEvent { SessionStarted, SessionEnded, DiagnosticsEnabled, DiagnosticsDisabled, DownloadStarted, EngineSelected, DownloadCompleted, DownloadFailed, DownloadStopped, DownloadSkipped, PreDownloadReady }
    internal enum DiagnosticProtocol { None, Http, Https, Ftp, Ftps, Magnet, LocalFile, Other }
    internal enum DiagnosticEngine { None, Aria2, Curl, YtDlp }
    internal enum DiagnosticFailure { None, Timeout, FileAccess, InputOutput, Network, Other }

    internal static class LocalDiagnostics
    {
        private const int FileLimit = 1024 * 1024;
        private static readonly object sync = new object();
        private static readonly Encoding utf8 = new UTF8Encoding(false);
        private static readonly string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        private static string directory;
        private static bool enabled;

        public static void BeginSession(string dataDirectory, bool record)
        {
            lock (sync)
            {
                directory = Path.Combine(dataDirectory, "diagnostics");
                enabled = record;
                Record(DiagnosticEvent.SessionStarted);
            }
        }

        public static void SetEnabled(bool value)
        {
            lock (sync)
            {
                if (enabled == value) return;
                if (!value) Record(DiagnosticEvent.DiagnosticsDisabled);
                enabled = value;
                if (value) Record(DiagnosticEvent.DiagnosticsEnabled);
            }
        }

        public static void EndSession()
        {
            lock (sync)
            {
                Record(DiagnosticEvent.SessionEnded);
                enabled = false;
            }
        }

        public static DiagnosticProtocol Protocol(string url)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address)) return DiagnosticProtocol.Other;
            switch (address.Scheme)
            {
                case "http": return DiagnosticProtocol.Http;
                case "https": return DiagnosticProtocol.Https;
                case "ftp": return DiagnosticProtocol.Ftp;
                case "ftps": return DiagnosticProtocol.Ftps;
                case "magnet": return DiagnosticProtocol.Magnet;
                case "file": return DiagnosticProtocol.LocalFile;
                default: return DiagnosticProtocol.Other;
            }
        }

        public static DiagnosticFailure Failure(Exception error)
        {
            if (error is TimeoutException || error is OperationCanceledException) return DiagnosticFailure.Timeout;
            if (error is UnauthorizedAccessException || error is SecurityException) return DiagnosticFailure.FileAccess;
            if (error is System.Net.Http.HttpRequestException || error is System.Net.WebException) return DiagnosticFailure.Network;
            if (error is IOException) return DiagnosticFailure.InputOutput;
            return DiagnosticFailure.Other;
        }

        public static void Record(DiagnosticEvent kind, DiagnosticProtocol protocol = DiagnosticProtocol.None,
            DiagnosticEngine engine = DiagnosticEngine.None, DiagnosticFailure failure = DiagnosticFailure.None)
        {
            lock (sync)
            {
                if (!enabled || directory == null) return;
                string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" + version + "\t" +
                    (IntPtr.Size * 8).ToString(CultureInfo.InvariantCulture) + "\t" + kind + "\t" + protocol + "\t" + engine + "\t" + failure + "\n";
                try
                {
                    Directory.CreateDirectory(directory);
                    string current = Path.Combine(directory, "current.log"), previous = Path.Combine(directory, "previous.log");
                    if (File.Exists(current) && new FileInfo(current).Length + utf8.GetByteCount(line) > FileLimit)
                    {
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(current, previous);
                    }
                    File.AppendAllText(current, line, utf8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
            }
        }

        public static void Export(string destination)
        {
            lock (sync)
            {
                var text = new StringBuilder("Qing Free Download Manager local diagnostics\nUTC\tVersion\tProcessBits\tEvent\tProtocol\tEngine\tFailure\n");
                foreach (string name in new[] { "previous.log", "current.log" })
                {
                    string file = Path.Combine(directory, name);
                    if (File.Exists(file)) text.Append(File.ReadAllText(file, utf8));
                }
                File.WriteAllText(destination, text.ToString(), utf8);
            }
        }
    }
}
