using System;
using System.Drawing;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public static class WindowPlacement
    {
        public static void Attach(Form form, Func<AppSettings> settings, Action save)
        {
            string key = form.GetType().Name;
            Point position;
            if (!settings().CenterPopups && settings().PopupPositions.TryGetValue(key, out position))
            {
                Rectangle area = Screen.GetWorkingArea(position);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(Math.Max(area.Left, Math.Min(position.X, area.Right - form.Width)), Math.Max(area.Top, Math.Min(position.Y, area.Bottom - form.Height)));
            }
            form.FormClosed += delegate
            {
                settings().PopupPositions[key] = (form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds).Location;
                save();
            };
        }
    }
}
