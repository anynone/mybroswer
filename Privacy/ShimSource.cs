using System.Reflection;
using System.Text.Json;

namespace RiBrowser.Privacy;

/// <summary>加载内嵌垫片脚本，并把当前隐私配置注入为最终页面脚本。</summary>
internal static class ShimSource
{
    private static readonly Lazy<string> TzTemplate = new(() => Load("RiBrowser.Privacy.Scripts.tz-shim.js"));
    private static readonly Lazy<string> FpTemplate = new(() => Load("RiBrowser.Privacy.Scripts.fp-shim.js"));

    private static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少嵌入资源：" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>tz + fp 垫片合并为一份 document_start 脚本，配置与白名单以 JSON 字面量内嵌。</summary>
    public static string Build(PrivacySettings s)
    {
        var lang = s.BrowserLanguage ?? "";
        var langs = lang.Length > 0 ? new[] { lang, lang.Split('-')[0] } : Array.Empty<string>();
        var fpCfg = new Dictionary<string, object?>
        {
            ["locale"] = lang,
            ["langs"] = langs,
            ["font"] = s.FontGuard,
            ["canvas"] = s.CanvasNoise,
            ["webrtc"] = s.WebrtcGuard,
            ["seed"] = SeedInt(s.FingerprintSeed),
            ["uad"] = s.UserAgentMode != "custom" ? UaProfile.UserAgentMetadata() : null,
        };
        var allow = JsonSerializer.Serialize(s.PrivacyAllowlist);
        return TzTemplate.Value
            .Replace("__RIB_TZ__", JsonSerializer.Serialize(s.Timezone ?? ""))
            .Replace("__RIB_ALLOW__", allow)
            + "\n"
            + FpTemplate.Value
            .Replace("__RIB_FP_CFG__", JsonSerializer.Serialize(fpCfg))
            .Replace("__RIB_ALLOW__", allow);
    }

    /// <summary>字符串种子 → 稳定 int（FNV-1a，永不为 0）。</summary>
    public static int SeedInt(string? seed)
    {
        if (string.IsNullOrEmpty(seed)) return 20260101;
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in seed) { h ^= c; h *= 16777619; }
            return (int)(h | 1);
        }
    }
}
