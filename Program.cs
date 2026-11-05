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
}
