using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class QueueDialog : Form
    {
        private readonly QueueSettings working;
        private readonly string language;
        private readonly TextBox nameBox = new TextBox();
        private readonly CheckBox enabledBox = new CheckBox();
        private readonly CheckBox renameBox = new CheckBox();
        private readonly NumericUpDown concurrentBox = new NumericUpDown();
        private readonly ComboBox modeBox = new ComboBox();
        private readonly DateTimePicker startBox = new DateTimePicker();
        private readonly DateTimePicker stopBox = new DateTimePicker();
        private readonly NumericUpDown intervalBox = new NumericUpDown();
        private readonly CheckBox[] dayBoxes = new CheckBox[7];
        private readonly NumericUpDown retriesBox = new NumericUpDown();
        private readonly NumericUpDown retryDelayBox = new NumericUpDown();
        private readonly ComboBox actionBox = new ComboBox();
        private readonly TextBox commandBox = new TextBox();
        private readonly TextBox argumentsBox = new TextBox();
        private readonly FlowLayoutPanel daysPanel = new FlowLayoutPanel();

        public QueueSettings Result { get; private set; }

        public QueueDialog(QueueSettings queue)
            : this(queue, CultureInfo.CurrentUICulture.Name) { }

        public QueueDialog(QueueSettings queue, string language)
        {
            working = queue.Copy();
            this.language = language;
            Text = L("Edit queue", "编辑队列");
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(590, 630);
            Size = new Size(620, 680);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(0, 0, 10, 0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(fields, 0, 0);

            nameBox.Text = working.Name;
            AddRow(fields, L("Name", "名称"), nameBox);
            enabledBox.Text = L("Allow this queue to run", "启用此队列");
            enabledBox.Checked = working.Enabled;
            AddRow(fields, "", enabledBox);
            SetupNumber(concurrentBox, 1, 128, working.MaxConcurrent);
            AddRow(fields, L("Concurrent downloads", "同时下载数"), concurrentBox);
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(new object[] { L("Manual", "手动"), L("Once", "指定日期"), L("Weekly schedule", "每周定时"), L("Repeat at interval", "间隔循环") });
            modeBox.SelectedIndex = (int)working.ScheduleMode;
            AddRow(fields, L("Schedule", "运行方式"), modeBox);

            startBox.Format = DateTimePickerFormat.Custom;
            startBox.ShowCheckBox = true;
            startBox.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            startBox.Value = working.StartAt ?? DateTime.Now.AddMinutes(5);
            startBox.Checked = working.StartAt.HasValue;
            AddRow(fields, L("Start", "开始时间"), startBox);
            stopBox.Format = DateTimePickerFormat.Custom;
            stopBox.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            stopBox.ShowCheckBox = true;
            stopBox.Value = working.StopAt ?? startBox.Value.AddHours(1);
            stopBox.Checked = working.StopAt.HasValue;
            AddRow(fields, L("Stop (optional)", "停止时间（可选）"), stopBox);

            daysPanel.AutoSize = true;
            daysPanel.WrapContents = true;
            var dayLabels = new[] { L("Sun", "周日"), L("Mon", "周一"), L("Tue", "周二"), L("Wed", "周三"), L("Thu", "周四"), L("Fri", "周五"), L("Sat", "周六") };
            for (int index = 0; index < dayBoxes.Length; index++)
            {
                dayBoxes[index] = new CheckBox { Text = dayLabels[index], AutoSize = true, Checked = working.Days.Contains((DayOfWeek)index) };
                daysPanel.Controls.Add(dayBoxes[index]);
            }
            AddRow(fields, L("Days", "星期"), daysPanel);
            SetupNumber(intervalBox, 1, 525600, working.RepeatIntervalMinutes);
            AddRow(fields, L("Interval (minutes)", "间隔（分钟）"), intervalBox);
            SetupNumber(retriesBox, 0, 100, working.RetryCount);
            AddRow(fields, L("Retries", "重试次数"), retriesBox);
            SetupNumber(retryDelayBox, 1, 86400, working.RetryDelaySeconds);
            AddRow(fields, L("Retry delay (seconds)", "重试等待（秒）"), retryDelayBox);
            renameBox.Text = L("Number the filename when it already exists", "文件已存在时编号保存"); renameBox.AutoSize = true; renameBox.Checked = working.RenameExisting;
            AddRow(fields, "", renameBox);

            actionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            actionBox.Items.AddRange(new object[] { L("Do nothing", "不执行操作"), L("Exit application", "退出程序"), L("Shut down computer", "关闭计算机"), L("Sleep", "睡眠"), L("Hibernate", "休眠") });
            actionBox.SelectedIndex = (int)working.CompletionAction;
            AddRow(fields, L("When queue finishes", "队列完成后"), actionBox);
            commandBox.Text = working.CompletionCommand;
            var commandPanel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            commandBox.Dock = DockStyle.Fill;
            var browse = new Button { Text = L("Browse…", "浏览…"), AutoSize = true };
            browse.Click += delegate
            {
                using (var dialog = new OpenFileDialog { CheckFileExists = true, Filter = L("Programs|*.exe;*.bat;*.cmd|All files|*.*", "程序|*.exe;*.bat;*.cmd|所有文件|*.*") })
                    if (dialog.ShowDialog(this) == DialogResult.OK) commandBox.Text = dialog.FileName;
            };
            commandPanel.Controls.Add(commandBox, 0, 0);
            commandPanel.Controls.Add(browse, 1, 0);
            AddRow(fields, L("Run command", "执行程序"), commandPanel);
            argumentsBox.Text = working.CompletionArguments;
            AddRow(fields, L("Arguments", "参数"), argumentsBox);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = L("Cancel", "取消"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = L("Save", "保存"), AutoSize = true };
            save.Click += Save;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
            modeBox.SelectedIndexChanged += delegate { UpdateScheduleControls(); };
            UpdateScheduleControls();
            Appearance.Apply(this);
        }

        private string L(string english, string chineseText) { return Localization.Text(language, english, chineseText); }

        private static void SetupNumber(NumericUpDown box, int minimum, int maximum, int value)
        {
            box.Minimum = minimum;
            box.Maximum = maximum;
            box.Value = Math.Max(minimum, Math.Min(maximum, value));
            box.ThousandsSeparator = true;
        }

        private static void AddRow(TableLayoutPanel panel, string label, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 5, 3, 5);
            panel.Controls.Add(control, 1, row);
        }

        private void UpdateScheduleControls()
        {
            var mode = (ScheduleMode)modeBox.SelectedIndex;
            startBox.Enabled = stopBox.Enabled = mode != ScheduleMode.Manual;
            daysPanel.Enabled = mode == ScheduleMode.Daily;
            intervalBox.Enabled = mode == ScheduleMode.Interval;
            startBox.CustomFormat = stopBox.CustomFormat = mode == ScheduleMode.Daily ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss";
        }

        private void Save(object sender, EventArgs args)
        {
            if (String.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show(this, L("Enter a queue name.", "请输入队列名称。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                nameBox.Focus();
                return;
            }
            var days = new List<DayOfWeek>();
            for (int index = 0; index < dayBoxes.Length; index++)
                if (dayBoxes[index].Checked) days.Add((DayOfWeek)index);
            var mode = (ScheduleMode)modeBox.SelectedIndex;
            if (mode == ScheduleMode.Daily && days.Count == 0)
            {
                MessageBox.Show(this, L("Choose at least one day.", "请至少选择一个星期。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (mode != ScheduleMode.Daily && startBox.Checked && stopBox.Checked && stopBox.Value <= startBox.Value)
            {
                MessageBox.Show(this, L("The stop time must be after the start time.", "停止时间必须晚于开始时间。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            working.Name = nameBox.Text.Trim();
            working.Enabled = enabledBox.Checked;
            working.MaxConcurrent = (int)concurrentBox.Value;
            working.ScheduleMode = mode;
            working.StartAt = mode == ScheduleMode.Manual || !startBox.Checked ? (DateTime?)null : startBox.Value;
            working.StopAt = mode == ScheduleMode.Manual || !stopBox.Checked ? (DateTime?)null : stopBox.Value;
            working.Days = days;
            working.RepeatIntervalMinutes = (int)intervalBox.Value;
            working.RetryCount = (int)retriesBox.Value;
            working.RetryDelaySeconds = (int)retryDelayBox.Value;
            working.RenameExisting = renameBox.Checked;
            working.CompletionAction = (CompletionAction)actionBox.SelectedIndex;
            working.CompletionCommand = commandBox.Text.Trim();
            working.CompletionArguments = argumentsBox.Text;
            Result = working;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
