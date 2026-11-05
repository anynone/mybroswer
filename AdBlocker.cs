using System.Text.RegularExpressions;

namespace RiBrowser;

/// <summary>
/// 广告拦截引擎（uBlock Origin 能力的原生复刻，无需扩展子系统）：
///   - 网络过滤：IResourceRequestHandler.OnBeforeResourceLoad 按 EasyList 规则阻断请求
///   - 元素隐藏：JS 注入在 DOM 上应用 cosmetic 选择器
/// 规则来源：EasyList（easylist.txt）+ EasyList China，首次启动下载后本地缓存。
/// </summary>
public static class AdBlocker
{
    public static bool Enabled { get; private set; } = true;
    public static long BlockedCount { get; private set; }
    public static event Action? Changed;

    // 已编译的网络过滤规则（按类型分桶减少匹配次数）
    private static readonly List<Regex> _networkRules = new();
    // 通用网络规则（域名匹配）
    private static readonly HashSet<string> _domainRules = new(StringComparer.OrdinalIgnoreCase);
    // 元素隐藏选择器
    private static readonly List<string> _hideSelectors = new();
    private static bool _initialized;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser");
    private static string RulesPath => Path.Combine(Dir, "easylist.txt");

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        if (File.Exists(RulesPath))
        {
            ParseRules(File.ReadAllLines(RulesPath));
            Console.WriteLine($"[adblock] loaded: net={_networkRules.Count} domain={_domainRules.Count} hide={_hideSelectors.Count}");
        }
        else
        {
            Console.WriteLine("[adblock] no rules file, downloading in background");
            Task.Run(DownloadRules);
        }
    }

    /// <summary>后台下载 EasyList + EasyList China 规则并解析。</summary>
    private static async Task DownloadRules()
    {
        var urls = new[]
        {
            "https://easylist.to/easylist/easylist.txt",
            "https://easylist.to/easylistchina/easylistchina.txt",
        };
        var sb = new System.Text.StringBuilder();
        foreach (var url in urls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var text = await http.GetStringAsync(url);
                sb.AppendLine(text);
                Console.WriteLine($"[adblock] downloaded {url} ({text.Length} chars)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[adblock] download failed {url}: {ex.Message}");
            }
        }
        if (sb.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(RulesPath, sb.ToString());
                ParseRules(sb.ToString().Split('\n'));
                Changed?.Invoke();
                Console.WriteLine($"[adblock] rules loaded: net={_networkRules.Count} domain={_domainRules.Count} hide={_hideRulesCount}");
            }
            catch (Exception ex) { Console.WriteLine($"[adblock] save failed: {ex.Message}"); }
        }
    }

    private static int _hideRulesCount;

    /// <summary>
    /// 解析 EasyList 格式规则。只提取常用子集，跳过注释/例外/复杂选项：
    ///   ||domain^         → 域名匹配
    ///   /regex/           → 正则
    ///   ##selector        → 元素隐藏
    ///   通配符 pattern    → 正则
    /// </summary>
    private static void ParseRules(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '!' || line[0] == '[') continue;
            if (line.Contains("@@")) continue; // 例外规则，v1 忽略
            if (line.Contains("$") && !line.StartsWith("##")) line = line[..line.IndexOf('$')].TrimEnd(); // 去掉 $选项

            // 元素隐藏规则：domain##selector 或 ##selector
            if (line.Contains("##"))
            {
                var idx = line.IndexOf("##");
                var selector = line[(idx + 2)..];
                if (selector.Length > 0 && selector.Length < 500 && !selector.Contains("{"))
                    _hideSelectors.Add(selector);
                continue;
            }

            // 域名规则：||example.com^
            if (line.StartsWith("||") && line.EndsWith("^") && !line.Contains('/') && !line.Contains('*'))
            {
                var domain = line[2..^1];
                if (domain.Length > 2 && domain.Contains('.') && !domain.Contains('^'))
                    _domainRules.Add(domain);
                continue;
            }

            // 跳过带复杂选项的规则
            if (line.Contains("||") || line.Contains('*') || line.Contains('^'))
            {
                // 转成正则
                var pattern = Regex.Escape(line)
                    .Replace(@"\|\|", @"https?://[^/]*")
                    .Replace(@"\*", ".*")
                    .Replace(@"\^", @"[/?&=:]");
                try
                {
                    var re = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(2));
                    if (re.ToString().Length < 2000) _networkRules.Add(re);
                }
                catch { /* 过长的跳过 */ }
            }
        }
        _hideRulesCount = _hideSelectors.Count;
    }

    /// <summary>
    /// 判断请求 URL 是否命中拦截规则（在 CEF IO 线程调用，必须快速）。
    /// </summary>
    public static bool ShouldBlock(string url, string? parentUrl)
    {
        if (!Enabled || url.Length == 0) return false;

        try
        {
            // 提取域名快速匹配
            var hostStart = url.IndexOf("://", StringComparison.Ordinal);
            if (hostStart < 0) return false;
            var hostEnd = url.IndexOf('/', hostStart + 3);
            if (hostEnd < 0) hostEnd = url.Length;
            var host = url[(hostStart + 3)..hostEnd];
            var colon = host.IndexOf(':');
            if (colon > 0) host = host[..colon];

            // 精确/后缀域名匹配
            var h = host;
            while (h.Length > 0)
            {
                if (_domainRules.Contains(h)) { BlockedCount++; return true; }
                var dot = h.IndexOf('.');
                if (dot < 0 || dot == h.Length - 1) break;
                h = h[(dot + 1)..];
            }

            // 正则规则（限制尝试次数避免慢请求）
            var checked_count = 0;
            foreach (var re in _networkRules)
            {
                if (++checked_count > 500) break; // 安全上限
                if (re.IsMatch(url)) { BlockedCount++; return true; }
            }
        }
        catch { /* 解析异常放行 */ }
        return false;
    }

    /// <summary>
    /// 生成元素隐藏注入脚本（cosmetic filtering，等价 uBlock 的隐藏过滤）。
    /// </summary>
    public static string BuildHideScript()
    {
        if (!Enabled || _hideSelectors.Count == 0) return "";
        // 去重、限量，生成一段在 DOM Ready 后隐藏匹配元素的 JS
        var selectors = _hideSelectors.Distinct().Take(8000).ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(selectors);
        return $@"(function() {{
  var SEL = {json};
  var css = SEL.join(',\n');
  var style = document.createElement('style');
  style.textContent = css + ' {{ display: none !important; }}';
  (document.head || document.documentElement).appendChild(style);
}})();";
    }

    public static void Toggle()
    {
        Enabled = !Enabled;
        Changed?.Invoke();
    }
}
