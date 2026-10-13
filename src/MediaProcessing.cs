using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QingDownloadManager
{
    public enum MediaOperation { Convert, ExtractAudio, SoftSubtitles, BurnSubtitles, Trim, Crop, Resize, Merge, ShiftAudio }

    public sealed class MediaProcessingOptions
    {
        public MediaOperation Operation { get; set; }
        public List<string> Inputs { get; set; }
        public string OutputPath { get; set; }
        public bool Overwrite { get; set; }
        public bool ReplaceOriginal { get; set; }
        public bool AudioOnly { get; set; }
        public int AudioBitrate { get; set; }
        public string SubtitlePath { get; set; }
        public decimal StartSeconds { get; set; }
        public decimal EndSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool KeepAspect { get; set; }
        public int OffsetMilliseconds { get; set; }

        public MediaProcessingOptions()
        {
            Inputs = new List<string>();
            OutputPath = SubtitlePath = "";
            AudioBitrate = 192;
            KeepAspect = true;
        }
    }

    public static class MediaProcessing
    {
        public static async Task<bool> ConvertDownloadedImageAsync(DownloadItem item, AppSettings settings, CancellationToken token, Func<DownloadItem, Task<ExistingFileAction>> conflictResolver = null)
        {
            if (!settings.Media.ConvertWebPToJpeg && !settings.Media.ConvertAvifToJpeg) return true;
            string input = item.OutputPath;
            if (!File.Exists(input)) return true;
            var header = new byte[64];
            int count;
            using (var file = File.OpenRead(input)) count = file.Read(header, 0, header.Length);
            string signature = Encoding.ASCII.GetString(header, 0, count);
            bool webp = count >= 12 && signature.Substring(0, 4) == "RIFF" && signature.Substring(8, 4) == "WEBP";
            bool avif = count >= 16 && signature.Substring(4, 4) == "ftyp" && (signature.IndexOf("avif", 8, StringComparison.Ordinal) >= 0 || signature.IndexOf("avis", 8, StringComparison.Ordinal) >= 0);
            if (!(webp && settings.Media.ConvertWebPToJpeg || avif && settings.Media.ConvertAvifToJpeg)) return true;

            string output = Path.ChangeExtension(input, ".jpg");
            if (Path.GetFullPath(output).Equals(Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                output = Path.Combine(item.Directory, Path.GetFileNameWithoutExtension(input) + ".converted.jpg");
            ExistingFileAction action = TransferEngine.EffectiveFileAction(item, settings);
            if (File.Exists(output))
            {
                if (action == ExistingFileAction.Ask)
                {
                    if (conflictResolver == null) throw new IOException("The JPEG output already exists. Choose a filename or an overwrite policy.");
                    DownloadItem target = item.Copy(); target.FileName = Path.GetFileName(output);
                    action = await conflictResolver(target).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (action == ExistingFileAction.Skip) return false;
                if (action == ExistingFileAction.Rename)
                {
                    string basis = Path.Combine(item.Directory, Path.GetFileNameWithoutExtension(output));
                    int suffix = 1;
                    while (File.Exists(output)) output = basis + " (" + suffix++ + ").jpg";
                }
                else if (action != ExistingFileAction.Overwrite) throw new IOException("The JPEG output already exists.");
            }
            string temporary = Path.Combine(item.Directory, ".qdm-image-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                await EngineProcess.RunAsync(MediaTools.EnginePath("ffmpeg.exe"), new List<string> { "-hide_banner", "-nostdin", "-n", "-i", input, "-frames:v", "1", "-q:v", "2", temporary }, null, null, token, false).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (File.Exists(output))
                {
                    if (action != ExistingFileAction.Overwrite) throw new IOException("The JPEG output already exists.");
                    File.Replace(temporary, output, null);
                }
                else File.Move(temporary, output);
                item.FileName = Path.GetFileName(output);
                item.BytesReceived = item.TotalBytes = new FileInfo(output).Length;
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static async Task ProcessAsync(MediaProcessingOptions options, Action<long> progressMilliseconds, CancellationToken token)
        {
            if (options.Inputs.Count == 0) throw new ArgumentException("Choose an input file.");
            foreach (string input in options.Inputs)
                if (!File.Exists(input)) throw new FileNotFoundException("Input file was not found.", input);
            if (String.IsNullOrWhiteSpace(options.OutputPath)) throw new ArgumentException("Choose an output file.");
            string output = Path.GetFullPath(options.OutputPath);
            bool replaceSource = options.ReplaceOriginal && options.Operation == MediaOperation.ShiftAudio && options.Inputs.Count == 1;
            foreach (string input in options.Inputs)
                if (Path.GetFullPath(input).Equals(output, StringComparison.OrdinalIgnoreCase) && !replaceSource)
                    throw new ArgumentException("Output must differ from every input file.");
            if (replaceSource && !Path.GetFullPath(options.Inputs[0]).Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The replacement output must be the selected source file.");
            if (File.Exists(output) && !options.Overwrite && !replaceSource) throw new IOException("The output file already exists.");
            if (options.Operation == MediaOperation.Merge && options.Inputs.Count < 2) throw new ArgumentException("Choose at least two files to merge.");
            if ((options.Operation == MediaOperation.SoftSubtitles || options.Operation == MediaOperation.BurnSubtitles) && !File.Exists(options.SubtitlePath))
                throw new FileNotFoundException("Choose a subtitle file.", options.SubtitlePath);
            if (options.Operation == MediaOperation.Trim && (options.StartSeconds < 0 || options.EndSeconds <= options.StartSeconds))
                throw new ArgumentException("The end time must be after the start time.");
            if (options.Operation == MediaOperation.Crop && (options.Width <= 0 || options.Height <= 0 || options.X < 0 || options.Y < 0))
                throw new ArgumentException("Enter a positive crop width and height and nonnegative coordinates.");
            if (options.Operation == MediaOperation.Resize && (options.Width < 0 || options.Height < 0 || options.Width == 0 && options.Height == 0 || !options.KeepAspect && (options.Width == 0 || options.Height == 0)))
                throw new ArgumentException("Enter output dimensions. With aspect ratio enabled, one dimension may be zero.");

            string temporary = Path.Combine(Path.GetDirectoryName(output), ".qdm-media-" + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
            string concat = null;
            var arguments = new List<string> { "-hide_banner", "-nostdin", "-n" };
            try
            {
                if (options.Operation == MediaOperation.Merge)
                {
                    concat = Path.Combine(Path.GetTempPath(), "qdm-merge-" + Guid.NewGuid().ToString("N") + ".txt");
                    var lines = new List<string>();
                    foreach (string input in options.Inputs)
                        lines.Add("file '" + Path.GetFullPath(input).Replace('\\', '/').Replace("'", "'\\''") + "'");
                    File.WriteAllLines(concat, lines, new UTF8Encoding(false));
                    arguments.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", concat });
                }
                else arguments.AddRange(new[] { "-i", options.Inputs[0] });

                switch (options.Operation)
                {
                    case MediaOperation.ExtractAudio:
                        AudioArguments(arguments, options.AudioBitrate);
                        break;
                    case MediaOperation.SoftSubtitles:
                        arguments.AddRange(new[] { "-i", options.SubtitlePath, "-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-map", "1:0", "-c", "copy", "-c:s", SubtitleCodec(output) });
                        break;
                    case MediaOperation.BurnSubtitles:
                        arguments.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-vf", "subtitles=filename=" + FilterPath(options.SubtitlePath), "-c:a", "copy" });
                        break;
                    case MediaOperation.Trim:
                        arguments.AddRange(new[] { "-ss", Number(options.StartSeconds), "-t", Number(options.EndSeconds - options.StartSeconds), "-map", "0:v?", "-map", "0:a?" });
                        break;
                    case MediaOperation.Crop:
                        arguments.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-vf", "crop=" + options.Width + ":" + options.Height + ":" + options.X + ":" + options.Y, "-c:a", "copy" });
                        break;
                    case MediaOperation.Resize:
                        string scale = "scale=" + (options.Width == 0 ? "-2" : Number(options.Width)) + ":" + (options.Height == 0 ? "-2" : Number(options.Height));
                        if (options.KeepAspect && options.Width > 0 && options.Height > 0) scale += ":force_original_aspect_ratio=decrease:force_divisible_by=2";
                        arguments.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-vf", scale, "-c:a", "copy" });
                        break;
                    case MediaOperation.Merge:
                        if (options.AudioOnly) AudioArguments(arguments, options.AudioBitrate);
                        else arguments.AddRange(new[] { "-map", "0:v?", "-map", "0:a?", "-c", "copy" });
                        break;
                    case MediaOperation.ShiftAudio:
                        arguments.AddRange(new[] { "-itsoffset", Number(options.OffsetMilliseconds / 1000M), "-i", options.Inputs[0], "-map", "0:v?", "-map", "1:a", "-map", "0:s?", "-c", "copy", "-avoid_negative_ts", "disabled" });
                        break;
                }
                arguments.AddRange(new[] { "-progress", "pipe:1", "-nostats", temporary });
                await EngineProcess.RunAsync(MediaTools.EnginePath("ffmpeg.exe"), arguments, delegate(string line)
                {
                    long time;
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && Int64.TryParse(line.Substring(12), out time) && progressMilliseconds != null)
                        progressMilliseconds(time / 1000);
                }, null, token, false).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (File.Exists(output))
                {
                    if (!options.Overwrite && !replaceSource) throw new IOException("The output file already exists.");
                    File.Replace(temporary, output, null);
                }
                else File.Move(temporary, output);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (concat != null && File.Exists(concat)) File.Delete(concat);
            }
        }

        private static string Number(decimal value) { return value.ToString(CultureInfo.InvariantCulture); }

        private static void AudioArguments(List<string> arguments, int bitrate)
        {
            arguments.AddRange(new[] { "-map", "0:a:0", "-vn", "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) + "k" });
        }

        private static string SubtitleCodec(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mp4" || extension == ".mov" || extension == ".m4v" ? "mov_text" : extension == ".webm" ? "webvtt" : "srt";
        }

        private static string FilterPath(string path)
        {
            string value = Path.GetFullPath(path).Replace('\\', '/').Replace("'", "\\'").Replace(":", "\\:");
            return value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("[", "\\[").Replace("]", "\\]").Replace(",", "\\,").Replace(";", "\\;");
        }
    }
}
