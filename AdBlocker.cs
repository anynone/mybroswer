using System.Text.RegularExpressions;
using CefSharp;

namespace RiBrowser;

/// <summary>
/// 广告拦截引擎（uBlock Origin 能力的原生复刻，无需扩展子系统）：
///   - 网络过滤：IResourceRequestHandler.OnBeforeResourceLoad 按 EasyList 规则阻断请求
///   - 元素隐藏：document_start 注入 CSS，按站点域名过滤 cosmetic 选择器
/// 规则来源：EasyList + EasyList China（GitHub raw 源，3 天自动刷新）+ 内置补充规则。
/// 匹配分桶：域名精确（HashSet）→ 域名锚定（||domain^rest）→ 通用规则（合并正则块）；
/// 支持 @@ 例外、$domain=、$third-party、$script/$image 等类型选项。
/// </summary>
public static class AdBlocker
{
    public static bool Enabled { get; private set; } = true;
    public static long BlockedCount { get; private set; }
    public static event Action? Changed;

    // ---------------- 规则模型 ----------------

    private class NetRule
    {
        public string Pattern = "";                 // 原始 pattern（调试用）
        public Regex? Re;                           // 含通配符/锚点的规则
        public string? Literal;                     // 纯字面量（url.Contains 命中）
        public HashSet<ResourceType>? Types;        // $script 等；null = 任意类型（主文档除外）
        public bool Popup;                          // $popup：允许拦截主文档导航
        public bool ThirdParty;                     // $third-party
        public bool FirstParty;                     // $~third-party
        public HashSet<string>? IncludeDomains;     // $domain=a,b（按文档域名匹配）
        public HashSet<string>? ExcludeDomains;     // $domain=~a
    }

    /// <summary>通用规则的合并正则块：合并正则粗筛 + 块内逐条确认，避免每请求遍历数万条。</summary>
    private sealed class RuleChunk
    {
        public Regex Combined = null!;
        public List<NetRule> Rules = new();
    }

    private sealed class RuleSet
    {
        public HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase);                   // ||domain^
        public Dictionary<string, List<NetRule>> ByDomain = new(StringComparer.OrdinalIgnoreCase); // ||domain^rest
        public List<RuleChunk> Chunks = new();                                                   // 其余通用规则
        public int Count;

        public bool MatchAny(List<NetRule>? rules, string url, string host,
            string? parentHost, ResourceType type) => rules is not null &&
            rules.Any(r => MatchRule(r, url, host, parentHost, type));

        public bool MatchChunks(string url, string host, string? parentHost, ResourceType type)
        {
            foreach (var chunk in Chunks)
            {
                bool hit;
                try { hit = chunk.Combined.IsMatch(url); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!hit) continue;
                foreach (var r in chunk.Rules)
                    if (MatchRule(r, url, host, parentHost, type)) return true;
            }
            return false;
        }
    }

    private sealed class HideRule
    {
        public string Selector = "";
        public string[]? IncludeDomains;
        public string[]? ExcludeDomains;
    }

    private static RuleSet _block = new();
    private static RuleSet _allow = new();
    private static List<HideRule> _hide = new();
    private static bool _initialized;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser");
    private static string RulesPath => Path.Combine(Dir, "easylist.txt");

    // 规则五件套：EasyList + EasyPrivacy + EasyList China + Turtlecute d3host + HaGeZi Pro
    // （uBlock 默认订阅等价 + DNS 级全域覆盖，堵住移动广告联盟/归因追踪类域名）。
    // 每个列表按顺序尝试多个源，避免单点 404（easylist.to 的 China 路径已失效，走 GitHub raw）。
    private static readonly string[][] Sources =
    {
        new[] { "https://easylist.to/easylist/easylist.txt", "https://raw.githubusercontent.com/easylist/easylist/master/easylist.txt" },
        new[] { "https://easylist.to/easylist/easyprivacy.txt" },
        new[] { "https://raw.githubusercontent.com/easylist/easylistchina/master/easylistchina.txt", "https://easylist.to/easylistchina/easylistchina.txt" },
        new[] { "https://adblock.turtlecute.org/d3host.adblock" },
        new[] { "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/pro.txt" },
    };

    /// <summary>缓存版本标记：列表组合变化时递增，旧缓存自动刷新。</summary>
    private const string CacheMarker = "! Ri-lists: v5 (easylist + easyprivacy + easylistchina + d3host + hagezi-pro)";

    /// <summary>内置补充：常见第一方路径广告模式 + 经典广告类名选择器 + 追踪域名精选。
    /// 平台 API 基础设施（graph.facebook / tagmanager / googlevideo 等）只按
    /// $image,ping,xmlhttprequest 限拦——favicon/追踪像素探测会被拦截，
    /// 登录组件与站点功能脚本不受影响。</summary>
    private const string BuiltinRules = """
        ! Ri 内置补充规则
        /pagead.js^$script
        /widget/ads.$script
        /adsbygoogle.^$script
        /adframe.^$script
        /adserver.js^$script
        ##.adsbox
        ##.banner_ads
        ##.textads
        ##.ad-slot
        ##.ad-banner
        ! —— 归因 / 移动广告追踪（纯追踪域，整域拦）
        ||appsflyer.com^
        ||adjust.com^
        ||kochava.com^
        ||impact.com^
        ||inmobi.com^
        ||quantcast.com^
        ||quantserve.com^
        ||dynamicyield.com^
        ||skimresources.com^
        ||viglink.com^
        ||mineralt.io^
        ||juicyads.com^
        ||list-manage.com^
        ||bnc.lt^
        ||tvinteractive.tv^
        ! —— 同意管理 / CMP（等同 AdGuard Cookie Notices 行为）
        ||cookielaw.org^
        ||cookiebot.com^
        ||privacy-mgmt.com^
        ||privacy-center.org^
        ||trustarc.com^
        ||onetrust.com^
        ||usercentrics.eu^
        ! —— 平台 API 基础设施（限拦图片/信标/XHR 探测，不破坏功能脚本）
        ||tagmanager.google.com^$image,ping,xmlhttprequest
        ||redirector.googlevideo.com^$image,ping,xmlhttprequest
        ||dai.google.com^$image,ping,xmlhttprequest
        ||graph.facebook.com^$image,ping,xmlhttprequest
        ||connect.facebook.net^$image,ping,xmlhttprequest
        ||i.instagram.com^$image,ping,xmlhttprequest
        ||graph.instagram.com^$image,ping,xmlhttprequest
        ||snap.licdn.com^$image,ping,xmlhttprequest
        ||xp.apple.com^$image,ping,xmlhttprequest
        ||s.youtube.com^$image,ping,xmlhttprequest
        ||youtubei.googleapis.com^$image,ping,xmlhttprequest
        ||cdn.optimizely.com^$image,ping,xmlhttprequest
        ||widget.intercom.io^$image,ping,xmlhttprequest
        ||analytics.twitter.com^$image,ping,xmlhttprequest
        ||ssl.p.jwpcdn.com^$image,ping,xmlhttprequest
        ||greatis.com^$image,ping,xmlhttprequest
        ||2giga.link^$image,ping,xmlhttprequest
        ||vk.com^$image,ping
        ! —— 厂商遥测
        ||vortex-win.data.microsoft.com^
        ||settings-win.data.microsoft.com^
        ||watson.telemetry.microsoft.com^
        ||ngfts.lge.com^
        ||us.ibs.lgappstv.com^
        """;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        if (File.Exists(RulesPath))
        {
            var text = File.ReadAllText(RulesPath);
            LoadRules(text);
            Console.WriteLine($"[adblock] loaded: block={_block.Count} allow={_allow.Count} hide={_hide.Count} [{Stats()}]");
            // 缓存过期（列表 Expires 4 天）或版本标记不匹配（列表组合已变化）→ 后台刷新
            var stale = (DateTime.UtcNow - File.GetLastWriteTimeUtc(RulesPath)).TotalDays > 3;
            if (stale || !text.StartsWith(CacheMarker, StringComparison.Ordinal))
            {
                Console.WriteLine("[adblock] rules stale or outdated cache format, refreshing in background");
                _ = EnsureRulesAsync();
            }
        }
        else
        {
            Console.WriteLine("[adblock] no rules file, downloading in background");
            _ = EnsureRulesAsync();
        }
    }

    private static Task? _downloadTask;
    private static readonly object _downloadLock = new();

    /// <summary>触发规则下载（幂等：并发/重复调用共享同一次下载）。</summary>
    public static Task EnsureRulesAsync()
    {
        lock (_downloadLock)
        {
            _downloadTask ??= DownloadRules();
            return _downloadTask;
        }
    }

    /// <summary>是否已加载到规则。</summary>
    public static bool Loaded => _block.Count > 0;

    /// <summary>后台下载全部规则列表（每个列表任一源成功即算成功），完成后热替换。</summary>
    private static async Task DownloadRules()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var candidates in Sources)
        {
            foreach (var url in candidates)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var text = await http.GetStringAsync(url);
                    if (text.Contains("adblock", StringComparison.OrdinalIgnoreCase) && text.Length > 2000)
                    {
                        sb.AppendLine(text);
                        Console.WriteLine($"[adblock] downloaded {url} ({text.Length} chars)");
                        break;
                    }
                    Console.WriteLine($"[adblock] suspicious content from {url}, skipped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[adblock] download failed {url}: {ex.Message}");
                }
            }
        }
        if (sb.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var rules = CacheMarker + "\n" + sb;
                File.WriteAllText(RulesPath, rules);
                LoadRules(rules);
                Changed?.Invoke();
                Console.WriteLine($"[adblock] rules reloaded: block={_block.Count} allow={_allow.Count} hide={_hide.Count}");
            }
            catch (Exception ex) { Console.WriteLine($"[adblock] save failed: {ex.Message}"); }
        }
    }

    private static void LoadRules(string text)
    {
        var block = new RuleSet();
        var allow = new RuleSet();
        var hide = new List<HideRule>();
        ParseRules(text.Split('\n'), block, allow, hide);
        ParseRules(BuiltinRules.Split('\n'), block, allow, hide);
        _block = block; _allow = allow; _hide = hide; // 引用替换是原子的
        _hideCache.Clear();
    }

    // ---------------- 规则解析 ----------------

    private static void ParseRules(string[] lines, RuleSet block, RuleSet allow, List<HideRule> hide)
    {
        var genericBlock = new List<NetRule>();
        var genericAllow = new List<NetRule>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '!' || line[0] == '[') continue;
            // #@# 反隐藏 / #$# #?# #%# 扩展语法：不支持；## 开头的通用选择器保留
            if (line.Length > 1 && line[0] == '#' && line[1] != '#') continue;

            var isAllow = line.StartsWith("@@");
            if (isAllow) line = line[2..];

            // 元素隐藏：domain##selector（先于 $ 选项分离，选择器里可能含 $）
            var sep = line.IndexOf("##", StringComparison.Ordinal);
            if (sep >= 0)
            {
                if (isAllow) continue; // #@# 反隐藏，v1 不支持
                AddHideRule(hide, line[..sep], line[(sep + 2)..]);
                continue;
            }

            // 分离 $选项
            string pattern = line, options = "";
            var dollar = line.IndexOf('$');
            if (dollar == 0) continue;
            if (dollar > 0)
            {
                pattern = line[..dollar];
                options = line[(dollar + 1)..];
            }
            if (pattern.Length == 0) continue;

            var rule = BuildRule(pattern);
            if (rule is null) continue;
            if (options.Length > 0 && !ParseOptions(options, rule)) continue;

            var target = isAllow ? allow : block;
            if (rule.AnchorDomain is not null && rule.Re is null && rule.Literal is null)
            {
                // 带限制选项（$domain=/$third-party/$script…）的整域规则不能进无差别域名桶，
                // 走 ByDomain 桶由选项约束（Literal="" 表示桶内 URL 恒命中）
                var restricted = rule.Types is not null || rule.ThirdParty || rule.FirstParty ||
                                 rule.IncludeDomains is not null || rule.ExcludeDomains is not null;
                if (restricted) rule.Literal = "";
                else { target.Domains.Add(rule.AnchorDomain); continue; }
            }
            if (rule.AnchorDomain is not null)
            {
                if (!target.ByDomain.TryGetValue(rule.AnchorDomain, out var list))
                    target.ByDomain[rule.AnchorDomain] = list = new List<NetRule>();
                list.Add(rule);
            }
            else
            {
                (isAllow ? genericAllow : genericBlock).Add(rule);
            }
        }

        block.Chunks.AddRange(BuildChunks(genericBlock));
        allow.Chunks.AddRange(BuildChunks(genericAllow));
        block.Count = block.Domains.Count + block.ByDomain.Values.Sum(l => l.Count) + genericBlock.Count;
        allow.Count = allow.Domains.Count + allow.ByDomain.Values.Sum(l => l.Count) + genericAllow.Count;
    }

    private sealed class ParsedPattern : NetRule
    {
        public string? AnchorDomain; // ||domain 锚定的域名
    }

    /// <summary>把 pattern 编译为字面量或正则；|| 开头的额外提取域名锚。</summary>
    private static ParsedPattern? BuildRule(string pattern)
    {
        var p = new ParsedPattern { Pattern = pattern };

        if (pattern.StartsWith("||"))
        {
            var body = pattern[2..];
            var stop = body.IndexOfAny(new[] { '/', '^', '*' });
            var domain = (stop < 0 ? body : body[..stop]).TrimEnd('^', '*', '.');
            if (domain.Length < 3 || !domain.Contains('.') || domain.Contains('^') || domain.Contains('*'))
                return null; // || 后不是合法域名，放弃
            p.AnchorDomain = domain;
            if (stop < 0 || (stop == body.Length - 1 && body[^1] == '^'))
                return p; // 纯整域规则：无字面量无正则，进 Domains 桶
            var re = WildcardToRegex("||" + domain + body[stop..], true, false);
            if (re is null) return null;
            p.Re = re;
            return p;
        }

        var startAnchor = false;
        if (pattern.StartsWith('|')) { startAnchor = true; pattern = pattern[1..]; }
        var endAnchor = false;
        if (pattern.EndsWith('|') && pattern.Length > 1) { endAnchor = true; pattern = pattern[..^1]; }
        if (pattern.Length == 0) return null;

        if (pattern.Contains('*') || pattern.Contains('^') || startAnchor || endAnchor)
        {
            var re = WildcardToRegex(pattern, startAnchor, endAnchor);
            if (re is null) return null;
            p.Re = re;
        }
        else
        {
            p.Literal = pattern; // 纯字面量：url.Contains 命中
        }
        return p;
    }

    /// <summary>ABP 通配符转正则：* → .*，^ → 分隔符或结尾，|| → 协议+任意子域，| → 行锚。
    /// 无锚 pattern 是子串语义（匹配 URL 任意位置），不加外层 ^ $。</summary>
    private static Regex? WildcardToRegex(string pattern, bool startAnchor, bool endAnchor)
    {
        var sb = new System.Text.StringBuilder(startAnchor ? "^" : "");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '|' && i == 0 && pattern.Length > 1 && pattern[1] == '|')
            {
                sb.Append("https?://(?:[^./]+\\.)*");
                i++;
            }
            else if (c == '|' && i == 0) { /* startAnchor 已处理 */ }
            else if (c == '|') sb.Append('$');
            else if (c == '*') sb.Append(".*");
            else if (c == '^') sb.Append("(?:[/?&=;:]|$)");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        if (endAnchor) sb.Append('$');
        try
        {
            return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }
        catch { return null; }
    }

    /// <summary>解析 $ 选项；返回 false 表示该规则含不支持的语义，应跳过。</summary>
    private static bool ParseOptions(string options, NetRule rule)
    {
        foreach (var raw in options.Split(','))
        {
            var opt = raw.Trim();
            if (opt.Length == 0) continue;
            switch (opt)
            {
                case "match-case": break;
                case "third-party": rule.ThirdParty = true; break;
                case "~third-party": rule.FirstParty = true; break;
                case "popup": rule.Popup = true; break;
                case "badfilter":
                case "genericblock":
                case "generichide":
                case "elemhide":
                case "specifichide":
                    return false; // 不支持的语义，跳过避免误伤
                default:
                    if (opt.StartsWith("domain=", StringComparison.Ordinal))
                    {
                        foreach (var d in opt[7..].Split('|'))
                        {
                            var name = d.Trim();
                            if (name.Length == 0) continue;
                            if (name.StartsWith('~'))
                            {
                                name = name[1..];
                                if (name.Length == 0) continue;
                                (rule.ExcludeDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                                    .Add(name.ToLowerInvariant());
                            }
                            else
                            {
                                (rule.IncludeDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                                    .Add(name.ToLowerInvariant());
                            }
                        }
                        break;
                    }
                    if (opt.StartsWith('~')) return false; // ~script 等反向类型，v1 不支持
                    var type = MapType(opt);
                    if (type is null) return false;        // 未知选项 → 保守跳过
                    (rule.Types ??= new HashSet<ResourceType>()).Add(type.Value);
                    break;
            }
        }
        // $popup 语义 = 拦主文档导航（window.open 弹窗的加载就是 MainFrame 请求）
        if (rule.Popup && rule.Types is null)
            (rule.Types ??= new HashSet<ResourceType>()).Add(ResourceType.MainFrame);
        return true;
    }

    private static ResourceType? MapType(string opt) => opt switch
    {
        "script" => ResourceType.Script,
        "image" => ResourceType.Image,
        "stylesheet" => ResourceType.Stylesheet,
        "subdocument" => ResourceType.SubFrame,
        "xmlhttprequest" or "xhr" => ResourceType.Xhr,
        "object" or "object-subrequest" => ResourceType.Object,
        "media" => ResourceType.Media,
        "font" => ResourceType.FontResource,
        "ping" or "beacon" => ResourceType.Ping,
        "document" or "main-frame" => ResourceType.MainFrame,
        "other" => ResourceType.SubResource,
        "websocket" => ResourceType.Xhr, // 近似映射：ws 握手按 Xhr 处理
        _ => null,
    };

    private static readonly string[] InvalidSelectorTokens =
    {
        ":-abp-", ":has-text(", ":matches-css", ":xpath(", ":style(", ":nth-ancestor(",
        ":matches-path(", ":regexp(", ":if(", ":if-not(", ":shadow(", "##", "{", "=>",
    };

    private static void AddHideRule(List<HideRule> hide, string domainPart, string selector)
    {
        selector = selector.Trim();
        if (selector.Length is < 2 or > 400) return;
        if (selector.StartsWith("#") || selector.StartsWith("^")) return; // extended-css 片段
        if (InvalidSelectorTokens.Any(t => selector.Contains(t, StringComparison.OrdinalIgnoreCase))) return;

        string[]? inc = null; string[]? exc = null;
        if (domainPart.Length > 0)
        {
            var i = new List<string>(); var e = new List<string>();
            foreach (var d in domainPart.Split(','))
            {
                var name = d.Trim().ToLowerInvariant();
                if (name.Length == 0) continue;
                if (name.StartsWith('~')) e.Add(name[1..]);
                else i.Add(name);
            }
            if (i.Count > 0) inc = i.ToArray();
            if (e.Count > 0) exc = e.ToArray();
        }
        hide.Add(new HideRule { Selector = selector, IncludeDomains = inc, ExcludeDomains = exc });
    }

    private static IEnumerable<RuleChunk> BuildChunks(List<NetRule> rules)
    {
        const int size = 200;
        for (var start = 0; start < rules.Count; start += size)
        {
            var part = rules.Skip(start).Take(size).ToList();
            var parts = part.Select(r => r.Literal is not null
                ? Regex.Escape(r.Literal)
                : StripAnchors(r.Re!.ToString())).ToList();
            Regex combined;
            try
            {
                combined = new Regex("(?:" + string.Join("|", parts) + ")",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
            }
            catch { continue; }
            yield return new RuleChunk { Combined = combined, Rules = part };
        }
    }

    /// <summary>合并块复用已编译规则的源文本，去掉首尾锚（块级命中后由原规则精确确认）。</summary>
    private static string StripAnchors(string re) =>
        (re.StartsWith('^') ? re[1..] : re).TrimEnd('$');

    // ---------------- 匹配 ----------------

    private static bool MatchRule(NetRule r, string url, string host, string? parentHost, ResourceType type)
    {
        // 无类型选项的规则不拦主文档（避免整页打不开）；$popup/$document 显式允许
        if (type == ResourceType.MainFrame && r.Types is null && !r.Popup) return false;
        if (r.Types is not null && !r.Types.Contains(type)) return false;

        if (r.ThirdParty || r.FirstParty)
        {
            var third = IsThirdParty(host, parentHost);
            if (r.ThirdParty && !third) return false;
            if (r.FirstParty && third) return false;
        }
        if (r.IncludeDomains is not null || r.ExcludeDomains is not null)
        {
            if (parentHost is null) return false; // 无法验证文档域名 → 不应用
            if (r.IncludeDomains is not null && !DomainMatches(r.IncludeDomains, parentHost)) return false;
            if (r.ExcludeDomains is not null && DomainMatches(r.ExcludeDomains, parentHost)) return false;
        }
        return r.Literal is not null
            ? url.Contains(r.Literal, StringComparison.OrdinalIgnoreCase)
            : SafeMatch(r.Re!, url);
    }

    private static bool SafeMatch(Regex re, string url)
    {
        try { return re.IsMatch(url); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool IsThirdParty(string host, string? parentHost)
    {
        if (string.IsNullOrEmpty(parentHost) || parentHost == host) return false;
        return !host.EndsWith("." + parentHost, StringComparison.OrdinalIgnoreCase) &&
               !parentHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DomainMatches(IEnumerable<string> domains, string host)
    {
        foreach (var d in domains)
            if (host == d || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>提取 URL 的 host（小写）；失败返回 null。</summary>
    private static string? HostOf(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.Length == 0 ? null : host.ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>host 及其全部父域（a.b.c.com → a.b.c.com / b.c.com / c.com）。</summary>
    private static List<string> Suffixes(string host)
    {
        var list = new List<string>();
        var h = host;
        list.Add(h);
        while (true)
        {
            var dot = h.IndexOf('.');
            if (dot < 0 || dot == h.Length - 1) break;
            h = h[(dot + 1)..];
            if (h.Length == 0) break;
            list.Add(h);
        }
        return list;
    }

    /// <summary>判断请求是否命中拦截规则（CEF IO 线程调用，必须快速）。</summary>
    public static bool ShouldBlock(string url, string? parentUrl, ResourceType type = ResourceType.SubResource)
    {
        if (!Enabled || url.Length == 0) return false;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var host = HostOf(url);
            if (host is null) return false;
            var parentHost = parentUrl is null ? null : HostOf(parentUrl);
            var block = _block; var allow = _allow; // 快照（热替换时无撕裂）
            var suffixes = Suffixes(host);

            // 1) 例外优先（@@ 规则赢过阻断规则）
            foreach (var h in suffixes)
                if (allow.Domains.Contains(h)) return false;
            foreach (var h in suffixes)
                if (allow.ByDomain.TryGetValue(h, out var list) &&
                    allow.MatchAny(list, url, host, parentHost, type)) return false;
            if (allow.MatchChunks(url, host, parentHost, type)) return false;

            // 2) 域名精确桶
            foreach (var h in suffixes)
                if (block.Domains.Contains(h)) { BlockedCount++; return true; }

            // 3) 域名锚定桶 + 4) 通用合并块
            foreach (var h in suffixes)
                if (block.ByDomain.TryGetValue(h, out var list) &&
                    block.MatchAny(list, url, host, parentHost, type)) { BlockedCount++; return true; }
            if (block.MatchChunks(url, host, parentHost, type)) { BlockedCount++; return true; }
        }
        catch { /* 解析异常放行 */ }
        return false;
    }

    // ---------------- 元素隐藏 ----------------

    /// <summary>
    /// 生成元素隐藏注入脚本（cosmetic filtering，等价 uBlock 的隐藏过滤）。
    /// 每个选择器独立成一条 CSS 规则：非法选择器只会被浏览器丢弃自身，不会连坐整块。
    /// 按域名缓存构建结果，避免每个 frame 重复序列化上万条选择器。
    /// </summary>
    public static string BuildHideScript(string? host)
    {
        var h = host?.ToLowerInvariant() ?? "";
        return _hideCache.GetOrAdd(h, BuildHideScriptCore);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _hideCache = new();

    private static string BuildHideScriptCore(string h)
    {
        if (!Enabled || _hide.Count == 0) return "";
        var selectors = new List<string>(_hide.Count);
        foreach (var r in _hide)
        {
            if (r.IncludeDomains is not null && (h.Length == 0 || !DomainMatches(r.IncludeDomains, h))) continue;
            if (r.ExcludeDomains is not null && h.Length > 0 && DomainMatches(r.ExcludeDomains, h)) continue;
            selectors.Add(r.Selector);
        }
        if (selectors.Count == 0) return "";
        var json = System.Text.Json.JsonSerializer.Serialize(selectors);
        return @"(function() {
  var css = " + json + @".map(function(s) { return s + ' { display: none !important; }'; }).join('\n');
  function apply() {
    var style = document.createElement('style');
    style.setAttribute('data-ri-adblock', '1');
    style.textContent = css;
    (document.head || document.documentElement).appendChild(style);
  }
  if (document.documentElement) apply();
  else document.addEventListener('DOMContentLoaded', apply);
})();";
    }

    /// <summary>调试/自测：当前规则统计。</summary>
    public static string Stats() =>
        $"domains={_block.Domains.Count} anchored={_block.ByDomain.Values.Sum(l => l.Count)} " +
        $"generic-chunks={_block.Chunks.Count} allow={_allow.Count} hide={_hide.Count}";

    /// <summary>调试：逐阶段说明一个 URL 为何放行/拦截（--adtest 用）。</summary>
    public static List<string> DebugWhy(string url, string? parentUrl, ResourceType type)
    {
        var log = new List<string>();
        var host = HostOf(url)!;
        var parentHost = parentUrl is null ? null : HostOf(parentUrl);
        var suffixes = Suffixes(host);
        log.Add($"host={host} parent={parentHost ?? "(none)"} suffixes=[{string.Join(",", suffixes)}] type={type}");

        foreach (var h in suffixes)
            if (_allow.Domains.Contains(h)) { log.Add($"allow.Domain 命中: {h}"); return log; }
        foreach (var h in suffixes)
            if (_allow.ByDomain.TryGetValue(h, out var list))
                foreach (var r in list)
                    if (MatchRule(r, url, host, parentHost, type))
                    { log.Add($"allow.ByDomain 命中: {h} → {r.Pattern}"); return log; }
        foreach (var chunk in _allow.Chunks)
            try
            {
                if (!chunk.Combined.IsMatch(url)) continue;
                foreach (var r in chunk.Rules)
                    if (MatchRule(r, url, host, parentHost, type))
                    { log.Add($"allow.Chunk 命中: {r.Pattern}"); return log; }
            }
            catch (RegexMatchTimeoutException) { }

        foreach (var h in suffixes)
            if (_block.Domains.Contains(h)) { log.Add($"block.Domain 命中: {h}"); return log; }
        foreach (var h in suffixes)
            if (_block.ByDomain.TryGetValue(h, out var list))
                foreach (var r in list)
                    if (MatchRule(r, url, host, parentHost, type))
                    { log.Add($"block.ByDomain 命中: {h} → {r.Pattern}"); return log; }
        foreach (var chunk in _block.Chunks)
            try
            {
                if (!chunk.Combined.IsMatch(url)) continue;
                foreach (var r in chunk.Rules)
                    if (MatchRule(r, url, host, parentHost, type))
                    { log.Add($"block.Chunk 命中: {r.Pattern}"); return log; }
            }
            catch (RegexMatchTimeoutException) { }

        log.Add("无规则命中 → 放行");
        return log;
    }

    public static void Toggle()
    {
        Enabled = !Enabled;
        _hideCache.Clear();
        Changed?.Invoke();
    }
}
