using System.Drawing;
using System.IO;

namespace QingDownloadManager
{
    internal static class AppIcons
    {
        internal static readonly Icon Normal = Load("QingFDM.Icon");
        internal static readonly Icon Active = Load("QingFDM.ActiveIcon");

        static Icon Load(string name)
        {
            using (Stream stream = typeof(AppIcons).Assembly.GetManifestResourceStream(name))
            using (Icon icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
        }
    }
}
