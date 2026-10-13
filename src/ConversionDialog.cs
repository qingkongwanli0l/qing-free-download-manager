using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class ConversionDialog : Form
    {
        private readonly bool chinese;
        private readonly ComboBox operationBox = new ComboBox();
        private readonly ListBox inputs = new ListBox();
        private readonly TextBox output = new TextBox();
        private readonly TextBox subtitles = new TextBox();
        private readonly NumericUpDown bitrate = Number(32, 512, 192, 0);
        private readonly NumericUpDown start = Number(0, 864000, 0, 3);
        private readonly NumericUpDown end = Number(0, 864000, 10, 3);
        private readonly NumericUpDown width = Number(0, 32768, 640, 0);
        private readonly NumericUpDown height = Number(0, 32768, 360, 0);
        private readonly NumericUpDown cropWidth = Number(1, 32768, 640, 0);
        private readonly NumericUpDown cropHeight = Number(1, 32768, 360, 0);
        private readonly NumericUpDown x = Number(0, 32768, 0, 0);
        private readonly NumericUpDown y = Number(0, 32768, 0, 0);
        private readonly NumericUpDown offset = Number(-86400000, 86400000, 0, 0);
        private readonly CheckBox aspect = new CheckBox();
        private readonly CheckBox audioOnly = new CheckBox();
        private readonly CheckBox replaceOriginal = new CheckBox();
        private readonly CheckBox showLinkNames = new CheckBox { AutoSize = true };
        private readonly Label status = new Label();
        private readonly Label explanation = new Label();
        private readonly Button convert = new Button();
        private readonly Button cancel = new Button();
        private readonly Button browseOutput = new Button();
        private readonly Button up = new Button();
        private readonly Button down = new Button();
        private readonly TableLayoutPanel content = new TableLayoutPanel();
        private readonly List<Control> optionPanels = new List<Control>();
        private TableLayoutPanel subtitlePanel, trimPanel, cropPanel, resizePanel, mergePanel, shiftPanel, audioPanel;
        private CancellationTokenSource operation;
        private bool closeRequested;

        public ConversionDialog(AppSettings settings) : this(settings, new string[0]) { }

        public ConversionDialog(AppSettings settings, IEnumerable<string> initialFiles) : this(settings, initialFiles, new Dictionary<string, string>(), MediaOperation.Convert) { }

        public ConversionDialog(AppSettings settings, IEnumerable<string> initialFiles, Dictionary<string, string> sourceLinks, MediaOperation initialOperation)
        {
            chinese = settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            Text = T("媒体处理", "Media processing");
            Size = new Size(820, 760); MinimumSize = new Size(750, 640);
            Font = new Font("Segoe UI", 9F); AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            bitrate.Value = Math.Max(bitrate.Minimum, Math.Min(bitrate.Maximum, settings.Media.AudioBitrate));
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Dock = DockStyle.Fill; content.ColumnCount = 1; content.RowCount = 6;
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            operationBox.DropDownStyle = ComboBoxStyle.DropDownList; operationBox.Dock = DockStyle.Fill;
            operationBox.Items.AddRange(new[] { T("转换格式", "Convert format"), T("提取音频", "Extract audio"), T("添加可切换字幕", "Add selectable subtitles"), T("烧录字幕到画面", "Burn subtitles into video"), T("截取时间片段", "Trim a time range"), T("裁切画面", "Crop video"), T("调整尺寸", "Resize video"), T("顺序拼接片段", "Merge video clips"), T("调整音轨同步", "Shift audio timing") });
            operationBox.SelectedIndex = (int)initialOperation; content.Controls.Add(operationBox, 0, 0);
            explanation.AutoSize = true; explanation.Margin = new Padding(3, 8, 3, 8); content.Controls.Add(explanation, 0, 1);
            inputs.Dock = DockStyle.Fill; inputs.IntegralHeight = false; inputs.HorizontalScrollbar = true;
            inputs.FormattingEnabled = true;
            inputs.Format += delegate(object sender, ListControlConvertEventArgs args)
            {
                if (!showLinkNames.Checked || (MediaOperation)operationBox.SelectedIndex != MediaOperation.Merge) return;
                string file = Convert.ToString(args.ListItem), link; Uri uri;
                if (sourceLinks.TryGetValue(file, out link) && Uri.TryCreate(link, UriKind.Absolute, out uri))
                {
                    string name = Path.GetFileName(uri.LocalPath);
                    if (name.Length > 0) args.Value = name + "  (" + Path.GetFileName(file) + ")";
                }
            };
            content.Controls.Add(inputs, 0, 2);
            foreach (string file in initialFiles) inputs.Items.Add(file);
            if (inputs.Items.Count > 0) inputs.SelectedIndex = 0;

            var inputButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 5, 0, 5) };
            var add = new Button { Text = T("添加文件…", "Add files…"), AutoSize = true };
            var remove = new Button { Text = T("移除", "Remove"), AutoSize = true };
            up.Text = T("上移", "Move up"); up.AutoSize = true; down.Text = T("下移", "Move down"); down.AutoSize = true;
            add.Click += delegate
            {
                using (var dialog = new OpenFileDialog { Filter = T("媒体文件|*.*", "Media files|*.*"), Multiselect = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        foreach (string file in dialog.FileNames) if (!inputs.Items.Contains(file)) inputs.Items.Add(file);
                        if (inputs.SelectedIndex < 0) inputs.SelectedIndex = 0;
                        SuggestOutput();
                    }
            };
            remove.Click += delegate
            {
                int index = inputs.SelectedIndex; if (index < 0) return;
                inputs.Items.RemoveAt(index);
                if (inputs.Items.Count > 0) inputs.SelectedIndex = Math.Min(index, inputs.Items.Count - 1);
                SuggestOutput();
            };
            up.Click += delegate { MoveInput(-1); }; down.Click += delegate { MoveInput(1); };
            inputButtons.Controls.AddRange(new Control[] { add, remove, up, down }); content.Controls.Add(inputButtons, 0, 3);

            var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outputRow.Controls.Add(new Label { Text = T("输出文件", "Output file"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 10, 3) }, 0, 0);
            output.Dock = DockStyle.Fill; outputRow.Controls.Add(output, 1, 0);
            browseOutput.Text = T("浏览…", "Browse…"); browseOutput.AutoSize = true;
            browseOutput.Click += delegate
            {
                using (var dialog = new SaveFileDialog { Filter = "MP4|*.mp4|Matroska|*.mkv|WebM|*.webm|MP3|*.mp3|M4A|*.m4a|FLAC|*.flac|WAV|*.wav|JPEG|*.jpg|PNG|*.png", FileName = output.Text, OverwritePrompt = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.FileName;
            };
            outputRow.Controls.Add(browseOutput, 2, 0); content.Controls.Add(outputRow, 0, 4);

            var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
            subtitlePanel = OptionPanel(options);
            var subtitlePath = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
            subtitlePath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); subtitlePath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            subtitles.Dock = DockStyle.Fill;
            var browseSubtitle = new Button { Text = T("浏览…", "Browse…"), AutoSize = true };
            browseSubtitle.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt|All files|*.*" }) if (dialog.ShowDialog(this) == DialogResult.OK) subtitles.Text = dialog.FileName; };
            subtitlePath.Controls.Add(subtitles, 0, 0); subtitlePath.Controls.Add(browseSubtitle, 1, 0);
            Row(subtitlePanel, T("字幕文件", "Subtitle file"), subtitlePath);
            trimPanel = OptionPanel(options);
            Row(trimPanel, T("开始（秒）", "Start (seconds)"), start); Row(trimPanel, T("结束（秒）", "End (seconds)"), end);
            cropPanel = OptionPanel(options);
            Row(cropPanel, T("左侧坐标 X", "Left coordinate X"), x); Row(cropPanel, T("顶部坐标 Y", "Top coordinate Y"), y);
            Row(cropPanel, T("裁切宽度", "Crop width"), cropWidth); Row(cropPanel, T("裁切高度", "Crop height"), cropHeight);
            resizePanel = OptionPanel(options);
            var preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            preset.Items.AddRange(new[] { T("自定义", "Custom"), "3840 × 2160", "1920 × 1080", "1280 × 720", "640 × 360" }); preset.SelectedIndex = 0;
            preset.SelectedIndexChanged += delegate
            {
                int[] widths = { 0, 3840, 1920, 1280, 640 }; int[] heights = { 0, 2160, 1080, 720, 360 };
                if (preset.SelectedIndex > 0) { width.Value = widths[preset.SelectedIndex]; height.Value = heights[preset.SelectedIndex]; }
            };
            Row(resizePanel, T("常用尺寸", "Common sizes"), preset);
            Row(resizePanel, T("宽度（像素）", "Width (pixels)"), width); Row(resizePanel, T("高度（像素）", "Height (pixels)"), height);
            aspect.Text = T("保持比例；单个尺寸为 0 时自动计算", "Keep aspect ratio; zero in one dimension means automatic"); aspect.Checked = true; aspect.AutoSize = true;
            Row(resizePanel, "", aspect);
            mergePanel = OptionPanel(options); audioOnly.Text = T("只输出音频", "Output audio only"); audioOnly.AutoSize = true; Row(mergePanel, "", audioOnly);
            showLinkNames.Text = T("显示视频链接中的片段名", "Show clip names from video URLs"); showLinkNames.Enabled = sourceLinks.Count > 0;
            showLinkNames.CheckedChanged += delegate { inputs.Refresh(); }; Row(mergePanel, "", showLinkNames);
            shiftPanel = OptionPanel(options); Row(shiftPanel, T("音轨偏移（毫秒）", "Audio offset (milliseconds)"), offset);
            Row(shiftPanel, "", new Label { Text = T("正值延后音频，负值提前音频。", "Positive values delay audio; negative values advance it."), AutoSize = true });
            replaceOriginal.Text = T("成功后替换原文件", "Replace the source after successful processing"); replaceOriginal.AutoSize = true; Row(shiftPanel, "", replaceOriginal);
            audioPanel = OptionPanel(options); Row(audioPanel, T("音频码率（kbps）", "Audio bitrate (kbps)"), bitrate);
            content.Controls.Add(options, 0, 5); root.Controls.Add(content, 0, 0);
            status.AutoSize = true; status.MaximumSize = new Size(780, 75); status.AutoEllipsis = true; status.Margin = new Padding(3, 8, 3, 8); root.Controls.Add(status, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            convert.Text = T("开始处理", "Process"); convert.AutoSize = true; convert.Click += Process;
            cancel.Text = T("关闭", "Close"); cancel.AutoSize = true; cancel.Click += delegate { if (operation != null) operation.Cancel(); else Close(); };
            buttons.Controls.Add(cancel); buttons.Controls.Add(convert); root.Controls.Add(buttons, 0, 2); Controls.Add(root);
            AcceptButton = convert; CancelButton = cancel;
            operationBox.SelectedIndexChanged += delegate { UpdateOptions(); SuggestOutput(); };
            inputs.SelectedIndexChanged += delegate { if ((MediaOperation)operationBox.SelectedIndex != MediaOperation.Merge) SuggestOutput(); };
            audioOnly.CheckedChanged += delegate { UpdateOptions(); SuggestOutput(); }; replaceOriginal.CheckedChanged += delegate { SuggestOutput(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (operation != null) { closeRequested = true; args.Cancel = true; operation.Cancel(); } };
            UpdateOptions(); SuggestOutput();
            Appearance.Apply(this);
        }

        private string T(string zh, string en) { return chinese ? zh : en; }
        private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimals)
        {
            return new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimals, ThousandsSeparator = true };
        }
        private TableLayoutPanel OptionPanel(TableLayoutPanel parent)
        {
            var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parent.Controls.Add(panel, 0, parent.RowCount++); optionPanels.Add(panel); return panel;
        }
        private static void Row(TableLayoutPanel panel, string label, Control control)
        {
            int row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 0, row);
            control.Dock = DockStyle.Fill; panel.Controls.Add(control, 1, row);
        }
        private void UpdateOptions()
        {
            MediaOperation selected = (MediaOperation)operationBox.SelectedIndex;
            foreach (var panel in optionPanels) panel.Visible = false;
            subtitlePanel.Visible = selected == MediaOperation.SoftSubtitles || selected == MediaOperation.BurnSubtitles;
            trimPanel.Visible = selected == MediaOperation.Trim; cropPanel.Visible = selected == MediaOperation.Crop;
            resizePanel.Visible = selected == MediaOperation.Resize; mergePanel.Visible = selected == MediaOperation.Merge;
            shiftPanel.Visible = selected == MediaOperation.ShiftAudio;
            audioPanel.Visible = selected == MediaOperation.ExtractAudio || selected == MediaOperation.Merge && audioOnly.Checked;
            up.Enabled = down.Enabled = selected == MediaOperation.Merge;
            if (selected != MediaOperation.ShiftAudio) replaceOriginal.Checked = false;
            inputs.Refresh();
            explanation.Text = selected == MediaOperation.Merge ? T("列表中的所有片段将按顺序拼接。请使用编码、尺寸和音轨兼容的片段。", "All listed clips are merged in order. Use clips with matching codecs, dimensions and audio tracks.")
                : T("处理列表中选中的文件。除明确选择替换外，原文件保持不变。", "Process the selected file. The source is kept unless replacement is explicitly selected.");
        }
        private void MoveInput(int direction)
        {
            int index = inputs.SelectedIndex;
            if (index < 0 || index + direction < 0 || index + direction >= inputs.Items.Count) return;
            object item = inputs.Items[index]; inputs.Items.RemoveAt(index); inputs.Items.Insert(index + direction, item); inputs.SelectedIndex = index + direction;
        }
        private void SuggestOutput()
        {
            bool replace = (MediaOperation)operationBox.SelectedIndex == MediaOperation.ShiftAudio && replaceOriginal.Checked;
            output.Enabled = browseOutput.Enabled = !replace;
            if (inputs.SelectedIndex < 0) { output.Text = ""; return; }
            string input = inputs.SelectedItem.ToString(); if (replace) { output.Text = input; return; }
            MediaOperation selected = (MediaOperation)operationBox.SelectedIndex;
            string extension = selected == MediaOperation.ExtractAudio || selected == MediaOperation.Merge && audioOnly.Checked ? ".mp3"
                : selected == MediaOperation.SoftSubtitles ? ".mkv" : selected == MediaOperation.ShiftAudio || selected == MediaOperation.Merge ? Path.GetExtension(input) : ".mp4";
            output.Text = Path.Combine(Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input) + "." + selected.ToString().ToLowerInvariant() + extension);
        }
        private MediaProcessingOptions GetOptions()
        {
            var result = new MediaProcessingOptions
            {
                Operation = (MediaOperation)operationBox.SelectedIndex, OutputPath = output.Text.Trim(), SubtitlePath = subtitles.Text.Trim(),
                AudioOnly = audioOnly.Checked, AudioBitrate = (int)bitrate.Value, StartSeconds = start.Value, EndSeconds = end.Value,
                Width = (int)width.Value, Height = (int)height.Value, X = (int)x.Value, Y = (int)y.Value,
                KeepAspect = aspect.Checked, OffsetMilliseconds = (int)offset.Value, ReplaceOriginal = replaceOriginal.Checked
            };
            if (result.Operation == MediaOperation.Crop) { result.Width = (int)cropWidth.Value; result.Height = (int)cropHeight.Value; }
            if (result.Operation == MediaOperation.Merge) foreach (object item in inputs.Items) result.Inputs.Add(item.ToString());
            else if (inputs.SelectedItem != null) result.Inputs.Add(inputs.SelectedItem.ToString());
            return result;
        }
        private async void Process(object sender, EventArgs args)
        {
            var options = GetOptions();
            if (options.Inputs.Count == 0) { status.Text = T("请选择输入文件。", "Choose an input file."); return; }
            if (String.IsNullOrWhiteSpace(options.OutputPath)) { status.Text = T("请选择输出文件。", "Choose an output file."); return; }
            if (options.ReplaceOriginal || File.Exists(options.OutputPath))
            {
                if (MessageBox.Show(this, T("成功处理后替换此文件？\n", "Replace this file after successful processing?\n") + options.OutputPath, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                options.Overwrite = true;
            }
            operation = new CancellationTokenSource(); content.Enabled = convert.Enabled = false; cancel.Text = T("取消", "Cancel"); status.Text = T("正在处理…", "Processing…");
            try
            {
                await MediaProcessing.ProcessAsync(options, delegate(long milliseconds)
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { status.Text = T("已处理 ", "Processed ") + TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss"); }));
                }, operation.Token);
                status.Text = T("已保存：", "Saved: ") + options.OutputPath;
            }
            catch (OperationCanceledException) { status.Text = T("已取消，原文件未改变。", "Canceled. The source was not changed."); }
            catch (Exception error) { status.Text = error.Message; }
            finally
            {
                operation.Dispose(); operation = null; content.Enabled = convert.Enabled = true; cancel.Text = T("关闭", "Close");
                if (closeRequested) Close();
            }
        }
    }
}
