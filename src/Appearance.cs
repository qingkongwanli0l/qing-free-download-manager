using System;
using System.Drawing;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public static class Appearance
    {
        static string theme = "system";
        static float fontSize = 9F;

        public static void Configure(AppSettings settings)
        {
            theme = settings.AppearanceTheme;
            fontSize = settings.UiFontSize;
        }

        public static void Apply(Form form)
        {
            form.Icon = AppIcons.Normal;
            bool system = theme != "light" && theme != "dark", dark = theme == "dark";
            Color background = system ? SystemColors.Control : dark ? Color.FromArgb(31, 38, 45) : Color.FromArgb(242, 245, 248);
            Color surface = system ? SystemColors.Window : dark ? Color.FromArgb(43, 52, 61) : Color.White;
            Color foreground = system ? SystemColors.ControlText : dark ? Color.FromArgb(231, 237, 242) : Color.FromArgb(30, 45, 59);
            Color border = system ? SystemColors.ControlDark : dark ? Color.FromArgb(84, 100, 114) : Color.FromArgb(192, 205, 215);
            float scale = fontSize / form.Font.SizeInPoints;
            form.SuspendLayout();
            form.AutoScaleMode = AutoScaleMode.None;
            if (Math.Abs(scale - 1) > 0.01) form.Scale(new SizeF(scale, scale));
            Font font = new Font("Segoe UI", fontSize);
            form.Disposed += delegate { font.Dispose(); };
            ApplyControl(form, font, background, surface, foreground, border, system);
            Rectangle area = Screen.FromControl(form).WorkingArea;
            form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
            form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            form.ResumeLayout(true);
            form.Invalidate(true);
        }

        static void ApplyControl(Control control, Font font, Color background, Color surface, Color foreground, Color border, bool system)
        {
            control.Font = font;
            control.ForeColor = foreground;
            TableLayoutPanel layout = control as TableLayoutPanel;
            if (layout != null)
            {
                if (layout.ColumnCount == 1 && layout.ColumnStyles.Count == 0) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                if (layout.Parent is Form) layout.AutoScroll = true;
            }
            bool input = control is TextBoxBase || control is ListView || control is ListBox || control is TreeView || control is ComboBox || control is NumericUpDown || control is DateTimePicker || control is DataGridView;
            control.BackColor = input ? surface : background;
            Button button = control as Button;
            if (button != null)
            {
                button.UseVisualStyleBackColor = system;
                button.FlatStyle = system ? FlatStyle.Standard : FlatStyle.Flat;
                button.FlatAppearance.BorderColor = border;
            }
            ButtonBase buttonBase = control as ButtonBase;
            if (buttonBase != null) { buttonBase.Paint -= DrawDisabledButton; if (!system) buttonBase.Paint += DrawDisabledButton; }
            CheckBox check = control as CheckBox; if (check != null) { check.UseVisualStyleBackColor = system; check.FlatStyle = system ? FlatStyle.Standard : FlatStyle.Flat; }
            RadioButton radio = control as RadioButton; if (radio != null) { radio.UseVisualStyleBackColor = system; radio.FlatStyle = system ? FlatStyle.Standard : FlatStyle.Flat; }
            ComboBox combo = control as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle = system ? FlatStyle.Standard : FlatStyle.Flat;
                combo.DrawItem -= DrawCombo; combo.DrawMode = system ? DrawMode.Normal : DrawMode.OwnerDrawFixed;
                combo.ItemHeight = font.Height + 4; if (!system) combo.DrawItem += DrawCombo;
            }
            LinkLabel link = control as LinkLabel; if (link != null) link.LinkColor = system ? Color.Blue : Color.FromArgb(53, 169, 199);
            TabControl tabs = control as TabControl;
            if (tabs != null)
            {
                tabs.DrawItem -= DrawTab;
                tabs.DrawMode = system ? TabDrawMode.Normal : TabDrawMode.OwnerDrawFixed;
                if (!system) tabs.DrawItem += DrawTab;
            }
            DataGridView grid = control as DataGridView;
            if (grid != null)
            {
                grid.EnableHeadersVisualStyles = system;
                grid.BackgroundColor = surface; grid.GridColor = border;
                grid.DefaultCellStyle.BackColor = surface; grid.DefaultCellStyle.ForeColor = foreground;
                grid.ColumnHeadersDefaultCellStyle.BackColor = background; grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                grid.RowHeadersDefaultCellStyle.BackColor = background; grid.RowHeadersDefaultCellStyle.ForeColor = foreground;
                grid.RowTemplate.Height = font.Height + 8;
                foreach (DataGridViewRow row in grid.Rows) row.Height = font.Height + 8;
            }
            ToolStrip strip = control as ToolStrip;
            if (strip != null)
            {
                strip.Renderer = system ? (ToolStripRenderer)new ToolStripSystemRenderer() : new ToolStripProfessionalRenderer(new MenuColors(background, surface, border));
                ApplyItems(strip.Items, font, background, foreground);
            }
            if (control.ContextMenuStrip != null) ApplyControl(control.ContextMenuStrip, font, background, surface, foreground, border, system);
            foreach (Control child in control.Controls) ApplyControl(child, font, background, surface, foreground, border, system);
        }

        static void ApplyItems(ToolStripItemCollection items, Font font, Color background, Color foreground)
        {
            foreach (ToolStripItem item in items)
            {
                item.Font = font; item.BackColor = background; item.ForeColor = foreground;
                ToolStripDropDownItem menu = item as ToolStripDropDownItem;
                if (menu != null) ApplyItems(menu.DropDownItems, font, background, foreground);
            }
        }
        static void DrawTab(object sender, DrawItemEventArgs args)
        {
            TabControl tabs = (TabControl)sender;
            using (var brush = new SolidBrush(tabs.TabPages[args.Index].BackColor)) args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, tabs.TabPages[args.Index].Text, tabs.Font, args.Bounds, tabs.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (args.Index == tabs.SelectedIndex) using (var pen = new Pen(Color.FromArgb(47, 153, 178), 3)) args.Graphics.DrawLine(pen, args.Bounds.Left + 3, args.Bounds.Bottom - 2, args.Bounds.Right - 3, args.Bounds.Bottom - 2);
        }
        static void DrawCombo(object sender, DrawItemEventArgs args)
        {
            ComboBox combo = (ComboBox)sender;
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? SystemColors.Highlight : combo.BackColor)) args.Graphics.FillRectangle(brush, args.Bounds);
            string text = args.Index >= 0 ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
            TextRenderer.DrawText(args.Graphics, text, combo.Font, args.Bounds, selected ? SystemColors.HighlightText : combo.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            args.DrawFocusRectangle();
        }
        static void DrawDisabledButton(object sender, PaintEventArgs args)
        {
            ButtonBase button = (ButtonBase)sender;
            if (button.Enabled || theme != "dark") return;
            Rectangle text = button.ClientRectangle;
            bool choice = button is CheckBox || button is RadioButton;
            if (choice)
            {
                using (var brush = new SolidBrush(button.BackColor)) args.Graphics.FillRectangle(brush, text);
                Rectangle glyph = new Rectangle(1, (text.Height - 12) / 2, 12, 12);
                using (var pen = new Pen(Color.FromArgb(157, 171, 183)))
                {
                    if (button is RadioButton) args.Graphics.DrawEllipse(pen, glyph); else args.Graphics.DrawRectangle(pen, glyph);
                    bool selected = button is CheckBox ? ((CheckBox)button).Checked : ((RadioButton)button).Checked;
                    if (selected && button is CheckBox) args.Graphics.DrawLines(pen, new[] { new Point(glyph.Left + 2, glyph.Top + 6), new Point(glyph.Left + 5, glyph.Top + 9), new Point(glyph.Left + 10, glyph.Top + 3) });
                    else if (selected) using (var mark = new SolidBrush(pen.Color)) args.Graphics.FillEllipse(mark, glyph.Left + 3, glyph.Top + 3, 6, 6);
                }
                text.X += 18; text.Width -= 18;
            }
            else text.Inflate(-2, -2);
            using (var brush = new SolidBrush(button.BackColor)) args.Graphics.FillRectangle(brush, text);
            TextRenderer.DrawText(args.Graphics, button.Text, button.Font, text, Color.FromArgb(157, 171, 183), TextFormatFlags.VerticalCenter | (choice ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        }
        sealed class MenuColors : ProfessionalColorTable
        {
            readonly Color background, surface, border;
            internal MenuColors(Color background, Color surface, Color border) { this.background = background; this.surface = surface; this.border = border; UseSystemColors = false; }
            public override Color ToolStripGradientBegin { get { return background; } }
            public override Color ToolStripGradientMiddle { get { return background; } }
            public override Color ToolStripGradientEnd { get { return background; } }
            public override Color MenuStripGradientBegin { get { return background; } }
            public override Color MenuStripGradientEnd { get { return background; } }
            public override Color ToolStripDropDownBackground { get { return background; } }
            public override Color ImageMarginGradientBegin { get { return background; } }
            public override Color ImageMarginGradientMiddle { get { return background; } }
            public override Color ImageMarginGradientEnd { get { return background; } }
            public override Color MenuItemSelected { get { return surface; } }
            public override Color MenuItemSelectedGradientBegin { get { return surface; } }
            public override Color MenuItemSelectedGradientEnd { get { return surface; } }
            public override Color MenuItemPressedGradientBegin { get { return surface; } }
            public override Color MenuItemPressedGradientMiddle { get { return surface; } }
            public override Color MenuItemPressedGradientEnd { get { return surface; } }
            public override Color MenuItemBorder { get { return border; } }
            public override Color MenuBorder { get { return border; } }
            public override Color ToolStripBorder { get { return border; } }
        }
    }
}
