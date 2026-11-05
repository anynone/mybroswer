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

    /// <summary>白名单域名（含子域），命中后不启用任何伪装</summary>
    public List<string> PrivacyAllowlist { get; set; } = new();

    /// <summary>书签栏是否显示（Ctrl+Shift+B 切换）</summary>
    public bool ShowBookmarkBar { get; set; } = true;

    /// <summary>Canvas 扰动种子（同一配置指纹稳定，换种子即换指纹）</summary>
    public string FingerprintSeed { get; set; } = "";

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
