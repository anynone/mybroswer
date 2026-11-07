using CefSharp;

namespace RiBrowser.Privacy;

/// <summary>构造与真实 Chrome 一致的 UA 与 Client Hints（移植自 Polaris ua.js）。</summary>
internal static class UaProfile
{
    // Chrome 官方的 GREASE 品牌轮换集合（Sec-CH-UA 中的 Not-A-Brand 变体），按 major 版本轮换
    private static readonly (string Brand, string Version)[] Grease =
    {
        ("Not A(Brand", "99"),
        ("Not;A=Brand", "99"),
        ("Not_A Brand", "24"),
        ("Not.A/Brand", "8"),
        ("Not)A;Brand", "99"),
    };

    /// <summary>Chromium 完整版本号（补齐四段式，与真实 Chrome UA 形式一致）。</summary>
    public static string ChromeFullVersion()
    {
        var parts = (Cef.ChromiumVersion ?? "").Split('.');
        while (parts.Length < 4)
        {
            Array.Resize(ref parts, parts.Length + 1);
            parts[^1] = "0";
        }
        return string.Join(".", parts, 0, 4);
    }

    public static string ChromeMajor() => ChromeFullVersion().Split('.')[0];

    public static string ChromeUA() =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/"
        + ChromeFullVersion() + " Safari/537.36";

    /// <summary>指定 BCP-47 语言的 Accept-Language（主语言 + 语系回退，与 Chrome 派生规则一致）。
    /// 不能带 q 值：CDP 覆盖与 CEF AcceptLanguageList 会各自追加权重，产生 ";q=0.9;q=0.9" 畸形头。</summary>
    public static string AcceptLanguagesFor(string locale)
    {
        var loc = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
        var primary = loc.Split('-')[0];
        return $"{loc},{primary}";
    }

    /// <summary>与真实 Chrome 完全一致的 userAgentMetadata（Client Hints）。</summary>
    public static Dictionary<string, object> UserAgentMetadata()
    {
        var full = ChromeFullVersion();
        var major = ChromeMajor();
        var g = Grease[int.TryParse(major, out var n) ? n % Grease.Length : 0];
        var (platform, platformVersion) = PlatformInfo();
        return new Dictionary<string, object>
        {
            ["brands"] = new object[]
            {
                new Dictionary<string, object> { ["brand"] = "Chromium", ["version"] = major },
                new Dictionary<string, object> { ["brand"] = "Google Chrome", ["version"] = major },
                new Dictionary<string, object> { ["brand"] = g.Brand, ["version"] = g.Version },
            },
            ["fullVersionList"] = new object[]
            {
                new Dictionary<string, object> { ["brand"] = "Chromium", ["version"] = full },
                new Dictionary<string, object> { ["brand"] = "Google Chrome", ["version"] = full },
                new Dictionary<string, object> { ["brand"] = g.Brand, ["version"] = g.Version },
            },
            ["fullVersion"] = full,
            ["platform"] = platform,
            ["platformVersion"] = platformVersion,
            ["architecture"] = "x86",
            ["model"] = "",
            ["mobile"] = false,
            ["bitness"] = Environment.Is64BitOperatingSystem ? "64" : "32",
            ["wow64"] = false,
        };
    }

    // 与 Chromium 的 UA-CH 映射一致：Win11 → 15.x，Win10 2004+ → 13.x，更老 → 10.x 以下
    private static (string Platform, string PlatformVersion) PlatformInfo()
    {
        var build = Environment.OSVersion.Version.Build;
        var platformVersion = build >= 22000 ? "15.0.0"
            : build >= 19041 ? "13.0.0"
            : build >= 18362 ? "10.0.0"
            : "0.1.0";
        return ("Windows", platformVersion);
    }
}
