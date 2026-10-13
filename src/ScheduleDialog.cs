using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class ScheduleDialog : Form
    {
        readonly ScheduleSettings working;
        readonly bool chinese;
        readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly DateTimePicker start = new DateTimePicker { Format = DateTimePickerFormat.Custom, ShowCheckBox = true };
        readonly DateTimePicker stop = new DateTimePicker { Format = DateTimePickerFormat.Custom, ShowCheckBox = true };
        readonly FlowLayoutPanel days = new FlowLayoutPanel { AutoSize = true };
        readonly NumericUpDown interval = new NumericUpDown { Minimum = 1, Maximum = 525600 };
        readonly NumericUpDown retries = new NumericUpDown { Maximum = 100 };
        readonly NumericUpDown delay = new NumericUpDown { Minimum = 1, Maximum = 86400 };
        readonly CheckBox rename = new CheckBox { AutoSize = true };
        public ScheduleSettings Result { get; private set; }
        string T(string zh, string en) { return chinese ? zh : en; }

        public ScheduleDialog(ScheduleSettings schedule, string language)
        {
            working = schedule.Copy(); chinese = language == "zh-CN";
            Text = T("任务计划", "Download schedule"); Size = new Size(610, 530); MinimumSize = new Size(530, 480);
            Font = new Font("Segoe UI", 9F); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mode.Items.AddRange(new[] { T("手动", "Manual"), T("一次", "Once"), T("每周定时", "Weekly schedule"), T("完成后间隔循环", "Repeat after completion") }); mode.SelectedIndex = (int)working.ScheduleMode;
            Row(root, T("运行方式", "Schedule"), mode);
            start.Value = working.StartAt ?? DateTime.Now.AddMinutes(5); start.Checked = working.StartAt.HasValue;
            stop.Value = working.StopAt ?? start.Value.AddHours(1); stop.Checked = working.StopAt.HasValue;
            Row(root, T("开始时间", "Start"), start); Row(root, T("停止时间（可选）", "Stop (optional)"), stop);
            string[] labels = chinese ? new[] { "日", "一", "二", "三", "四", "五", "六" } : new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            for (int i = 0; i < labels.Length; i++) days.Controls.Add(new CheckBox { Text = labels[i], AutoSize = true, Tag = (DayOfWeek)i, Checked = working.Days.Contains((DayOfWeek)i) });
            Row(root, T("星期", "Days"), days);
            interval.Value = Math.Max(1, Math.Min(interval.Maximum, working.RepeatIntervalMinutes)); Row(root, T("完成后等待（分钟）", "Wait after completion (min)"), interval);
            retries.Value = Math.Max(0, Math.Min(retries.Maximum, working.RetryCount)); Row(root, T("重试次数", "Retries"), retries);
            delay.Value = Math.Max(1, Math.Min(delay.Maximum, working.RetryDelaySeconds)); Row(root, T("重试等待（秒）", "Retry delay (seconds)"), delay);
            rename.Text = T("文件已存在时编号保存", "Number the filename when it already exists"); rename.Checked = working.RenameExisting; Row(root, "", rename);
            Row(root, "", new Label { AutoSize = true, MaximumSize = new Size(340, 0), Text = T("计划同时受所属队列时间与同时下载数约束。手动暂停后需继续任务；强制开始会略过时间限制。", "The queue's schedule and concurrency also apply. Resume manually paused downloads; Force start bypasses time restrictions.") });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var cancel = new Button { Text = T("取消", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = T("保存", "Save"), AutoSize = true }; save.Click += Save;
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); Controls.Add(root); Controls.Add(buttons); AcceptButton = save; CancelButton = cancel;
            mode.SelectedIndexChanged += delegate { UpdateMode(); }; UpdateMode();
            Appearance.Apply(this);
        }

        static void Row(TableLayoutPanel panel, string label, Control control)
        {
            int row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 8) }, 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(3, 5, 3, 5); panel.Controls.Add(control, 1, row);
        }

        void UpdateMode()
        {
            var selected = (ScheduleMode)mode.SelectedIndex;
            start.Enabled = selected != ScheduleMode.Manual; days.Enabled = selected == ScheduleMode.Daily; interval.Enabled = selected == ScheduleMode.Interval;
            start.CustomFormat = stop.CustomFormat = selected == ScheduleMode.Daily ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss";
        }

        void Save(object sender, EventArgs args)
        {
            var selected = (ScheduleMode)mode.SelectedIndex;
            var selectedDays = days.Controls.Cast<CheckBox>().Where(x => x.Checked).Select(x => (DayOfWeek)x.Tag).ToList();
            if (selected == ScheduleMode.Daily && selectedDays.Count == 0) { MessageBox.Show(this, T("请至少选择一天。", "Choose at least one day."), Text); return; }
            if (selected != ScheduleMode.Daily && start.Checked && stop.Checked && stop.Value <= start.Value) { MessageBox.Show(this, T("停止时间必须晚于开始时间。", "Stop must be after start."), Text); return; }
            working.ScheduleMode = selected; working.StartAt = selected == ScheduleMode.Manual || !start.Checked ? (DateTime?)null : start.Value;
            working.StopAt = stop.Checked ? (DateTime?)stop.Value : null; working.Days = selectedDays;
            working.RepeatIntervalMinutes = (int)interval.Value; working.RetryCount = (int)retries.Value; working.RetryDelaySeconds = (int)delay.Value;
            working.RenameExisting = rename.Checked; Result = working; DialogResult = DialogResult.OK; Close();
        }
    }
}
