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
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            // 自定义 UA 模式在网络层直接生效；默认模式留空（与内核 Chromium 版本一致）
            UserAgent = privacy.UserAgentMode == "custom" && !string.IsNullOrWhiteSpace(privacy.CustomUserAgent)
                ? privacy.CustomUserAgent.Trim()
                : null,
        };

        // 隐私防护：关闭站点隔离（跨站 iframe 与主框架共享渲染进程，
        // 使进程级时区/语言覆盖直达全部 frame，参考 Android Chrome 默认行为）
        settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");
        // 浏览器语言伪装需重启后经 --lang 深入 Web Worker
        if (!string.IsNullOrEmpty(privacy.BrowserLanguage))
            settings.CefCommandLineArgs.Add("lang", privacy.BrowserLanguage);

        Cef.Initialize(settings);

        // 广告拦截引擎自测（--adtest [URL]）：不启动 UI，直接对样例 URL 跑匹配引擎
        var args = Environment.GetCommandLineArgs();
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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var main = new MainForm();
        Application.Run(main);

        Cef.Shutdown();
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
