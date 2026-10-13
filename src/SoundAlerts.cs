using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;

namespace QingDownloadManager
{
    public enum DownloadSound { Added, Finished, Error, AllFinished }

    public static class SoundAlerts
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool PlaySound(string sound, IntPtr module, uint flags);

        public static void Play(AppSettings settings, DownloadSound sound)
        {
            if (!settings.SoundsEnabled) return;
            switch (sound)
            {
                case DownloadSound.Added: if (settings.SoundAddedEnabled) Preview(settings.SoundAddedFile, sound); break;
                case DownloadSound.Finished: if (settings.SoundFinishedEnabled) Preview(settings.SoundFinishedFile, sound); break;
                case DownloadSound.Error: if (settings.SoundErrorEnabled) Preview(settings.SoundErrorFile, sound); break;
                case DownloadSound.AllFinished: if (settings.SoundAllFinishedEnabled) Preview(settings.SoundAllFinishedFile, sound); break;
            }
        }

        public static bool Preview(string file, DownloadSound sound)
        {
            if (!String.IsNullOrWhiteSpace(file)) return File.Exists(file) && PlaySound(file, IntPtr.Zero, 0x00020003);
            SystemSound system = sound == DownloadSound.Error ? SystemSounds.Hand : sound == DownloadSound.AllFinished ? SystemSounds.Exclamation : sound == DownloadSound.Finished ? SystemSounds.Asterisk : SystemSounds.Beep;
            system.Play();
            return true;
        }
    }
}
