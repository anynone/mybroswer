using CefSharp;
using CefSharp.Handler;
using System.Windows.Forms;
using RiBrowser.Privacy;

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

/// <summary>权限请求处理：
/// 位置伪装开启时对 Geolocation 请求自动放行——页面能取到的是 CDP 下发的自设坐标，
/// 放行不暴露真实位置；伪装关闭（或坐标未设置、白名单站点）时显式拒绝定位请求，
/// 其余权限类型交还默认（CEF 无权限弹窗 UI，即取消）。</summary>
internal sealed class GeolocationPermissionHandler : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser,
        ulong promptId, string requestingOrigin, PermissionRequestType requestedPermissions,
        IPermissionPromptCallback callback)
    {
        if ((requestedPermissions & PermissionRequestType.Geolocation) == 0) return false;
        var s = PrivacySettings.Current;
        var spoof = s.LocationSpoof && (s.LocationLat != 0 || s.LocationLng != 0)
            && !PrivacyEngine.IsAllowedUrl(requestingOrigin, s);
        callback.Continue(spoof ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
        return true;
    }
}

/// <summary>页面右键菜单：与 Chrome 一致。
/// 链接/图片上下文完全重建菜单（在新标签页/新窗口中打开等），其余上下文保留
/// CEF 默认菜单并追加「开发者工具」（打开后定位到右键位置的元素）。</summary>
internal sealed class ContextMenuHandler : IContextMenuHandler
{
    private static readonly CefMenuCommand DevTools =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 1);
    private static readonly CefMenuCommand OpenLinkTab =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 10);
    private static readonly CefMenuCommand OpenLinkWin =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 11);
    private static readonly CefMenuCommand SaveLink =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 12);
    private static readonly CefMenuCommand CopyLink =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 13);
    private static readonly CefMenuCommand OpenImageTab =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 14);
    private static readonly CefMenuCommand OpenImageWin =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 15);
    private static readonly CefMenuCommand SaveImage =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 16);
    private static readonly CefMenuCommand CopyImage =
        (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 17);

    private readonly MainForm _form;
    public ContextMenuHandler(MainForm form) => _form = form;

    public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IContextMenuParams parameters, IMenuModel model)
    {
        var isLink = !string.IsNullOrEmpty(parameters.LinkUrl);
        var isImage = parameters.MediaType == ContextMenuMediaType.Image;
        if (isLink || isImage)
        {
            // Chrome 同款：链接/图片上下文不出现 后退/前进/刷新 等页面项
            model.Clear();
            if (isLink)
            {
                model.AddItem(OpenLinkTab, "在新标签页中打开链接");
                model.AddItem(OpenLinkWin, "在新窗口中打开链接");
                model.AddSeparator();
                model.AddItem(SaveLink, "将链接另存为...");
                model.AddItem(CopyLink, "复制链接地址");
            }
            if (isImage)
            {
                if (isLink) model.AddSeparator();
                model.AddItem(OpenImageTab, "在新标签页中打开图片");
                model.AddItem(OpenImageWin, "在新窗口中打开图片");
                model.AddSeparator();
                model.AddItem(SaveImage, "将图片另存为...");
                model.AddItem(CopyImage, "复制图片地址");
            }
            model.AddSeparator();
            model.AddItem(DevTools, "开发者工具  F12");
            return;
        }

        if (model.Count > 0) model.AddSeparator();
        model.AddItem(DevTools, "开发者工具  F12");
    }

    public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IContextMenuParams parameters, CefMenuCommand command, CefEventFlags eventFlags)
    {
        var link = parameters.LinkUrl;
        var image = parameters.SourceUrl;
        if (command == DevTools)
        {
            // 带点击坐标打开：DevTools 会直接选中右键位置的元素（与 Chrome「检查」一致）
            _form.UiShowDevTools(chromiumWebBrowser, parameters.XCoord, parameters.YCoord);
            return true;
        }
        if (command == OpenLinkTab)
        {
            // Chrome 惯例：右键菜单打开的新标签页在后台（不切换当前页）
            _form.UiAddTab(link, select: false);
            return true;
        }
        if (command == OpenLinkWin)
        {
            _form.Ui(() => Program.OpenWindow(link));
            return true;
        }
        if (command == SaveLink)
        {
            chromiumWebBrowser.GetBrowser().GetHost().StartDownload(link);
            return true;
        }
        if (command == CopyLink)
        {
            _form.Ui(() => Clipboard.SetText(link));
            return true;
        }
        if (command == OpenImageTab)
        {
            _form.UiAddTab(image, select: false);
            return true;
        }
        if (command == OpenImageWin)
        {
            _form.Ui(() => Program.OpenWindow(image));
            return true;
        }
        if (command == SaveImage)
        {
            chromiumWebBrowser.GetBrowser().GetHost().StartDownload(image);
            return true;
        }
        if (command == CopyImage)
        {
            _form.Ui(() => Clipboard.SetText(image));
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

    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        // CEF 线程回调绝不能让异常逃逸进原生层（未处理即进程崩溃 0xe0434352）
        try
        {
            // Chrome 语义：脚本不能关闭非脚本打开的标签——用户开的标签 window.close() 无效。
            // （claude.ai/login 登录跳转时就会自关，若照办且是最后一个标签 → 整个浏览器退出）
            // 我们托管的标签：取消关闭（忽略）；CEF 原生弹窗（window.open 打开的）：允许自关。
            var isOurTab = _form.IsOurTab(chromiumWebBrowser);
            RiLog.Info($"[life] DoClose url={chromiumWebBrowser.Address} ourTab={isOurTab}" +
                (isOurTab ? "（忽略：脚本不可关用户标签）" : "（原生弹窗，允许关闭）"));
            return !isOurTab; // ourTab → true（取消）；原生弹窗 → false（默认关闭）
        }
        catch (Exception ex)
        {
            RiLog.Error("DoClose", ex);
            return false;
        }
    }


    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures,
        IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        try
        {
        var blank = string.IsNullOrWhiteSpace(targetUrl) ||
            targetUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        var w = popupFeatures?.Width ?? 0;
        var h = popupFeatures?.Height ?? 0;
        var sized = w >= 120 && h >= 120; // 正常窗口尺寸（OAuth 登录窗 ~480×620）
        RiLog.Info($"[life] OnBeforePopup disp={targetDisposition} size={w}x{h} url={targetUrl}");

        // 尺寸明确的真弹窗（OAuth/SSO 登录流：先弹 about:blank 再导航，或带尺寸开 URL）：
        // 必须交给 CEF 创建原生弹窗——window.open 才返回真实窗口，登录页才有
        // opener/postMessage 通道，登录完成后 window.close() 才能自关。
        // 之前一律取消/转标签 → 登录站报 "There was an error logging you in"。
        if (!blank && sized && targetDisposition == WindowOpenDisposition.NewPopup)
            return false;
        if (blank && sized)
            return false;

        // 小尺寸探测窗（如隐私分析站的 10×10 探测）与空地址无尺寸占位：按
        // "弹窗已拦截"静默取消——window.open 返回 null，站点走"已拦截"分支
        if (blank)
            return true;

        // 其余（target=_blank 等）：取消默认弹窗，改为新标签页打开
        _form.UiAddTab(targetUrl);
        return true;
        }
        catch (Exception ex)
        {
            RiLog.Error("OnBeforePopup " + targetUrl, ex);
            return true; // 出错按拦截处理，绝不让异常进原生层
        }
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
        if (ctrl && shift && key == (int)Keys.A) { _form.UiShowTabSearchMenu(); return true; }
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
