using CefSharp;
using CefSharp.Handler;

namespace RiBrowser;

/// <summary>
/// 广告拦截的请求拦截器：按 URL 阻断广告请求（uBlock Origin 网络过滤的原生等价）。
/// 直接挂接在 ChromiumWebBrowser.RequestHandler 上。
/// </summary>
internal sealed class GlobalRequestHandler : RequestHandler
{
    private readonly AdBlockRequestHandler _adBlock = new();

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
        string requestInitiator, ref bool disableDefaultHandling)
    {
        return _adBlock;
    }
}

/// <summary>页面右键菜单：追加「开发者工具」，打开 DevTools 并定位到点击处的元素。</summary>
internal sealed class ContextMenuHandler : IContextMenuHandler
{
    private static readonly CefMenuCommand DevTools =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 1);

    private readonly MainForm _form;
    public ContextMenuHandler(MainForm form) => _form = form;

    public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IContextMenuParams parameters, IMenuModel model)
    {
        if (model.Count > 0) model.AddSeparator();
        model.AddItem(DevTools, "开发者工具  F12");
    }

    public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IContextMenuParams parameters, CefMenuCommand command, CefEventFlags eventFlags)
    {
        if (command == DevTools)
        {
            // 带点击坐标打开：DevTools 会直接选中右键位置的元素（与 Chrome「检查」一致）
            _form.UiShowDevTools(chromiumWebBrowser, parameters.XCoord, parameters.YCoord);
            return true;
        }
        return false;
    }

    public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

    public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model, IRunContextMenuCallback callback) => false;
}

/// <summary>把 target=_blank / window.open 弹窗改为新标签页打开。</summary>
internal sealed class LifeSpanHandler : ILifeSpanHandler
{
    private readonly MainForm _form;
    public LifeSpanHandler(MainForm form) => _form = form;

    public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser) => false;

    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures,
        IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        _form.UiAddTab(targetUrl);
        return true; // 取消默认弹窗，改为新标签页
    }
}

/// <summary>在网页获得焦点时处理浏览器级快捷键（Ctrl+T 等）。</summary>
internal sealed class KeyboardHandler : IKeyboardHandler
{
    private readonly MainForm _form;
    public KeyboardHandler(MainForm form) => _form = form;

    public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int modifiers,
        int key, CefEventFlags eventFlags, bool isSystemKey, ref bool isKeyboardShortcut)
    {
        if (type != KeyType.RawKeyDown && type != KeyType.KeyDown)
            return false;

        var ctrl = (eventFlags & CefEventFlags.ControlDown) != 0;
        var shift = (eventFlags & CefEventFlags.ShiftDown) != 0;
        var alt = (eventFlags & CefEventFlags.AltDown) != 0;

        if (ctrl && !shift && key == (int)Keys.T) { _form.UiAddTab(null); return true; }
        if (ctrl && !shift && key == (int)Keys.W) { _form.UiCloseActiveTab(); return true; }
        if (ctrl && key == (int)Keys.Oemcomma) { _form.UiOpenPrivacySettings(); return true; }
        if (ctrl && !shift && key == (int)Keys.D) { _form.UiToggleBookmark(); return true; }
        if (ctrl && shift && key == (int)Keys.B) { _form.UiToggleBookmarkBar(); return true; }
        if (key == (int)Keys.F12 || (ctrl && shift && key == (int)Keys.I)) { _form.UiShowDevToolsActive(); return true; }
        if ((ctrl && key == (int)Keys.L) || key == (int)Keys.F6) { _form.UiFocusAddress(); return true; }
        if ((ctrl && key == (int)Keys.R) || key == (int)Keys.F5) { _form.UiReload(); return true; }
        if (ctrl && key == (int)Keys.Tab) { _form.UiCycleTab(shift ? -1 : 1); return true; }
        if (alt && key == (int)Keys.Left) { _form.UiBack(); return true; }
        if (alt && key == (int)Keys.Right) { _form.UiForward(); return true; }
        return false;
    }

    public bool OnKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int modifiers,
        int key, CefEventFlags eventFlags, bool isSystemKey) => false;
}

/// <summary>文件下载：用 CEF 自带的另存为对话框，完成后提示。</summary>
internal sealed class DownloadHandler : IDownloadHandler
{
    private readonly MainForm _form;
    public DownloadHandler(MainForm form) => _form = form;

    public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        => true;

    public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        // showDialog:true → CEF 弹出系统“另存为”对话框，路径确认后开始下载
        callback.Continue(null, showDialog: true);
        return true;
    }

    public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
        IDownloadItemCallback callback)
    {
        if (downloadItem.IsComplete)
            _form.ShowMessage("下载完成：" + downloadItem.FullPath);
    }
}
