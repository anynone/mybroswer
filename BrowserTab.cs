using CefSharp.WinForms;

namespace RiBrowser;

/// <summary>一个浏览器标签页：标签条上的按钮 + 内容区的 Chromium 控件。</summary>
internal sealed class BrowserTab
{
    public required ChromiumWebBrowser Browser { get; init; }
    public required TabItem TabItem { get; init; }
    public bool IsLoading { get; set; }
}
