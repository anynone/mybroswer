using CefSharp;
using CefSharp.WinForms;
using RiBrowser.Privacy;

namespace RiBrowser;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        PrivacySettings.Current = PrivacySettings.Load();
        UserScripts.Reload();
        AdBlocker.Initialize();
        Console.WriteLine("[adblock] init: enabled=" + AdBlocker.Enabled);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ri-injector.js"), UserScripts.BuildInjector()); } catch { }
        var privacy = PrivacySettings.Current;

        var settings = new CefSettings
        {
            // 独立的磁盘缓存与用户数据目录（Cookie、登录状态会保留）
            CachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RiBrowser", "Cache"),
            // 会话 Cookie（登录时下发、无过期时间）默认只在内存、优雅退出才落盘；
            // 强杀/崩溃会全部丢失 → 表现为"网站登录突然退出"。开启后即时持久化。
            PersistSessionCookies = true,
            // 语言三端对齐（页面 CDP 覆盖 / Worker 原生读取 / HTTP 头）都源自 BrowserLanguage：
            // Worker 的 navigator.languages 不走脚本垫片，只认这里的 AcceptLanguageList。
            // 注意 CEF 不剥离 q 值，列表必须用纯标签（"en-US,en"），否则 Worker 读到 "en;q=0.9"
            Locale = string.IsNullOrEmpty(privacy.BrowserLanguage) ? "zh-CN" : privacy.BrowserLanguage,
            AcceptLanguageList = string.IsNullOrEmpty(privacy.BrowserLanguage)
                ? "zh-CN,zh,en"
                : privacy.BrowserLanguage + "," + privacy.BrowserLanguage.Split('-')[0],
            // 自定义 UA 模式在网络层直接生效；默认模式留空（与内核 Chromium 版本一致）
            UserAgent = privacy.UserAgentMode == "custom" && !string.IsNullOrWhiteSpace(privacy.CustomUserAgent)
                ? privacy.CustomUserAgent.Trim()
                : null,
        };

        // 隐私防护：关闭站点隔离（跨站 iframe 与主框架共享渲染进程，
        // 使进程级时区/语言覆盖直达全部 frame，参考 Android Chrome 默认行为）
        settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");

        // 远程调试端口（--debug-port=9222）：CDP 驱动，用于自动化验证
        var dbg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--debug-port="));
        if (int.TryParse(dbg?["--debug-port=".Length..], out var debugPort))
            settings.CefCommandLineArgs.Add("remote-debugging-port", debugPort.ToString());
        // 浏览器语言伪装需重启后经 --lang 深入 Web Worker
        if (!string.IsNullOrEmpty(privacy.BrowserLanguage))
            settings.CefCommandLineArgs.Add("lang", privacy.BrowserLanguage);

        // 代理：manual 直连用户的 SOCKS/HTTP 端口；singbox 经内置 sing-box 内核中转。
        // CEF 的 proxy-server 只在初始化时生效 → 修改配置需重启浏览器。
        if (RiBrowser.Privacy.ProxyManager.CefProxyServer(privacy) is { } proxyServer)
        {
            settings.CefCommandLineArgs.Add("proxy-server", proxyServer);
            Console.WriteLine("[proxy] CEF proxy-server = " + proxyServer);
        }

        Cef.Initialize(settings);

        // 广告拦截引擎自测（--adtest [URL]）：不启动 UI，直接对样例 URL 跑匹配引擎
        var args = Environment.GetCommandLineArgs();
        // --genproxyconfig：导出当前代理配置到 singbox/config.json（校验/调试用）
        if (args.Contains("--genproxyconfig"))
        {
            var cfg = RiBrowser.Privacy.ProxyManager.GenerateConfigJson(privacy);
            var cfgDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RiBrowser", "singbox");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "config.json"), cfg);
            Console.WriteLine(cfg);
            return;
        }

        if (args.Contains("--adtest"))
        {
            RunAdTest();
            return;
        }

        // DotNetBrowser 集成探针（--dnb-test）
        if (Environment.GetCommandLineArgs().Contains("--dnb-test"))
        {
            DnbSpike.Run();
            return;
        }

        // sing-box 模式：拉起内核（阻塞至本地端口就绪；失败不阻断浏览器，走直连）
        if (RiBrowser.Privacy.ProxyManager.NeedsCore(privacy))
        {
            try { RiBrowser.Privacy.ProxyManager.StartCore(privacy); }
            catch (Exception ex) { Console.WriteLine("[proxy] " + ex.Message); }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 会话恢复：上次异常终止（崩溃/被杀，无 CleanExit 标记）时恢复全部页面；
        // selftest 保持确定性环境，不参与会话恢复
        List<string>? restoreUrls = null;
        var restoreActive = 0;
        if (!args.Contains("--selftest"))
        {
            var (restore, urls, active) = SessionStore.PeekStartup();
            if (restore) { restoreUrls = urls; restoreActive = active; }
        }
        SessionStore.BeginRun(); // 立即写"运行中"状态，本次再崩溃即可被识别

        var main = restoreUrls is { Count: > 0 }
            ? new MainForm(null, restoreUrls, restoreActive)
            : new MainForm();
        Track(main);
        main.Show(); // 无参 Application.Run 不自动显示窗体，需显式 Show
        Application.Run();

        RiBrowser.Privacy.ProxyManager.StopCore(); // 收掉 sing-box 子进程
        Cef.Shutdown();
    }

    // ==================== 多窗口生命周期 ====================
    // 主窗口关闭不再退出进程：只要还有浏览器窗口就继续跑，
    // 最后一个窗口关闭时结束消息循环（与 Chrome 多窗口一致）。
    private static readonly List<System.Windows.Forms.Form> _windows = new();

    /// <summary>当前打开的全部浏览器窗口（标签下拉列表用）。</summary>
    internal static IEnumerable<MainForm> Windows => _windows.OfType<MainForm>();

    private static void Track(System.Windows.Forms.Form form)
    {
        _windows.Add(form);
        SessionStore.Register((MainForm)form);
        form.FormClosed += (s, e) =>
        {
            _windows.Remove(form);
            if (_windows.Count == 0)
            {
                SessionStore.MarkCleanExit(); // 用户主动关闭：下次正常启动
                Application.ExitThread();
            }
        };
    }

    /// <summary>打开新的浏览器窗口（右键「在新窗口中打开链接」等）。</summary>
    internal static void OpenWindow(string? url = null)
    {
        var form = new MainForm(url) { StartPosition = FormStartPosition.CenterParent };
        Track(form);
        form.Show();
    }

    /// <summary>广告拦截引擎自测：覆盖 turtlecute 等测试站的核心用例与常见误杀反例。</summary>
    private static void RunAdTest()
    {
        if (!AdBlocker.Loaded)
        {
            Console.WriteLine("[adtest] 无规则缓存，先同步下载…");
            AdBlocker.EnsureRulesAsync().Wait();
        }
        Console.WriteLine("[adtest] " + AdBlocker.Stats());
        // (期望拦截?, URL, 文档页, 资源类型, 说明)
        var cases = new (bool block, string url, string? parent, CefSharp.ResourceType type, string note)[]
        {
            // turtlecute 脚本测试（内置补充规则）
            (true,  "https://adblock.turtlecute.org/js/pagead.js", "https://adblock.turtlecute.org/", CefSharp.ResourceType.Script, "pagead.js 脚本"),
            (true,  "https://adblock.turtlecute.org/js/widget/ads.js", "https://adblock.turtlecute.org/", CefSharp.ResourceType.Script, "widget/ads.js 脚本"),
            // 经典广告域名（EasyList 域名桶）
            (true,  "https://www.googletagservices.com/tag/js/gpt.js", "https://news.example.com/", CefSharp.ResourceType.Script, "googletagservices"),
            (true,  "https://stats.g.doubleclick.net/r/collect", "https://news.example.com/", CefSharp.ResourceType.Image, "doubleclick"),
            (true,  "https://tpc.googlesyndication.com/safeframe/1-0-40/html/container.html", "https://news.example.com/", CefSharp.ResourceType.SubFrame, "googlesyndication 子帧"),
            (true,  "https://px.ads.linkedin.com/collect?v=2", "https://corp.example.com/", CefSharp.ResourceType.Xhr, "linkedin 埋点"),
            // 不应拦截（误杀反例）
            (false, "https://www.google.com/search?q=adblock", "https://www.google.com/", CefSharp.ResourceType.MainFrame, "google 主文档"),
            (false, "https://example.com/assets/upload.js", "https://example.com/", CefSharp.ResourceType.Script, "upload.js 非广告"),
            (false, "https://s.turtlecute.org/assets/index.js", "https://adblock.turtlecute.org/", CefSharp.ResourceType.Script, "测试站统计脚本"),
            (false, "https://cdn.jsdelivr.net/npm/vue@3/dist/vue.global.js", "https://example.com/", CefSharp.ResourceType.Script, "公共 CDN"),
        };
        var pass = 0;
        foreach (var c in cases)
        {
            var got = AdBlocker.ShouldBlock(c.url, c.parent, c.type);
            var ok = got == c.block;
            if (ok) pass++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  expect={(c.block ? "block" : "allow")} got={(got ? "block" : "allow")}  {c.url}  [{c.note}]");
            if (!ok)
                foreach (var line in AdBlocker.DebugWhy(c.url, c.parent, c.type))
                    Console.WriteLine("        " + line);
        }
        // cosmetic：测试站的选择器必须可用且按域名生效
        var site = AdBlocker.BuildHideScript("adblock.turtlecute.org");
        foreach (var sel in new[] { ".adsbox", ".banner_ads", ".textads" })
            Console.WriteLine($"  {(site.Contains("\"" + sel + "\"") ? "PASS" : "FAIL")}  cosmetic 选择器 {sel} 存在");
        Console.WriteLine($"[adtest] {pass}/{cases.Length} 网络用例通过");

        // 匹配性能：CEF IO 线程上每个请求都要跑一次，必须亚毫秒级
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10000; i++)
        {
            foreach (var c in cases) AdBlocker.ShouldBlock(c.url, c.parent, c.type);
        }
        sw.Stop();
        Console.WriteLine($"[adtest] 性能：{cases.Length * 10000} 次匹配耗时 {sw.ElapsedMilliseconds}ms（平均 {(double)sw.ElapsedTicks / System.Diagnostics.Stopwatch.Frequency * 1_000_000 / (cases.Length * 10000):F1}µs/次）");
        Cef.Shutdown();
    }
}
