using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QingDownloadManager
{
    public sealed class MediaStreamInformation
    {
        public string Type { get; set; }
        public string Codec { get; set; }
        public string Language { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public long BitRate { get; set; }
        public MediaStreamInformation Copy() { return (MediaStreamInformation)MemberwiseClone(); }
    }

    public sealed class MediaFileInformation
    {
        public string Path { get; set; }
        public string FileName { get; set; }
        public string Format { get; set; }
        public long SizeBytes { get; set; }
        public double DurationSeconds { get; set; }
        public long BitRate { get; set; }
        public string Error { get; set; }
        public List<MediaStreamInformation> Streams { get; set; }

        public MediaFileInformation()
        {
            Path = FileName = Format = Error = "";
            Streams = new List<MediaStreamInformation>();
        }

        public MediaFileInformation Copy()
        {
            var copy = (MediaFileInformation)MemberwiseClone();
            copy.Streams = Streams.ConvertAll(stream => stream.Copy());
            return copy;
        }

        public static async Task CollectAsync(DownloadItem item, AppSettings settings, CancellationToken token)
        {
            if (!settings.Media.ReadDownloadedInformation || item.MediaOnlyAssets || TransferEngine.IsTorrent(item)) return;
            var results = new List<MediaFileInformation>();
            foreach (string file in item.OwnedFiles)
            {
                string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(new[] { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".flv", ".ts", ".m2ts", ".mpg", ".mpeg", ".wmv", ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".flac", ".wma", ".aiff" }, extension) < 0) continue;
                var info = new MediaFileInformation { Path = System.IO.Path.GetFullPath(file), FileName = System.IO.Path.GetFileName(file) };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        var args = new List<string> { "-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
                            "format=format_name,duration,size,bit_rate:stream=codec_type,codec_name,width,height,avg_frame_rate,sample_rate,channels,bit_rate:stream_tags=language",
                            "-of", "json", "-i", info.Path };
                        ProcessResult result = await EngineProcess.RunAsync(MediaTools.EnginePath("ffprobe.exe"), args, null, null, timeout.Token, true).ConfigureAwait(false);
                        Dictionary<string, object> json = JsonObject.Parse(result.Output), format = JsonObject.Object(json, "format");
                        info.Format = JsonObject.String(format, "format_name");
                        info.DurationSeconds = JsonObject.Number(format, "duration");
                        info.SizeBytes = (long)JsonObject.Number(format, "size");
                        info.BitRate = (long)JsonObject.Number(format, "bit_rate");
                        foreach (Dictionary<string, object> stream in JsonObject.Objects(json, "streams"))
                        {
                            Dictionary<string, object> tags = JsonObject.Object(stream, "tags");
                            string[] rate = JsonObject.String(stream, "avg_frame_rate").Split('/');
                            double numerator, denominator;
                            double fps = rate.Length == 2 && Double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                                Double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) && denominator > 0 ? numerator / denominator : 0;
                            info.Streams.Add(new MediaStreamInformation { Type = JsonObject.String(stream, "codec_type"), Codec = JsonObject.String(stream, "codec_name"),
                                Language = tags == null ? "" : JsonObject.String(tags, "language"), Width = (int)JsonObject.Number(stream, "width"), Height = (int)JsonObject.Number(stream, "height"),
                                FrameRate = fps, SampleRate = (int)JsonObject.Number(stream, "sample_rate"), Channels = (int)JsonObject.Number(stream, "channels"), BitRate = (long)JsonObject.Number(stream, "bit_rate") });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                        info.Error = Localization.Text(settings.Language, "Reading media information timed out.", "读取媒体信息超时。");
                    }
                    catch (Exception error) { info.Error = error.Message; }
                }
                results.Add(info);
            }
            token.ThrowIfCancellationRequested();
            item.MediaFiles = results;
        }

        public static string Describe(List<MediaFileInformation> files, string language)
        {
            var text = new StringBuilder(Localization.Text(language, "Saved file media information", "已保存文件的媒体信息"));
            foreach (MediaFileInformation file in files)
            {
                text.AppendLine().Append(file.FileName);
                if (file.Error.Length > 0) { text.AppendLine().Append(Localization.Text(language, "Could not read: ", "无法读取：")).Append(Localization.Error(language, file.Error)); continue; }
                text.AppendLine().Append(file.Format).Append(" · ").Append(file.SizeBytes.ToString("N0")).Append(" B");
                if (file.DurationSeconds > 0) text.Append(" · ").Append(file.DurationSeconds.ToString("0.###")).Append(Localization.Text(language, " s", " 秒"));
                if (file.BitRate > 0) text.Append(" · ").Append((file.BitRate / 1000d).ToString("0.##")).Append(" kbps");
                foreach (MediaStreamInformation stream in file.Streams)
                {
                    string type = stream.Type == "video" ? Localization.Text(language, "video", "视频") : stream.Type == "audio" ? Localization.Text(language, "audio", "音频") : stream.Type == "subtitle" ? Localization.Text(language, "subtitle", "字幕") : stream.Type;
                    text.AppendLine().Append("  ").Append(type).Append(": ").Append(stream.Codec);
                    if (stream.Width > 0 && stream.Height > 0) text.Append(" · ").Append(stream.Width).Append('×').Append(stream.Height);
                    if (stream.FrameRate > 0) text.Append(" · ").Append(stream.FrameRate.ToString("0.###")).Append(" fps");
                    if (stream.SampleRate > 0) text.Append(" · ").Append(stream.SampleRate).Append(" Hz");
                    if (stream.Channels > 0) text.Append(" · ").Append(stream.Channels).Append(Localization.Text(language, " channels", " 声道"));
                    if (stream.BitRate > 0) text.Append(" · ").Append((stream.BitRate / 1000d).ToString("0.##")).Append(" kbps");
                    if (stream.Language.Length > 0 && stream.Language != "und") text.Append(" · ").Append(stream.Language);
                }
            }
            return text.ToString();
        }
    }
}
