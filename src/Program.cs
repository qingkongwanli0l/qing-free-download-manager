using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Qing Free Download Manager")]
[assembly: AssemblyProduct("Qing Free Download Manager")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Qing Free Download Manager contributors")]

namespace QingDownloadManager
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
#if NATIVE_HOST
            try { return NativeBridge.RunHost(); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
#else
            if (args.Contains("--register-file-types"))
            {
                try { FileAssociations.Register(); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            if (args.Contains("--unregister-browser") || args.Contains("--unregister-integration"))
            {
                try { IntegrationDialog.UnregisterCurrentInstall(); if (args.Contains("--unregister-integration")) FileAssociations.UnregisterCurrentInstall(); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\" + NativeBridge.PipeName, out created))
            {
                if (!created)
                {
                    try
                    {
                        NativeBridge.Send(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { id = Guid.NewGuid().ToString("N"), action = "show", text = String.Join(Environment.NewLine, args.Where(AddDialog.ValidUrl)) })), false);
                        return 0;
                    }
                    catch (Exception error) { MessageBox.Show(error.Message, "Qing Free Download Manager"); return 1; }
                }
                try
                {
                    string directory = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable"))
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingFDM");
                    int dataIndex = Array.IndexOf(args, "--data-dir");
                    if (dataIndex >= 0 && dataIndex + 1 < args.Length) directory = Path.GetFullPath(args[dataIndex + 1]);
                    Storage storage = new Storage(directory);
                    string languageFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "initial-language.txt");
                    using (MainForm main = new MainForm(storage, storage.Load(File.Exists(languageFile) ? File.ReadAllText(languageFile).Trim() : null), args.Contains("--background")))
                    {
                        string[] urls = args.Where(AddDialog.ValidUrl).ToArray();
                        if (urls.Length > 0) main.Shown += delegate { main.AddText(String.Join(Environment.NewLine, urls)); };
                        Application.Run(main);
                    }
                    return 0;
                }
                catch (Exception error) { MessageBox.Show(error.Message, "Qing Free Download Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
#endif
        }
    }
}
