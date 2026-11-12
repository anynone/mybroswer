using System.Globalization;
using RiBrowser.Privacy;

namespace RiBrowser;

/// <summary>隐私防护设置对话框（菜单「隐私防护」/ Ctrl+, 打开）。所有修改即时保存。</summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);
    private readonly PrivacySettings _s;
    private readonly Action<PrivacySettings>? _onChanged;

    private readonly ComboBox _uaCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _uaCustom = new();
    private readonly ComboBox _langCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _tzCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _locCheck = new() { Text = "位置伪装（网页 Geolocation 取到自设坐标，而非真实位置）", AutoSize = true };
    private readonly TextBox _locLat = new() { PlaceholderText = "纬度，如 34.0522（洛杉矶）" };
    private readonly TextBox _locLng = new() { PlaceholderText = "经度，如 -118.2437" };
    private readonly CheckBox _fontCheck = new() { Text = "字体指纹防护（阻断字体宽度探测，中文字体恒报未安装）", AutoSize = true };
    private readonly CheckBox _webrtcCheck = new() { Text = "WebRTC 防泄露（隐藏页面可见的本机 IP）", AutoSize = true };
    private readonly CheckBox _canvasCheck = new() { Text = "Canvas 指纹扰动", AutoSize = true };
    private readonly Button _seedButton = new() { Text = "换种子", AutoSize = true };
    private readonly Label _seedLabel = new() { AutoSize = true, ForeColor = Muted };
    private readonly ListBox _allowList = new();
    private readonly TextBox _allowInput = new();
    private readonly Button _allowAdd = new() { Text = "添加" };
    private readonly Button _allowRemove = new() { Text = "删除" };

    // ---- 代理 ----
    private readonly ComboBox _proxyMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _kernelState = new() { AutoSize = true, ForeColor = Muted };
    private readonly ComboBox _manScheme = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _manHost = new() { PlaceholderText = "代理服务器" };
    private readonly TextBox _manPort = new() { PlaceholderText = "端口" };
    private readonly TextBox _manUser = new() { PlaceholderText = "用户名（选填）" };
    private readonly TextBox _manPass = new() { PlaceholderText = "密码（选填，暂不生效）", UseSystemPasswordChar = true };
    private readonly Button _sbOpen = new() { Text = "配置 sing-box…" };
    private readonly Label _sbSummary = new() { AutoSize = true, ForeColor = Muted };
    private readonly Panel _manualRows = new();
    private readonly Panel _sbRow = new();

    // ---- Clash 接管 ----
    private readonly TextBox _clashPort = new() { PlaceholderText = "端口" };
    private readonly TextBox _clashApi = new() { PlaceholderText = "http://127.0.0.1:9097" };
    private readonly TextBox _clashSecret = new() { PlaceholderText = "控制器密钥（无则留空）", UseSystemPasswordChar = true };
    private readonly TextBox _clashProbe = new() { PlaceholderText = "http://内网地址/探测路径" };
    private readonly TextBox _clashSub = new() { PlaceholderText = "https://…（订阅链接，自管内核用）" };
    private readonly CheckBox _clashGate = new() { Text = "启动门禁：连上内网才放行浏览", AutoSize = true };
    private readonly Panel _clashRows = new();

    public SettingsForm(PrivacySettings settings, Action<PrivacySettings>? onChanged = null)
    {
        _s = settings;
        _onChanged = onChanged;

        Text = "隐私防护";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 844);
        AutoScroll = true;
        // 布局按 96 DPI 设计；显示时框架按显示器实际 DPI 整体缩放
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        BuildIdentityGroup();
        BuildFingerprintGroup();
        BuildAllowlistGroup();
        BuildProxyGroup();

        var note = new Label
        {
            Text = "所有修改即时保存并生效；浏览器语言修改后重启生效，代理即时生效。",
            AutoSize = true,
            Location = new Point(18, 818),
            ForeColor = Muted,
        };
        Controls.Add(note);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        DpiUtil.ScaleTree(this, DeviceDpi / 96f); // 框架不缩子控件，见 DpiUtil 注释
    }

    // ---- 身份伪装 ----

    private void BuildIdentityGroup()
    {
        var g = new GroupBox { Text = "身份伪装", Bounds = new Rectangle(12, 10, 536, 252) };

        _uaCombo.Items.AddRange(new object[] { "与 Chrome 一致", "自定义" });
        _uaCombo.Bounds = new Rectangle(120, 26, 300, 24);
        _uaCombo.SelectedIndexChanged += (s, e) =>
        {
            _s.UserAgentMode = _uaCombo.SelectedIndex == 1 ? "custom" : "chrome";
            _uaCustom.Visible = _uaCustom.Enabled = _s.UserAgentMode == "custom";
            Changed();
        };
        _uaCustom.Bounds = new Rectangle(120, 56, 380, 24);
        _uaCustom.Visible = false;
        _uaCustom.TextChanged += (s, e) => { _s.CustomUserAgent = _uaCustom.Text; Changed(); };

        _langCombo.Bounds = new Rectangle(120, 92, 300, 24);
        _langCombo.Items.AddRange(new object[]
        {
            "跟随系统", "English (en-US)", "English (en-GB)", "中文 (zh-CN)",
            "中文繁體 (zh-TW)", "日本語 (ja-JP)", "한국어 (ko-KR)", "Deutsch (de-DE)",
            "Français (fr-FR)", "Русский (ru-RU)", "Español (es-ES)", "Português (pt-BR)",
        });
        _langCombo.SelectedIndexChanged += (s, e) =>
        {
            _s.BrowserLanguage = _langCombo.SelectedIndex <= 0 ? "" : LangValues[_langCombo.SelectedIndex];
            Changed();
        };

        _tzCombo.Bounds = new Rectangle(120, 128, 380, 24);
        _tzCombo.Items.Add("跟随系统");
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            var iana = TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var id) ? id : tz.Id;
            _tzCombo.Items.Add(new TzItem(tz.DisplayName, iana));
        }
        _tzCombo.SelectedIndexChanged += (s, e) =>
        {
            _s.Timezone = _tzCombo.SelectedIndex <= 0 ? "" : ((TzItem)_tzCombo.SelectedItem!).Value;
            Changed();
        };

        var tip = new Label
        {
            Text = "时区/语言立即对页面生效；浏览器语言重启后可深入 Web Worker。",
            AutoSize = true,
            Location = new Point(120, 168),
            ForeColor = Muted,
        };

        // 位置伪装：勾选 + 自设经纬度（与伪装时区配套用更像本地人）
        _locCheck.Location = new Point(16, 192);
        _locCheck.CheckedChanged += (s, e) => { _s.LocationSpoof = _locCheck.Checked; Changed(); };
        _locLat.SetBounds(64, 218, 130, 24);
        _locLat.TextChanged += (s, e) => ParseCoord(_locLat, v => _s.LocationLat = v);
        _locLng.SetBounds(220, 218, 130, 24);
        _locLng.TextChanged += (s, e) => ParseCoord(_locLng, v => _s.LocationLng = v);

        g.Controls.AddRange(new Control[]
        {
            MakeLabel("User-Agent：", 30), _uaCombo, _uaCustom,
            MakeLabel("浏览器语言：", 94), _langCombo,
            MakeLabel("页面时区：", 132), _tzCombo, tip,
            _locCheck, _locLat, _locLng,
        });
        Controls.Add(g);

        // 回填当前值
        _uaCombo.SelectedIndex = _s.UserAgentMode == "custom" ? 1 : 0;
        if (_s.UserAgentMode == "custom")
        {
            _uaCustom.Text = _s.CustomUserAgent;
            _uaCustom.Visible = _uaCustom.Enabled = true;
        }
        _langCombo.SelectedIndex = SelectLang(_s.BrowserLanguage);
        _tzCombo.SelectedIndex = SelectTz(_s.Timezone);
        _locCheck.Checked = _s.LocationSpoof;
        if (_s.LocationLat != 0) _locLat.Text = _s.LocationLat.ToString(CultureInfo.InvariantCulture);
        if (_s.LocationLng != 0) _locLng.Text = _s.LocationLng.ToString(CultureInfo.InvariantCulture);
    }

    // ---- 指纹防护 ----

    private void BuildFingerprintGroup()
    {
        var g = new GroupBox { Text = "指纹防护", Bounds = new Rectangle(12, 270, 536, 150) };

        _fontCheck.SetBounds(16, 30, 460, 22);
        _fontCheck.CheckedChanged += (s, e) => { _s.FontGuard = _fontCheck.Checked; Changed(); };

        _webrtcCheck.SetBounds(16, 60, 460, 22);
        _webrtcCheck.CheckedChanged += (s, e) => { _s.WebrtcGuard = _webrtcCheck.Checked; Changed(); };

        _canvasCheck.SetBounds(16, 90, 180, 22);
        _canvasCheck.CheckedChanged += (s, e) => { _s.CanvasNoise = _canvasCheck.Checked; Changed(); };
        _seedButton.SetBounds(210, 86, 78, 26);
        _seedButton.Click += (s, e) =>
        {
            _s.FingerprintSeed = Guid.NewGuid().ToString("N");
            UpdateSeedLabel();
            Changed();
        };
        _seedLabel.Location = new Point(300, 90);

        g.Controls.AddRange(new Control[] { _fontCheck, _webrtcCheck, _canvasCheck, _seedButton, _seedLabel });
        Controls.Add(g);

        _fontCheck.Checked = _s.FontGuard;
        _webrtcCheck.Checked = _s.WebrtcGuard;
        _canvasCheck.Checked = _s.CanvasNoise;
        UpdateSeedLabel();
    }

    // ---- 白名单 ----

    private void BuildAllowlistGroup()
    {
        var g = new GroupBox { Text = "白名单（不启用任何伪装的站点，含子域）", Bounds = new Rectangle(12, 428, 536, 156) };

        _allowList.SetBounds(16, 26, 500, 82);
        foreach (var d in _s.PrivacyAllowlist) _allowList.Items.Add(d);

        _allowInput.SetBounds(16, 116, 380, 24);
        _allowAdd.SetBounds(404, 115, 60, 26);
        _allowAdd.Click += (s, e) =>
        {
            var d = PrivacySettings.NormalizeDomain(_allowInput.Text);
            if (d.Length == 0 || _allowList.Items.Contains(d)) return;
            _s.PrivacyAllowlist.Add(d);
            _allowList.Items.Add(d);
            _allowInput.Clear();
            Changed();
        };
        _allowRemove.SetBounds(468, 115, 60, 26);
        _allowRemove.Click += (s, e) =>
        {
            if (_allowList.SelectedItem is not string d) return;
            _s.PrivacyAllowlist.Remove(d);
            _allowList.Items.Remove(d);
            Changed();
        };

        g.Controls.AddRange(new Control[] { _allowList, _allowInput, _allowAdd, _allowRemove });
        Controls.Add(g);
    }

    // ---- 代理 ----
    /// <summary>代理分组：模式 + 手动 SOCKS/HTTP；sing-box 详细参数在独立「代理设置」页配置。</summary>
    private void BuildProxyGroup()
    {
        var g = new GroupBox { Text = "代理（修改即时生效）", Bounds = new Rectangle(12, 592, 536, 216) };

        // 模式行（sing-box 已下线：仅旧配置仍在用该模式时显示为第 4 项，方便查看/迁移）
        _proxyMode.Items.AddRange(new object[]
        {
            "直连（不使用代理）",
            "手动代理（SOCKS / HTTP）",
            "Clash（接管本机内核，推荐）",
        });
        if (_s.ProxyMode == "singbox")
            _proxyMode.Items.Add("sing-box（已下线，建议迁移 Clash）");
        _proxyMode.SelectedIndex = _s.ProxyMode switch { "manual" => 1, "clash" => 2, "singbox" => 3, _ => 0 };
        _proxyMode.SetBounds(76, 24, 240, 24);
        _proxyMode.SelectedIndexChanged += (s, e) =>
        {
            _s.ProxyMode = _proxyMode.SelectedIndex switch
            {
                1 => "manual", 2 => "clash", 3 => "singbox", _ => "off",
            };
            UpdateProxyVisibility();
            Changed();
        };
        var modeLabel = new Label { Text = "模式", AutoSize = true, Location = new Point(16, 28) };
        _kernelState.Location = new Point(330, 28);
        _kernelState.Text = ProxyManager.BinaryReady ? "内核：已下载" : "内核：未下载";

        // 手动模式两行
        _manScheme.Items.AddRange(new object[] { "socks5", "http" });
        _manScheme.SelectedIndex = _s.ProxyScheme == "http" ? 1 : 0;
        _manScheme.SelectedIndexChanged += (s, e) => { _s.ProxyScheme = (string)_manScheme.SelectedItem!; Changed(); };
        _manHost.Text = _s.ProxyHost;
        _manHost.TextChanged += (s, e) => { _s.ProxyHost = _manHost.Text.Trim(); Changed(); };
        _manPort.Text = _s.ProxyPort.ToString();
        _manPort.TextChanged += (s, e) => { if (int.TryParse(_manPort.Text.Trim(), out var p) && p is > 0 and < 65536) _s.ProxyPort = p; Changed(); };
        _manUser.Text = _s.ProxyUser;
        _manUser.TextChanged += (s, e) => { _s.ProxyUser = _manUser.Text.Trim(); Changed(); };
        _manPass.Text = _s.ProxyPass;
        _manPass.TextChanged += (s, e) => { _s.ProxyPass = _manPass.Text; Changed(); };
        var manL1 = new Label { Text = "协议/服务器/端口", AutoSize = true, Location = new Point(16, 60) };
        _manScheme.SetBounds(140, 56, 70, 24);
        _manHost.SetBounds(218, 56, 170, 24);
        _manPort.SetBounds(396, 56, 60, 24);
        var manL2 = new Label { Text = "认证（选填）", AutoSize = true, Location = new Point(16, 90) };
        _manUser.SetBounds(140, 86, 150, 24);
        _manPass.SetBounds(300, 86, 156, 24);
        _manualRows.SetBounds(0, 34, 536, 86);
        _manualRows.Controls.AddRange(new Control[] { manL1, _manScheme, _manHost, _manPort, manL2, _manUser, _manPass });

        // sing-box：当前链路摘要 + 独立配置页入口
        var sbL = new Label { Text = "当前链路", AutoSize = true, Location = new Point(16, 64) };
        _sbSummary.SetBounds(76, 60, 300, 20);
        _sbOpen.SetBounds(380, 56, 120, 26);
        _sbOpen.Click += (s, e) =>
        {
            using var form = new ProxyForm(_s);
            form.ShowDialog(this);
            _kernelState.Text = ProxyManager.BinaryReady ? "内核：已下载" : "内核：未下载";
            UpdateProxyVisibility();
        };
        _sbRow.SetBounds(0, 34, 536, 60);
        _sbRow.Controls.AddRange(new Control[] { sbL, _sbSummary, _sbOpen });

        // Clash 接管四行：端口/控制器/密钥/探测地址 + 启动门禁
        _clashPort.Text = _s.ClashMixedPort.ToString();
        _clashPort.TextChanged += (s, e) => { if (int.TryParse(_clashPort.Text.Trim(), out var p) && p is > 0 and < 65536) _s.ClashMixedPort = p; Changed(); };
        _clashApi.Text = _s.ClashApiBase;
        _clashApi.TextChanged += (s, e) => { _s.ClashApiBase = _clashApi.Text.Trim(); Changed(); };
        _clashSecret.Text = _s.ClashApiSecret;
        _clashSecret.TextChanged += (s, e) => { _s.ClashApiSecret = _clashSecret.Text.Trim(); Changed(); };
        _clashProbe.Text = _s.ClashProbeUrl;
        _clashProbe.TextChanged += (s, e) => { _s.ClashProbeUrl = _clashProbe.Text.Trim(); Changed(); };
        _clashGate.Checked = _s.ClashGateOnStartup;
        _clashGate.CheckedChanged += (s, e) => { _s.ClashGateOnStartup = _clashGate.Checked; Changed(); };
        var cL1 = new Label { Text = "混合端口 / 控制器", AutoSize = true, Location = new Point(16, 32) };
        _clashPort.SetBounds(140, 28, 60, 24);
        _clashApi.SetBounds(210, 28, 246, 24);
        var cL2 = new Label { Text = "密钥", AutoSize = true, Location = new Point(16, 62) };
        _clashSecret.SetBounds(140, 58, 316, 24);
        var cL3 = new Label { Text = "内网探测地址", AutoSize = true, Location = new Point(16, 92) };
        _clashProbe.SetBounds(140, 88, 316, 24);
        _clashSub.Text = _s.ClashSubUrl;
        _clashSub.TextChanged += (s, e) => { _s.ClashSubUrl = _clashSub.Text.Trim(); Changed(); };
        var cL4 = new Label { Text = "订阅链接（选填）", AutoSize = true, Location = new Point(16, 122) };
        _clashSub.SetBounds(140, 118, 316, 24);
        _clashGate.Location = new Point(16, 150);
        _clashRows.SetBounds(0, 34, 536, 178);
        _clashRows.Controls.AddRange(new Control[]
        {
            cL1, _clashPort, _clashApi, cL2, _clashSecret, cL3, _clashProbe,
            cL4, _clashSub, _clashGate,
        });

        g.Controls.AddRange(new Control[] { modeLabel, _proxyMode, _kernelState, _manualRows, _sbRow, _clashRows });
        UpdateProxyVisibility();
        Controls.Add(g);
    }

    private void UpdateProxyVisibility()
    {
        _manualRows.Visible = _s.ProxyMode == "manual";
        _clashRows.Visible = _s.ProxyMode == "clash";
        _sbRow.Visible = _s.ProxyMode == "singbox";
        _sbSummary.Text = _s.ProxyMode != "singbox" ? "" :
            string.IsNullOrWhiteSpace(_s.SbServer)
                ? "已下线模式：未配置服务器，建议切换到 Clash"
                : $"已下线模式（参数仍生效）：{_s.SbProto} → {_s.SbServer}:{_s.SbPort} → 本地 {_s.SbLocalPort}";
    }


    // ---- 辅助 ----

    /// <summary>坐标输入框：解析成功才写入设置（非法输入保持旧值，不弹窗打扰）。</summary>
    private void ParseCoord(TextBox box, Action<double> set)
    {
        if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) set(v);
        Changed();
    }

    private static readonly string[] LangValues =
        { "", "en-US", "en-GB", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "ru-RU", "es-ES", "pt-BR" };

    private static Label MakeLabel(string text, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(16, y + 3),
    };

    private int SelectLang(string value)
    {
        var i = Array.IndexOf(LangValues, value);
        return i >= 0 ? i : 0;
    }

    private int SelectTz(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        for (int i = 1; i < _tzCombo.Items.Count; i++)
        {
            if (((TzItem)_tzCombo.Items[i]!).Value == value) return i;
        }
        // 不在系统时区列表里的已存值（如手改 json），追加显示
        _tzCombo.Items.Add(new TzItem($"（自定义）{value}", value));
        return _tzCombo.Items.Count - 1;
    }

    private void UpdateSeedLabel() =>
        _seedLabel.Text = "种子：" + (_s.FingerprintSeed.Length > 10
            ? _s.FingerprintSeed[..10] + "…"
            : _s.FingerprintSeed);

    private void Changed()
    {
        _s.Save();
        _onChanged?.Invoke(_s);
    }

    private sealed record TzItem(string Display, string Value)
    {
        public override string ToString() => Display;
    }
}
