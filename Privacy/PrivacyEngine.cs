using System.Text.Json;
using CefSharp;
using CefSharp.Internals;

namespace RiBrowser.Privacy;

/// <summary>
/// 隐私防护引擎（CEF 移植）：
///  - CDP Emulation 覆盖：UA / Accept-Language / Client Hints、页面时区、Intl locale
///    （经 CefThread.ExecuteOnUiThread 在 CEF UI 线程下发）
///  - 垫片注入：IRenderProcessMessageHandler.OnContextCreated（每个 frame 的 V8
///    上下文创建时执行，等价 document_start，先于页面脚本）+ 设置变更时 frame 树 sweep
///  - 白名单：CDP 层按主 frame URL 判定豁免；垫片层按各 frame 自身域名自检
/// </summary>
internal sealed class PrivacyEngine
{
    private readonly MainForm _form;

    public PrivacyEngine(MainForm form) => _form = form;

    // ---------------- 生命周期 ----------------

    /// <summary>标签页创建时挂接：上下文创建注入 + 浏览器就绪/导航后下发 CDP 覆盖。</summary>
    public void Attach(BrowserTab tab)
    {
        // 每个 frame 的 V8 上下文创建时注入垫片（主路径，等价 Electron preload）
        tab.Browser.RenderProcessMessageHandler = new ShimRenderHandler();

        tab.Browser.IsBrowserInitializedChanged += (s, e) =>
        {
            if (tab.Browser.IsBrowserInitialized) _form.Ui(() => Apply(tab));
        };
    }

    /// <summary>主 frame 导航后重判白名单并重下发 CDP 覆盖（垫片已随上下文创建注入）。</summary>
    public void ApplyOnNavigation(BrowserTab tab) => Apply(tab);

    /// <summary>设置变更：所有标签页重下发 CDP 覆盖，并对全部 frame 重注入垫片（即时生效）。</summary>
    public void SettingsChanged(IEnumerable<BrowserTab> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            Apply(tab);
            Sweep(tab);
        }
    }

    public void Detach(BrowserTab tab) { }

    // ---------------- CDP 覆盖 ----------------

    private bool Ready(BrowserTab tab)
    {
        try { return tab.Browser.IsBrowserInitialized && tab.Browser.GetBrowser() != null; }
        catch { return false; }
    }

    private void Apply(BrowserTab tab)
    {
        if (!Ready(tab)) return;
        var s = PrivacySettings.Current;
        var host = tab.Browser.GetBrowser().GetHost();
        var allow = IsAllowedUrl(tab.Browser.Address, s);
        var customUa = s.UserAgentMode == "custom" && !string.IsNullOrWhiteSpace(s.CustomUserAgent);

        // 1) UA / Accept-Language / Client Hints（Emulation.setUserAgentOverride：
        //    同时覆盖 navigator.userAgent、navigator.language(s)、Sec-CH-UA 系列请求头）
        {
            var ua = customUa ? s.CustomUserAgent.Trim() : UaProfile.ChromeUA();
            var payload = new Dictionary<string, object?> { ["userAgent"] = ua };
            if (!customUa)
            {
                payload["userAgentMetadata"] = UaProfile.UserAgentMetadata();
                // 白名单站点用系统语言；配置了语言且非白名单才覆盖
                if (!allow && !string.IsNullOrEmpty(s.BrowserLanguage))
                    payload["acceptLanguage"] = UaProfile.AcceptLanguagesFor(s.BrowserLanguage);
            }
            Send(host, "Emulation.setUserAgentOverride", JsonSerializer.Serialize(payload));
        }

        // 2) 页面时区（timezoneId 传空串即清除覆盖、恢复系统时区）
        {
            var tz = !allow && !string.IsNullOrEmpty(s.Timezone) ? s.Timezone : "";
            Send(host, "Emulation.setTimezoneOverride", JsonSerializer.Serialize(new { timezoneId = tz }));
        }

        // 3) Intl 默认 locale（空对象 = 清除）
        {
            var loc = !allow && !string.IsNullOrEmpty(s.BrowserLanguage) ? s.BrowserLanguage : "";
            Send(host, "Emulation.setLocaleOverride",
                loc.Length > 0 ? JsonSerializer.Serialize(new { locale = loc }) : "{}");
        }
    }

    /// <summary>ExecuteDevToolsMethod 只在 CEF UI 线程有效（否则返回 0 静默丢弃），统一投递。</summary>
    private void Send(IBrowserHost host, string method, string paramsJson)
    {
        CefThread.ExecuteOnUiThread(() =>
        {
            try { host.ExecuteDevToolsMethod(host.GetNextDevToolsMessageId(), method, paramsJson); }
            catch { /* 浏览器关闭中等情况，下次导航会重试 */ }
        });
    }

    // ---------------- 垫片 sweep（设置变更时对已加载页面实时生效） ----------------

    private void Sweep(BrowserTab tab)
    {
        if (!Ready(tab)) return;
        try
        {
            var browser = tab.Browser.GetBrowser();
            var source = ShimSource.Build(PrivacySettings.Current);
            // 重跑垫片 = G[TAG].update(新配置)，各 frame 按自身域名白名单自检
            foreach (var frame in browser.GetAllFrames())
            {
                try { frame.ExecuteJavaScriptAsync(source); }
                catch { }
            }
        }
        catch { }
    }

    internal static bool IsAllowedUrl(string? url, PrivacySettings s)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            var host = new Uri(url).Host;
            return s.IsWhitelisted(host);
        }
        catch { return false; }
    }
}

/// <summary>每个 frame 的 V8 上下文创建时注入垫片脚本与用户脚本（document_start 时机）。</summary>
internal sealed class ShimRenderHandler : IRenderProcessMessageHandler
{
    public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
    {
        try { frame.ExecuteJavaScriptAsync(ShimSource.Build(PrivacySettings.Current)); }
        catch { /* frame 已失效等情况忽略 */ }
        // 用户脚本（扩展替代）只在主 frame 注入，由脚本内 run-at 决定执行时机
        try
        {
            if (frame.IsMain && UserScripts.Items.Count > 0)
            {
                Console.WriteLine("[userscripts] injecting main frame: " + frame.Url);
                frame.ExecuteJavaScriptAsync(UserScripts.BuildInjector());
            }
        }
        catch (Exception ex) { Console.WriteLine("[userscripts] inject fail: " + ex.Message); }
    }

    public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }
    public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node) { }
    public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception) { }
}
