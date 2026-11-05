using CefSharp;

namespace RiBrowser;

/// <summary>
/// 广告拦截的请求拦截器：在每个标签页上按 URL 阻断广告请求（uBlock Origin 网络过滤的原生等价）。
/// </summary>
internal sealed class AdBlockRequestHandler : IResourceRequestHandler
{
    public ICookieAccessFilter GetCookieAccessFilter(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request) => null!;

    public IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request) => null!;

    public IResponseFilter GetResourceResponseFilter(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, IResponse response) => null!;

    public bool GetOnlyRequestCookies(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
        => false;

    public bool OnProtocolExecution(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
        => false;

    public CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IRequestCallback callback)
    {
        if (AdBlocker.ShouldBlock(request.Url, frame?.Url))
        {
            return CefReturnValue.Cancel;
        }
        return CefReturnValue.Continue;
    }

    public bool OnResourceResponse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response) => false;

    public void OnResourceRedirect(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, ref string newUrl) { }

    public void OnResourceLoadComplete(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, UrlRequestStatus status, long loadedLength) { }

    public void Dispose() { }
}
