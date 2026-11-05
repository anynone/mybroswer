using DotNetBrowser.Browser;
using DotNetBrowser.Engine;
using DotNetBrowser.Extensions;
using DotNetBrowser.Ui;

namespace RiBrowser;

/// <summary>
/// DotNetBrowser 集成探针（评估用，--dnb-test 入口）。
/// 结论：全部关键 API 均存在（引擎/导航/Frame JS/扩展 Install/弹窗 handler/CDP/DevTools），
/// 唯一阻塞点：需要有效 LicenseKey（30 天试用或商业授权），无授权抛 InvalidLicenseException。
/// </summary>
internal static class DnbSpike
{
    internal static void Run()
    {
        var builder = new EngineOptions.Builder
        {
            LicenseKey = "TRIAL",
            UserDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RiBrowser", "DnbProfile"),
            Language = Language.Chinese,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.8078.74 Safari/537.36",
        };
        builder.ChromiumSwitches.Add("--disable-site-isolation-trials");
        var options = builder.Build();

        try
        {
            using var engine = EngineFactory.Create(options);

            // 扩展子系统
            IExtensions extensions = engine.Profiles.Default.Extensions;
            Console.WriteLine("[dnb] extensions count=" + extensions.All.Count);

            // 浏览器 + WinForms 视图 + 导航
            IBrowser browser = engine.CreateBrowser();
            var view = new DotNetBrowser.WinForms.BrowserView();
            view.InitializeFrom(browser);
            browser.Navigation.LoadUrl("https://www.nuget.org").Wait();

            var main = browser.MainFrame;
            Console.WriteLine($"[dnb] url={browser.Url} isMain={main?.IsMain}");

            // DevTools / CDP
            Console.WriteLine("[dnb] devtools: " + browser.DevTools.RemoteDebuggingUrl);

            // 扩展安装（CRX 文件路径）
            // await extensions.Install("path/to/extension.crx");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnb] BLOCKED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

