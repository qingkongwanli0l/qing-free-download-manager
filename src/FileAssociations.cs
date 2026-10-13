using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QingDownloadManager
{
    public static class FileAssociations
    {
        const string ApplicationName = "Qing Free Download Manager";
        const string Capabilities = @"Software\QingFDM\Capabilities";
        const string Classes = @"Software\Classes\";
        static string Executable { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QingFDM.exe"); } }
        static string Command { get { return "\"" + Executable + "\" \"%1\""; } }
        [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int AssocQueryString(uint flags, uint value, string association, string extra, StringBuilder output, ref uint length);

        public static bool IsTorrentDefault()
        {
            uint length = 0;
            AssocQueryString(0, 2, ".torrent", "open", null, ref length);
            if (length == 0) return false;
            var output = new StringBuilder((int)length);
            return AssocQueryString(0, 2, ".torrent", "open", output, ref length) == 0 && String.Equals(output.ToString(), Executable, StringComparison.OrdinalIgnoreCase);
        }

        public static void Register()
        {
            foreach (string type in new[] { "torrent", "magnet" })
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Classes + "QingFDM." + type))
                {
                    key.SetValue("", type == "torrent" ? "BitTorrent file" : "Magnet link");
                    if (type == "magnet") key.SetValue("URL Protocol", "");
                    using (RegistryKey icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + Executable + "\",0");
                    using (RegistryKey command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", Command);
                }
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Classes + @".torrent\OpenWithProgids")) key.SetValue("QingFDM.torrent", new byte[0], RegistryValueKind.None);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Capabilities))
            {
                key.SetValue("ApplicationName", ApplicationName); key.SetValue("ApplicationDescription", "Download files and BitTorrent links with Qing Free Download Manager.");
                using (RegistryKey files = key.CreateSubKey("FileAssociations")) files.SetValue(".torrent", "QingFDM.torrent");
                using (RegistryKey urls = key.CreateSubKey("URLAssociations")) urls.SetValue("magnet", "QingFDM.magnet");
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) key.SetValue(ApplicationName, Capabilities);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public static void UnregisterCurrentInstall()
        {
            foreach (string type in new[] { "torrent", "magnet" })
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Classes + "QingFDM." + type + @"\shell\open\command"))
                    if (key != null && !String.Equals(key.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase)) return;
            foreach (string type in new[] { "torrent", "magnet" }) Registry.CurrentUser.DeleteSubKeyTree(Classes + "QingFDM." + type, false);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Classes + @".torrent\OpenWithProgids", true)) if (key != null) key.DeleteValue("QingFDM.torrent", false);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                if (key != null && String.Equals(key.GetValue(ApplicationName) as string, Capabilities, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ApplicationName, false);
            Registry.CurrentUser.DeleteSubKeyTree(Capabilities, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
