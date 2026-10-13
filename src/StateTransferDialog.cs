using System;
using System.Drawing;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class StateTransferDialog : Form
    {
        readonly CheckBox settings = new CheckBox();
        readonly CheckBox current = new CheckBox();
        readonly CheckBox history = new CheckBox();

        public bool IncludeSettings { get { return settings.Checked && settings.Enabled; } }
        public bool IncludeCurrent { get { return current.Checked && current.Enabled; } }
        public bool IncludeHistory { get { return history.Checked && history.Enabled; } }

        public StateTransferDialog(string language, bool importing, bool settingsAvailable, bool currentAvailable, bool historyAvailable)
        {
            Text = importing ? (Localization.Text(language, "Import settings and tasks", "导入设置和任务")) : (Localization.Text(language, "Export settings and tasks", "导出设置和任务"));
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(650, 430);
            MinimumSize = new Size(600, 400);
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(590, 0), Margin = new Padding(3, 3, 3, 14),
                Text = importing ? (Localization.Text(language, "Choose which sections to replace. Unselected settings and tasks remain unchanged; downloaded files are not deleted.", "选择要替换的范围。未选中的设置和任务保持原样；已下载的文件不会删除。"))
                    : (Localization.Text(language, "Choose which sections to export. The backup can transfer ordinary settings and tasks to another computer; media files and thumbnails are not included.", "选择要导出的范围。备份可用于迁移普通设置和任务；媒体文件和缩略图不包含在内。")) }, 0, 0);
            settings.Text = Localization.Text(language, "Application settings", "程序设置");
            current.Text = Localization.Text(language, "Current tasks (excluding history)", "当前任务（不含历史）");
            history.Text = Localization.Text(language, "Download history", "下载历史");
            settings.Enabled = settings.Checked = settingsAvailable;
            current.Enabled = current.Checked = currentAvailable;
            history.Enabled = history.Checked = historyAvailable;
            CheckBox[] choices = { settings, current, history };
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i].AutoSize = true;
                choices[i].Margin = new Padding(3, 6, 3, 6);
                root.Controls.Add(choices[i], 0, i + 1);
            }
            root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(590, 0), Margin = new Padding(3, 16, 3, 12),
                Text = Localization.Text(language, "Passwords, cookies, POST bodies and API tokens are excluded. Sign in again after import; POST-dependent tasks may need to be recreated. Download links may themselves contain credentials, so inspect the backup before sharing. Check folder paths after moving to another computer.", "导出不包含密码、Cookie、POST 请求体和 API 令牌。导入后需要重新授权；依赖 POST 的任务可能需要重新创建。下载链接本身可能包含访问凭据，分享备份前请检查。跨电脑导入后请核对目录路径。") }, 0, 4);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { AutoSize = true, Text = Localization.Text(language, "Cancel", "取消"), DialogResult = DialogResult.Cancel };
            var ok = new Button { AutoSize = true, Text = importing ? (Localization.Text(language, "Replace selected sections", "替换所选范围")) : (Localization.Text(language, "Continue export…", "继续导出…")) };
            ok.Click += delegate
            {
                if (!IncludeSettings && !IncludeCurrent && !IncludeHistory)
                {
                    MessageBox.Show(this, Localization.Text(language, "Select at least one section.", "请至少选择一项。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons, 0, 5);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
            Appearance.Apply(this);
        }
    }
}
