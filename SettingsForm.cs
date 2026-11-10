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

    public SettingsForm(PrivacySettings settings, Action<PrivacySettings>? onChanged = null)
    {
        _s = settings;
        _onChanged = onChanged;

        Text = "隐私防护";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 640);
        AutoScroll = true;
        Font = new Font("Segoe UI", 9f);

        BuildIdentityGroup();
        BuildFingerprintGroup();
        BuildAllowlistGroup();

        var note = new Label
        {
            Text = "所有修改即时保存并生效；浏览器语言修改后重启可深入 Web Worker。",
            AutoSize = true,
            Location = new Point(18, 596),
            ForeColor = Muted,
        };
        Controls.Add(note);
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
