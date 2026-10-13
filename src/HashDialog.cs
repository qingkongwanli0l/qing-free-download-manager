using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingDownloadManager
{
    public sealed class HashDialog : Form
    {
        private readonly string path;
        private readonly bool chinese;
        private readonly string[] names = { "MD5", "SHA1", "SHA256", "SHA512" };
        private readonly Dictionary<string, TextBox> values = new Dictionary<string, TextBox>();
        private readonly TextBox expected = new TextBox();
        private readonly Label comparison = new Label();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button calculate = new Button();
        private readonly Button close = new Button();
        private CancellationTokenSource operation;
        private Dictionary<string, string> hashes;
        private bool closeRequested;

        public HashDialog(string path, string language)
        {
            this.path = path;
            chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            Text = T("文件摘要", "File hashes");
            Size = new Size(850, 430); MinimumSize = new Size(650, 430);
            Font = new Font("Segoe UI", 9F); AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 10 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(new Label { Text = T("文件", "File"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            root.Controls.Add(new TextBox { Text = path, ReadOnly = true, Dock = DockStyle.Fill }, 1, 0);
            for (int i = 0; i < names.Length; i++)
            {
                root.Controls.Add(new Label { Text = names[i], AutoSize = true, Anchor = AnchorStyles.Left }, 0, i + 1);
                var value = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
                values.Add(names[i], value); root.Controls.Add(value, 1, i + 1);
            }
            root.Controls.Add(new Label { Text = T("预期摘要", "Expected hash"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
            expected.Dock = DockStyle.Fill; expected.TextChanged += delegate { CompareHash(); }; root.Controls.Add(expected, 1, 5);
            comparison.AutoSize = true; comparison.Dock = DockStyle.Fill; comparison.Margin = new Padding(3, 8, 3, 8);
            root.Controls.Add(comparison, 0, 6); root.SetColumnSpan(comparison, 2);
            progress.Dock = DockStyle.Fill; root.Controls.Add(progress, 0, 7); root.SetColumnSpan(progress, 2);
            status.AutoSize = true; status.Dock = DockStyle.Fill; root.Controls.Add(status, 0, 8); root.SetColumnSpan(status, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            close.Text = T("关闭", "Close"); close.AutoSize = true; close.Click += delegate { Close(); };
            calculate.Text = T("重新计算", "Calculate again"); calculate.AutoSize = true;
            calculate.Click += async delegate { if (operation != null) operation.Cancel(); else await CalculateAsync(); };
            buttons.Controls.Add(close); buttons.Controls.Add(calculate); root.Controls.Add(buttons, 0, 9); root.SetColumnSpan(buttons, 2);
            Controls.Add(root); CancelButton = close;
            Shown += async delegate { await CalculateAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (operation != null) { closeRequested = true; operation.Cancel(); e.Cancel = true; }
            };
            CompareHash();
            Appearance.Apply(this);
        }

        private async Task CalculateAsync()
        {
            hashes = null; foreach (TextBox value in values.Values) value.Clear();
            progress.Value = 0; CompareHash();
            operation = new CancellationTokenSource();
            calculate.Text = T("取消计算", "Cancel calculation"); status.Text = T("正在读取文件…", "Reading file…");
            var reporter = new Progress<int>(delegate(int percent) { progress.Value = percent; });
            CancellationToken token = operation.Token;
            try
            {
                hashes = await Task.Run(delegate { return ComputeHashes(path, token, reporter); }, token);
                foreach (string name in names) values[name].Text = hashes[name];
                progress.Value = 100; status.Text = T("摘要计算完成，文件未修改。", "Hashes calculated. The file was not changed.");
                CompareHash();
            }
            catch (OperationCanceledException) { status.Text = T("已取消。", "Canceled."); }
            catch (Exception error) { status.Text = error.Message; }
            finally
            {
                operation.Dispose(); operation = null; calculate.Text = T("重新计算", "Calculate again");
                if (closeRequested) Close();
            }
        }

        internal static Dictionary<string, string> ComputeHashes(string path, CancellationToken token, IProgress<int> progress)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (HashAlgorithm md5 = MD5.Create())
            using (HashAlgorithm sha1 = SHA1.Create())
            using (HashAlgorithm sha256 = SHA256.Create())
            using (HashAlgorithm sha512 = SHA512.Create())
            {
                HashAlgorithm[] algorithms = { md5, sha1, sha256, sha512 };
                byte[] buffer = new byte[65536]; int read, previous = -1;
                while (true)
                {
                    token.ThrowIfCancellationRequested(); read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    foreach (HashAlgorithm algorithm in algorithms) algorithm.TransformBlock(buffer, 0, read, buffer, 0);
                    int percent = stream.Length == 0 ? 100 : (int)(stream.Position * 100.0 / stream.Length);
                    if (percent != previous) { progress.Report(percent); previous = percent; }
                }
                token.ThrowIfCancellationRequested();
                foreach (HashAlgorithm algorithm in algorithms) algorithm.TransformFinalBlock(buffer, 0, 0);
                return new Dictionary<string, string> { { "MD5", Hex(md5.Hash) }, { "SHA1", Hex(sha1.Hash) }, { "SHA256", Hex(sha256.Hash) }, { "SHA512", Hex(sha512.Hash) } };
            }
        }

        private void CompareHash()
        {
            string text = expected.Text.Trim(); comparison.ForeColor = SystemColors.ControlText;
            if (text.Length == 0) { comparison.Text = T("粘贴摘要以比对；支持 MD5、SHA1、SHA256 和 SHA512。", "Paste a hash to compare: MD5, SHA1, SHA256 or SHA512."); return; }
            string name = text.Length == 32 ? "MD5" : text.Length == 40 ? "SHA1" : text.Length == 64 ? "SHA256" : text.Length == 128 ? "SHA512" : null;
            bool valid = name != null;
            foreach (char character in text) if (!Uri.IsHexDigit(character)) valid = false;
            if (!valid) { comparison.Text = T("请输入完整的十六进制摘要。", "Enter a complete hexadecimal hash."); return; }
            if (hashes == null) { comparison.Text = T("等待计算后比对 ", "Waiting to compare ") + name + "."; return; }
            bool match = String.Equals(text, hashes[name], StringComparison.OrdinalIgnoreCase);
            comparison.ForeColor = match ? Color.DarkGreen : Color.Firebrick;
            comparison.Text = name + (match ? T(" 匹配。", " matches.") : T(" 不匹配。", " does not match."));
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private string T(string zh, string en) { return chinese ? zh : en; }
    }
}
