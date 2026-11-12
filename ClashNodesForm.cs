using RiBrowser.Privacy;

namespace RiBrowser;

/// <summary>手动节点配置编辑器（Clash YAML 格式）：公司直发 trojan 域名/端口/密钥
/// 这类"没有订阅链接"的场景，在这里手写 proxies；保存即校验并让自管内核重启生效。
/// 顶层只读取 proxies（必需）与 rules（可选），端口/控制器由浏览器托管。</summary>
internal sealed class ClashNodesForm : Form
{
    private static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);

    /// <summary>trojan 模板：用户改 server/port/password/sni 四处即可用。</summary>
    public const string Template = """
        # 顶层只读取 proxies（节点列表）与 rules（可选，分流规则）
        # 端口/控制器由浏览器托管，写了也会被忽略
        proxies:
          - name: "公司内网"
            type: trojan
            server: proxy.company.com     # 公司给的代理域名
            port: 443                     # 端口
            password: 公司给的密钥          # 密钥
            sni: proxy.company.com        # 公司给了 SNI 就填；没给删掉本行
            # client-fingerprint: chrome  # 可选：TLS 指纹伪装
            # skip-cert-verify: false     # 可选：自签证书时才开
        rules:                            # 可选：不写则全部流量走「节点选择」
          - DOMAIN-SUFFIX,company.internal,节点选择
        """;

    private readonly PrivacySettings _s;
    private readonly Action _onSaved;
    private readonly TextBox _box = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.5f),
        AcceptsTab = true,
        WordWrap = false,
    };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _state = new() { AutoSize = true };
    private readonly Button _save = new() { Text = "保存并生效" };
    private readonly Button _template = new() { Text = "恢复模板" };

    public ClashNodesForm(PrivacySettings s, Action onSaved)
    {
        _s = s;
        _onSaved = onSaved;

        Text = "手动节点配置（Clash 格式）";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 480);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        _hint.Text = "节点来源之二（与订阅可并存）：顶层写 proxies（必需）与 rules（可选）。保存后自管内核自动重启生效；接管 Verge 时节点由 Verge 管理。";
        _hint.MaximumSize = new Size(588, 0);
        _hint.Location = new Point(16, 12);

        _box.SetBounds(16, 52, 588, 340);
        _box.Text = string.IsNullOrWhiteSpace(_s.ClashManualYaml) ? Template : _s.ClashManualYaml;

        _state.Location = new Point(16, 400);
        _state.ForeColor = Color.FromArgb(0xD9, 0x30, 0x25);
        _save.SetBounds(16, 428, 110, 30);
        _save.Click += (s2, e2) => SaveAndClose();
        _template.SetBounds(134, 428, 96, 30);
        _template.Click += (s2, e2) =>
        {
            if (_box.Text.Trim().Length > 0 && _box.Text != Template
                && MessageBox.Show(this, "用模板覆盖当前内容？未保存的修改会丢失。", "Ri 浏览器",
                       MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            _box.Text = Template;
        };
        var cancel = new Button { Text = "取消" };
        cancel.SetBounds(238, 428, 80, 30);
        cancel.Click += (s2, e2) => Close();

        Controls.AddRange(new Control[] { _hint, _box, _state, _save, _template, cancel });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        DpiUtil.ScaleTree(this, DeviceDpi / 96f);
    }

    private void SaveAndClose()
    {
        var text = _box.Text;
        var (names, error) = ClashCoreManager.ParseManualYaml(text);
        if (text.Trim().Length > 0 && error != null)
        {
            _state.Text = error;
            return;
        }
        _s.ClashManualYaml = text;
        _s.Save();
        _onSaved();
        Close();
    }
}
