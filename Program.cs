using CefSharp;
using CefSharp.WinForms;
using RiBrowser.Privacy;

namespace RiBrowser;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        RiLog.Info($"[app] RiBrowser 启动 pid={Environment.ProcessId}");
        // WinForms 初始化必须先于任何窗体（含 Clash 门禁窗），否则
        // SetCompatibleTextRenderingDefault 在首个窗口后调用会直接崩溃
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        PrivacySettings.Current = PrivacySettings.Load();
        UserScripts.Reload();
        AdBlocker.Initialize();
        Console.WriteLine("[adblock] init: enabled=" + AdBlocker.Enabled);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ri-injector.js"), UserScripts.BuildInjector()); } catch { }
        var privacy = PrivacySettings.Current;

        // --clashselftest：自管 mihomo 内核端到端自检（本地 profile 当订阅 → 内核 →
        // 控制器/订阅节点/延迟），不进 UI；stdin/stdout 供脚本捕获
        if (Environment.GetCommandLineArgs().Contains("--clashselftest"))
        {
            ClashSelfTest.Run();
            return;
        }

        // clash 模式一律内置 mihomo 内核（不依赖本机 Clash GUI；零节点时带"直连"
        // 占位常驻，控制台/状态链路始终可用），先拉起再进门禁
        if (privacy.ProxyMode == "clash")
        {
            try { ClashCoreManager.Start(privacy); }
            catch (Exception ex)
            {
                RiLog.Error("ClashSelfCore", ex);
                Console.WriteLine("[mihomo] " + ex.Message); // 门禁会给出可见的失败出口
            }
        }

        // Clash 启动门禁：混合端口 → 控制器 → 内网探测都过了才放行浏览；
        // 「仍要进入」以网络锁定态打开（MainForm 禁止全部网页访问直至代理就绪）
        var gateResult = GateResult.Passed;
        if (!Environment.GetCommandLineArgs().Contains("--selftest")
            && privacy.ProxyMode == "clash" && privacy.ClashGateOnStartup)
        {
            gateResult = ClashGate.Run(privacy);
            if (gateResult == GateResult.Aborted) return;
        }

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

        // 测试辅助（--no-popup-block）：关闭 Chromium 弹窗拦截器，CDP 无手势也能触发弹窗流程
        if (Environment.GetCommandLineArgs().Contains("--no-popup-block"))
            settings.CefCommandLineArgs.Add("disable-popup-blocking", "1");

        // 远程调试端口（--debug-port=9222）：CDP 驱动，用于自动化验证
        var dbg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--debug-port="));
        if (int.TryParse(dbg?["--debug-port=".Length..], out var debugPort))
            settings.CefCommandLineArgs.Add("remote-debugging-port", debugPort.ToString());
        // 浏览器语言伪装需重启后经 --lang 深入 Web Worker
        if (!string.IsNullOrEmpty(privacy.BrowserLanguage))
            settings.CefCommandLineArgs.Add("lang", privacy.BrowserLanguage);

        // 代理：manual 直连用户的 SOCKS/HTTP 端口；singbox 经内置 sing-box 内核中转。
        // 经 CEF 'proxy' preference 下发（启动即设一次，此后修改即时生效）。
        // 不能走 --proxy-server 启动参数：命令行设定的 preference 之后无法再被修改
        // （CefSharp 文档明示 + 实测）。
        Cef.Initialize(settings);
        ProxyManager.ApplyCefProxyAsync(privacy).GetAwaiter().GetResult();

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

        // 全局未处理异常兜底：记录后尽量存活（此前 CEF 回调抛异常直接崩溃整个进程）
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            RiLog.Error("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            RiLog.Error("AppDomain.Unhandled" + (e.IsTerminating ? "(fatal)" : ""),
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "?"));
        System.Windows.Forms.Application.ThreadException += (s, e) =>
        {
            RiLog.Error("UI.ThreadException", e.Exception);
        };

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
            ? new MainForm(null, restoreUrls, restoreActive, gateResult == GateResult.Skipped)
            : new MainForm(netLock: gateResult == GateResult.Skipped);
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
            RiLog.Info($"[win] FormClosed 剩余窗口={_windows.Count - 1}");
            _windows.Remove(form);
            if (_windows.Count == 0)
            {
                RiLog.Info("[app] 全部窗口已关 → 退出进程");
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
