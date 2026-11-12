using System.Text.Json;

namespace RiBrowser.Privacy;

/// <summary>
/// 隐私防护设置（与参考项目 Polaris 的字段口径对齐），
/// 持久化在 %LocalAppData%\RiBrowser\settings.json。
/// </summary>
public sealed class PrivacySettings
{
    /// <summary>"chrome"（与 Chrome 一致）或 "custom"（自定义 UA）</summary>
    public string UserAgentMode { get; set; } = "chrome";

    public string CustomUserAgent { get; set; } = "";

    /// <summary>IANA 时区 ID；空 = 跟随系统</summary>
    public string Timezone { get; set; } = "";

    /// <summary>浏览器语言（如 en-US）；空 = 跟随系统</summary>
    public string BrowserLanguage { get; set; } = "";

    /// <summary>字体指纹防护（默认开）</summary>
    public bool FontGuard { get; set; } = true;

    /// <summary>Canvas 指纹扰动（默认关：与真实 Chrome 画布哈希一致）</summary>
    public bool CanvasNoise { get; set; } = false;

    /// <summary>WebRTC 防泄露（默认开）</summary>
    public bool WebrtcGuard { get; set; } = true;

    /// <summary>
    /// 位置伪装：开启后 Geolocation API 返回 LocationLat/Lng 自设坐标；关闭时下发
    /// 超界坐标强制"位置不可用"。默认开，坐标默认洛杉矶市中心——与默认伪装身份
    /// （America/Los_Angeles 时区 + en-US）配套成完整地理画像。
    /// 坐标 0/0 视为未设置，不生效。
    /// </summary>
    public bool LocationSpoof { get; set; } = true;

    public double LocationLat { get; set; } = 34.0522;

    public double LocationLng { get; set; } = -118.2437;

    /// <summary>白名单域名（含子域），命中后不启用任何伪装</summary>
    public List<string> PrivacyAllowlist { get; set; } = new();

    /// <summary>书签栏是否显示（Ctrl+Shift+B 切换）</summary>
    public bool ShowBookmarkBar { get; set; } = true;

    /// <summary>默认搜索引擎（google / baidu），地址栏非 URL 输入时使用</summary>
    public string SearchEngine { get; set; } = "google";

    /// <summary>启动页地址（新标签页与主页按钮），默认 Google</summary>
    public string HomePage { get; set; } = "https://www.google.com";

    /// <summary>Canvas 扰动种子（同一配置指纹稳定，换种子即换指纹）</summary>
    public string FingerprintSeed { get; set; } = "";

    // ==================== 代理（重启浏览器后生效） ====================

    /// <summary>"off" 直连 / "manual" 手动 HTTP·SOCKS 代理 / "singbox" 内置 sing-box 内核</summary>
    public string ProxyMode { get; set; } = "off";

    /// <summary>manual 模式协议："socks5" / "http"</summary>
    public string ProxyScheme { get; set; } = "socks5";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    /// <summary>认证代理 Chromium 命令行不支持，仅预留（带认证请用 sing-box 模式）</summary>
    public string ProxyUser { get; set; } = "";
    public string ProxyPass { get; set; } = "";

    /// <summary>sing-box 远端协议："shadowsocks" / "vmess" / "vless" / "trojan"</summary>
    public string SbProto { get; set; } = "shadowsocks";
    public string SbServer { get; set; } = "";
    public int SbPort { get; set; } = 443;
    /// <summary>shadowsocks 加密方法（如 aes-128-gcm / 2022-blake3-aes-128-gcm）</summary>
    public string SbMethod { get; set; } = "aes-128-gcm";
    /// <summary>shadowsocks / trojan 密码</summary>
    public string SbPassword { get; set; } = "";
    /// <summary>vmess / vless UUID</summary>
    public string SbUuid { get; set; } = "";
    /// <summary>sing-box 本地混合入站端口（socks+http 同端口）</summary>
    public int SbLocalPort { get; set; } = 7897;

    /// <summary>全局单例，Program.Main 启动时加载</summary>
    public static PrivacySettings Current { get; set; } = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static PrivacySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<PrivacySettings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    if (string.IsNullOrEmpty(loaded.FingerprintSeed))
                        loaded.FingerprintSeed = NewSeed();
                    return loaded;
                }
            }
        }
        catch { /* 配置损坏时回退默认值 */ }
        var fresh = new PrivacySettings { FingerprintSeed = NewSeed() };
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 磁盘只读等极端情况下静默失败，不影响浏览 */ }
    }

    private static string NewSeed() => Guid.NewGuid().ToString("N");

    /// <summary>host 是否命中白名单（精确匹配或子域）。</summary>
    public bool IsWhitelisted(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant();
        foreach (var entry in PrivacyAllowlist)
        {
            var d = NormalizeDomain(entry);
            if (d.Length == 0) continue;
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>整理用户输入的域名：去协议/路径/端口，转小写。</summary>
    public static string NormalizeDomain(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return "";
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        foreach (var cut in new[] { '/', '?', '#', ':' })
        {
            int i = s.IndexOf(cut);
            if (i >= 0) s = s[..i];
        }
        return s.Trim('.').ToLowerInvariant();
    }
}
