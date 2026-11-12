using RiBrowser.Privacy;

namespace RiBrowser;

/// <summary>sing-box 代理配置页（独立窗口，菜单「代理设置」打开）：
/// 服务器（协议/域名/端口）、认证（密码/UUID/加密方法/flow）、
/// TLS（SNI/uTLS 指纹/ALPN/不安全/Reality）、传输层（tcp/ws）、本地混合端口，
/// 以及测试连接、内核下载、生成配置预览。所有修改即时保存，重启浏览器生效。</summary>
internal sealed class ProxyForm : Form
{
    private static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);

    private readonly PrivacySettings _s;
    private readonly Action<PrivacySettings>? _onChanged;

    // 服务器
    private readonly ComboBox _proto = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _server = new() { PlaceholderText = "服务器域名或 IP" };
    private readonly TextBox _port = new() { PlaceholderText = "端口" };

    // 认证（按协议切换）
    private readonly TextBox _password = new() { PlaceholderText = "密码", UseSystemPasswordChar = true };
    private readonly TextBox _uuid = new() { PlaceholderText = "UUID（vmess / vless）" };
    private readonly TextBox _method = new() { PlaceholderText = "加密方法，如 aes-128-gcm" };
    private readonly ComboBox _flow = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _l1 = new() { AutoSize = true };
    private readonly Label _l2 = new() { AutoSize = true };

    // TLS
    private readonly CheckBox _tls = new() { Text = "启用 TLS", AutoSize = true };
    private readonly TextBox _sni = new() { PlaceholderText = "SNI（空 = 用服务器地址）" };
    private readonly ComboBox _fp = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _alpn = new() { PlaceholderText = "ALPN，如 h2,http/1.1" };
    private readonly CheckBox _insecure = new() { Text = "允许不安全证书", AutoSize = true };
    private readonly TextBox _realityKey = new() { PlaceholderText = "Reality public key（填写即启用）" };
    private readonly TextBox _realitySid = new() { PlaceholderText = "short id" };

    // 传输
    private readonly ComboBox _transport = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _wsPath = new() { PlaceholderText = "ws 路径，如 /ray" };
    private readonly TextBox _wsHost = new() { PlaceholderText = "ws Host 头（CDN，选填）" };

    // 本地
    private readonly TextBox _local = new() { PlaceholderText = "本地混合端口" };

    // 防探测
    private readonly CheckBox _dnsCheck = new() { Text = "DNS 防泄露（域名经加密隧道解析）", AutoSize = true };
    private readonly TextBox _dnsServer = new() { PlaceholderText = "DoH 服务器，如 https://1.1.1.1/dns-query" };

    // 工具
    private readonly Button _test = new() { Text = "测试连接" };
    private readonly Button _download = new() { Text = "下载/更新内核" };
    private readonly Button _preview = new() { Text = "配置预览" };
    private readonly Label _state = new() { AutoSize = true, ForeColor = Muted };
    private Label _note = new();

    private readonly GroupBox _authGroup = new() { Text = "认证" };
    private readonly GroupBox _tlsGroup = new() { Text = "TLS" };
    private readonly GroupBox _transportGroup = new() { Text = "传输层（vmess / vless）" };
    private readonly Panel _tlsFields = new();
    private readonly Panel _realityRow = new();
    private readonly Panel _wsFields = new();

    public ProxyForm(PrivacySettings s, Action<PrivacySettings>? onChanged = null)
    {
        _s = s;
        _onChanged = onChanged;

        Text = "代理设置 · sing-box";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 668);
        AutoScroll = true;
        Font = new Font("Segoe UI", 9f);

        BuildServerGroup();
        BuildAuthGroup();
        BuildTlsGroup();
        BuildTransportGroup();
        BuildLocalGroup();
        BuildAntiProbeGroup();
        BuildTools();

        _note = new Label
        {
            Text = "修改即时保存，重启浏览器后生效；浏览器流量经 sing-box 本地端口转发到代理服务器。",
            AutoSize = true,
            ForeColor = Muted,
        };
        Controls.Add(_note);
        UpdateVisibility();
    }

    private void BuildServerGroup()
    {
        var g = new GroupBox { Text = "服务器", Bounds = new Rectangle(12, 10, 536, 56) };
        _proto.Items.AddRange(new object[] { "shadowsocks", "vmess", "vless", "trojan" });
        _proto.SelectedIndex = Math.Max(0, _proto.Items.IndexOf(_s.SbProto));
        _proto.SelectedIndexChanged += (s, e) =>
        {
            _s.SbProto = (string)_proto.SelectedItem!;
            // vless 默认 chrome 指纹：客户端 TLS 指纹与浏览器 UA 一致（防被动指纹识别）
            if (_s.SbProto == "vless" && _fp.SelectedIndex == 0) { _fp.SelectedIndex = 1; _s.SbFingerprint = "chrome"; }
            Changed();
        };
        _server.Text = _s.SbServer;
        _server.TextChanged += (s, e) => { _s.SbServer = _server.Text.Trim(); Changed(); };
        _port.Text = _s.SbPort.ToString();
        _port.TextChanged += (s, e) => { if (int.TryParse(_port.Text.Trim(), out var p) && p is > 0 and < 65536) _s.SbPort = p; Changed(); };

        var lp = new Label { Text = "协议", AutoSize = true, Location = new Point(16, 26) };
        _proto.SetBounds(56, 22, 110, 24);
        var lh = new Label { Text = "服务器", AutoSize = true, Location = new Point(180, 26) };
        _server.SetBounds(228, 22, 190, 24);
        _port.SetBounds(426, 22, 60, 24);
        g.Controls.AddRange(new Control[] { lp, _proto, lh, _server, _port });
        Controls.Add(g);
    }

    private void BuildAuthGroup()
    {
        _authGroup.Bounds = new Rectangle(12, 72, 536, 56);

        // 单行布局：标签1 + 字段1（方法/UUID/密码）+ 标签2 + 字段2（ss 密码 / vless Flow）
        _l1.Location = new Point(16, 30);
        _l2.Location = new Point(300, 30);
        _method.SetBounds(86, 26, 200, 24);
        _uuid.SetBounds(86, 26, 200, 24);
        _password.SetBounds(86, 26, 200, 24);
        _flow.Items.AddRange(new object[] { "（无）", "xtls-rprx-vision" });
        _flow.SelectedIndex = _s.SbFlow == "xtls-rprx-vision" ? 1 : 0;
        _flow.SetBounds(340, 26, 146, 24);

        _method.Text = _s.SbMethod;
        _method.TextChanged += (s, e) => { _s.SbMethod = _method.Text.Trim(); Changed(); };
        _password.Text = _s.SbPassword;
        _password.TextChanged += (s, e) => { _s.SbPassword = _password.Text; Changed(); };
        _uuid.Text = _s.SbUuid;
        _uuid.TextChanged += (s, e) => { _s.SbUuid = _uuid.Text.Trim(); Changed(); };
        _flow.SelectedIndexChanged += (s, e) => { _s.SbFlow = _flow.SelectedIndex == 1 ? "xtls-rprx-vision" : ""; Changed(); };

        _authGroup.Controls.AddRange(new Control[] { _l1, _l2, _method, _password, _uuid, _flow });
        Controls.Add(_authGroup);
    }

    private void BuildTlsGroup()
    {
        _tlsGroup.Bounds = new Rectangle(12, 166, 536, 140);

        _tls.Location = new Point(16, 24);
        _tls.Checked = _s.SbTls;
        _tls.CheckedChanged += (s, e) => { _s.SbTls = _tls.Checked; Changed(); };

        _tlsFields.SetBounds(0, 48, 536, 76);
        var ls = new Label { Text = "SNI", AutoSize = true, Location = new Point(16, 34) };
        _sni.SetBounds(52, 30, 170, 24);
        var lf = new Label { Text = "指纹", AutoSize = true, Location = new Point(240, 34) };
        _fp.SetBounds(278, 30, 110, 24);
        var la = new Label { Text = "ALPN", AutoSize = true, Location = new Point(404, 34) };
        _alpn.SetBounds(440, 30, 66, 24);
        _sni.PlaceholderText = "空 = 用服务器地址";
        _insecure.Location = new Point(16, 4);
        _tlsFields.Controls.AddRange(new Control[] { _insecure, ls, _sni, lf, _fp, la, _alpn });

        _realityRow.SetBounds(0, 126, 536, 30);
        var lr = new Label { Text = "Reality 公钥", AutoSize = true, Location = new Point(16, 8) };
        _realityKey.SetBounds(90, 4, 240, 24);
        var lsid = new Label { Text = "短 ID", AutoSize = true, Location = new Point(344, 8) };
        _realitySid.SetBounds(382, 4, 124, 24);
        _realityKey.Text = _s.SbRealityKey;
        _realityKey.TextChanged += (s, e) => { _s.SbRealityKey = _realityKey.Text.Trim(); Changed(); };
        _realitySid.Text = _s.SbRealitySid;
        _realitySid.TextChanged += (s, e) => { _s.SbRealitySid = _realitySid.Text.Trim(); Changed(); };
        _realityRow.Controls.AddRange(new Control[] { lr, _realityKey, lsid, _realitySid });

        _tlsGroup.Controls.AddRange(new Control[] { _tls, _tlsFields, _realityRow });
        Controls.Add(_tlsGroup);
    }

    private void BuildTransportGroup()
    {
        _transportGroup.Bounds = new Rectangle(12, 312, 536, 52);
        var lt = new Label { Text = "类型", AutoSize = true, Location = new Point(16, 28) };
        _transport.Items.AddRange(new object[] { "tcp", "ws" });
        _transport.SelectedIndex = _s.SbTransport == "ws" ? 1 : 0;
        _transport.SelectedIndexChanged += (s, e) => { _s.SbTransport = _transport.SelectedIndex == 1 ? "ws" : "tcp"; Changed(); };
        _transport.SetBounds(56, 24, 70, 24);

        var lp = new Label { Text = "路径", AutoSize = true, Location = new Point(16, 8) };
        _wsPath.SetBounds(50, 4, 150, 24);
        var lh = new Label { Text = "Host", AutoSize = true, Location = new Point(216, 8) };
        _wsHost.SetBounds(250, 4, 236, 24);
        _wsPath.Text = _s.SbWsPath;
        _wsPath.TextChanged += (s, e) => { _s.SbWsPath = _wsPath.Text.Trim(); Changed(); };
        _wsHost.Text = _s.SbWsHost;
        _wsHost.TextChanged += (s, e) => { _s.SbWsHost = _wsHost.Text.Trim(); Changed(); };
        _wsFields.SetBounds(0, 48, 536, 30);
        _wsFields.Controls.AddRange(new Control[] { lp, _wsPath, lh, _wsHost });

        _transportGroup.Controls.AddRange(new Control[] { lt, _transport, _wsFields });
        Controls.Add(_transportGroup);
    }

    private void BuildLocalGroup()
    {
        var g = new GroupBox { Text = "本地入站", Bounds = new Rectangle(12, 404, 536, 52) };
        var l = new Label { Text = "混合端口（socks + http 同端口）", AutoSize = true, Location = new Point(16, 24) };
        _local.SetBounds(230, 20, 70, 24);
        _local.Text = _s.SbLocalPort.ToString();
        _local.TextChanged += (s, e) => { if (int.TryParse(_local.Text.Trim(), out var p) && p is > 0 and < 65536) _s.SbLocalPort = p; Changed(); };
        g.Controls.AddRange(new Control[] { l, _local });
        Controls.Add(g);
    }

    /// <summary>防探测（内网安全）：DNS 防泄露 + DoH 服务器。</summary>
    private void BuildAntiProbeGroup()
    {
        var g = new GroupBox { Text = "防探测（内网安全）", Bounds = new Rectangle(12, 462, 536, 122) };

        _dnsCheck.Location = new Point(16, 24);
        _dnsCheck.Checked = _s.SbDnsProtect;
        _dnsCheck.CheckedChanged += (s, e) => { _s.SbDnsProtect = _dnsCheck.Checked; Changed(); };
        var hint = new Label
        {
            Text = "域名经加密隧道 DoH 解析，不向运营商 DNS 泄露访问目标；内网私有域名需公司 DNS 时才关闭。",
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Muted,
            Location = new Point(34, 44),
        };
        var ld = new Label { Text = "DoH", AutoSize = true, Location = new Point(16, 88) };
        _dnsServer.SetBounds(80, 84, 406, 24);
        _dnsServer.Text = _s.SbDnsServer;
        _dnsServer.TextChanged += (s, e) => { _s.SbDnsServer = _dnsServer.Text.Trim(); Changed(); };

        g.Controls.AddRange(new Control[] { _dnsCheck, hint, ld, _dnsServer });
        Controls.Add(g);
    }

    private void BuildTools()
    {
        _test.SetBounds(16, 468, 90, 28);
        _test.Click += async (s, e) =>
        {
            _state.Text = "测试中…";
            try { _state.Text = await ProxyManager.TestConnectionAsync(_s); }
            catch (Exception ex) { _state.Text = "测试失败：" + ex.Message; }
        };
        _download.SetBounds(114, 468, 110, 28);
        _download.Click += async (s, e) =>
        {
            _state.Text = "正在下载 sing-box 内核…";
            try
            {
                await ProxyManager.EnsureBinaryAsync();
                _state.Text = "sing-box 内核下载完成。";
            }
            catch (Exception ex) { _state.Text = "下载失败：" + ex.Message; }
        };
        _preview.SetBounds(232, 468, 90, 28);
        _preview.Click += (s, e) => ShowPreview();
        _state.Location = new Point(16, 504);
        _state.MaximumSize = new Size(504, 0);

        Controls.AddRange(new Control[] { _test, _download, _preview, _state });
    }

    /// <summary>弹出生成的 config.json（只读），方便对照机场/服务器参数。</summary>
    private void ShowPreview()
    {
        using var f = new Form
        {
            Text = "config.json 预览（实际写入 %LocalAppData%/RiBrowser/singbox）",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(640, 460),
            Font = new Font("Consolas", 9f),
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = ProxyManager.GenerateConfigJson(_s),
        };
        f.Controls.Add(box);
        f.ShowDialog(this);
    }

    /// <summary>按协议切换可见字段。</summary>
    private void UpdateVisibility()
    {
        var p = _s.SbProto;
        var ss = p == "shadowsocks";

        // 认证区：单行动态字段（字段位置在 BuildAuthGroup 固定，只切换可见性与标签）
        _method.Visible = ss;
        _uuid.Visible = p is "vmess" or "vless";
        _flow.Visible = p == "vless";
        _l1.Text = ss ? "加密方法" : (p == "trojan" ? "密码" : "UUID");
        _l2.Text = ss ? "密码" : "Flow";
        _l2.Visible = ss || p == "vless";
        _password.Visible = ss; // trojan 的密码显示在字段1位置：
        if (p == "trojan")
        {
            _password.Visible = true;
            _password.SetBounds(86, 26, 200, 24);
        }
        else
        {
            _password.SetBounds(340, 26, 146, 24);
        }

        // TLS 区（ss 不用 TLS）
        _tlsGroup.Visible = !ss;
        _tlsFields.Visible = _tls.Checked;
        _realityRow.Visible = p == "vless" && _tls.Checked;
        _tlsGroup.Height = !_tls.Checked ? 48 : (p == "vless" ? 172 : 138);

        // 传输区
        _transportGroup.Visible = p is "vmess" or "vless";
        _wsFields.Visible = _s.SbTransport == "ws";
        _transportGroup.Height = _s.SbTransport == "ws" ? 86 : 52;

        LayoutGroups();
    }

    /// <summary>分组纵向紧凑排布（高度随可见性变化）。</summary>
    private void LayoutGroups()
    {
        var y = 10;
        foreach (Control c in Controls)
        {
            if (c is GroupBox g && g.Visible)
            {
                g.Location = new Point(12, y);
                y += g.Height + 6;
            }
        }
        _test.Location = new Point(16, y + 4);
        _download.Location = new Point(114, y + 4);
        _preview.Location = new Point(232, y + 4);
        _state.Location = new Point(16, y + 40);
        _state.MaximumSize = new Size(504, 0);
        _note.Location = new Point(18, y + 76);
        // 说明跟随内容排布；超高时交给 AutoScroll
        if (y + 100 > ClientSize.Height) ClientSize = new Size(ClientSize.Width, y + 100);
    }

    private void Changed()
    {
        _s.Save();
        _onChanged?.Invoke(_s);
        UpdateVisibility();
    }
}
