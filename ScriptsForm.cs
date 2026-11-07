namespace RiBrowser;

/// <summary>
/// 用户脚本管理（Chrome 扩展的替代方案：CEF 官方构建不含扩展子系统，无法安装 CRX）。
/// 支持导入 Tampermonkey 格式 .user.js，按 @match/@include/@run-at 自动注入匹配页面。
/// </summary>
internal sealed class ScriptsForm : Form
{
    private readonly ListBox _list = new();
    private readonly Button _import = new() { Text = "导入 .user.js" };
    private readonly Button _remove = new() { Text = "删除" };
    private readonly Button _openDir = new() { Text = "打开脚本目录" };

    public ScriptsForm()
    {
        Text = "用户脚本";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 360);
        Font = new Font("Segoe UI", 9f);

        var note = new Label
        {
            Text = "说明：CEF 内核不含 Chrome 扩展子系统，无法安装商店扩展/CRX。\n这里支持 Tampermonkey 格式的用户脚本（.user.js），导入后自动匹配页面注入。",
            AutoSize = false,
            Bounds = new Rectangle(14, 10, 432, 40),
            ForeColor = UiColors.TextMuted,
        };

        _list.SetBounds(14, 56, 432, 220);
        _list.HorizontalScrollbar = true;
        ReloadList();
        UserScripts.Changed += ReloadList;
        FormClosed += (s, e) => UserScripts.Changed -= ReloadList; // 静态事件随窗体反订阅，防累积

        _import.SetBounds(14, 288, 110, 30);
        _import.Click += (s, e) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "导入用户脚本",
                Filter = "用户脚本 (*.user.js)|*.user.js|所有文件 (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                UserScripts.Import(dlg.FileNames);
            }
        };
        _remove.SetBounds(132, 288, 80, 30);
        _remove.Click += (s, e) =>
        {
            if (_list.SelectedItem is UserScript script)
                UserScripts.Remove(script.FileName);
        };
        _openDir.SetBounds(220, 288, 110, 30);
        _openDir.Click += (s, e) =>
        {
            Directory.CreateDirectory(UserScripts.Dir);
            System.Diagnostics.Process.Start("explorer.exe", UserScripts.Dir);
        };

        Controls.AddRange(new Control[] { note, _list, _import, _remove, _openDir });
    }

    private void ReloadList()
    {
        if (IsDisposed) return;
        void Fill()
        {
            _list.Items.Clear();
            foreach (var s in UserScripts.Items) _list.Items.Add(s);
        }
        // 句柄未创建（构造期）或本就在 UI 线程时直接填充，
        // 否则 BeginInvoke 会抛 InvalidOperationException
        if (!IsHandleCreated || !InvokeRequired) Fill();
        else BeginInvoke(Fill);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UserScripts.Changed -= ReloadList;
        base.OnFormClosed(e);
    }
}
