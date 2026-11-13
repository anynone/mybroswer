using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.Callback;
using CefSharp.Enums;

namespace RiBrowser.Privacy;

/// <summary>
/// clash://console 自定义 scheme 处理器：控制台的全部请求在进程内应答，
/// 不开任何本地 TCP 端口。路由：
///   静态文件   —— metacubexd 面板产物（ClashPanel.ServeStatic，index.html
///                 注入 WS shim 与「手动节点配置」入口）；
///   /config.js —— 动态生成（defaultBackendURL=clash://console + 面板语言）；
///   /config*   —— 手动节点编辑页（GET）与保存（POST）；
///   /__ws/*    —— WS 桥：clash:// scheme 不支持 WebSocket（CEF 收不到升级
///                 请求，new WebSocket("clash://…") 直接抛错），面板侧 shim 把
///                 WebSocket 换成 clash://console/__ws 指令，帧由本桥持
///                 ClientWebSocket（ClashApi.ConnectWsAsync）转接并回推页面；
///   控制器路径 —— REST 反代：方法/查询/请求体原样转发 mihomo 控制器
///                 （ClashApi.SendControllerAsync，进程内选择内置内核/verge 管道）。
/// </summary>
public sealed class ClashSchemeHandlerFactory : ISchemeHandlerFactory
{
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        // browser/frame 包装只在本次调用内有效（CEF 约束），能持有的只有标识值
        => new ClashSchemeHandler(browser.Identifier);
}

internal sealed class ClashSchemeHandler : IResourceHandler
{
    private readonly int _browserId;
    private byte[] _body = Array.Empty<byte>();
    private string _mime = "text/plain; charset=utf-8";
    private int _status = 200;
    private MemoryStream? _stream;

    public ClashSchemeHandler(int browserId) => _browserId = browserId;

    public bool Open(IRequest request, out bool handleRequest, ICallback callback)
    {
        // IRequest 只在本次调用内有效（CEF 约束），异步处理前先拷走所需数据
        var url = request.Url;
        var method = request.Method;
        var contentType = TryHeader(request, "Content-Type");
        var body = ReadBody(request);

        handleRequest = false; // 异步：字段就绪后 callback.Continue()
        _ = Task.Run(async () =>
        {
            try { await HandleAsync(url, method, body, contentType); }
            catch (Exception ex)
            {
                RiLog.Error("Panel", ex);
                _status = 500;
                _body = Encoding.UTF8.GetBytes("控制台内部错误：" + ex.Message);
            }
            callback.Continue();
            callback.Dispose();
        });
        return true;
    }

    public void GetResponseHeaders(IResponse response, out long responseLength, out string redirectUrl)
    {
        redirectUrl = null!;
        responseLength = _body.Length;
        // CEF 的 MimeType 只认裸类型（带 "; charset=…" 会被当纯文本渲染）；
        // 字符集由各内容自身的 meta charset / 默认 UTF-8 规则决定
        response.MimeType = _mime.Split(';', 2)[0].Trim();
        // 面板文件会被就地打补丁（PatchPanelDist），不能让 CEF 拿缓存的旧字节；
        // Headers 索引赋值不生效，必须整体替换集合
        var headers = new System.Collections.Specialized.NameValueCollection
        {
            ["Cache-Control"] = "no-cache",
        };
        response.Headers = headers;
        response.StatusCode = _status;
        response.StatusText = _status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            502 => "Bad Gateway",
            _ => "",
        };
    }

    public bool Read(Stream dataOut, out int bytesRead, IResourceReadCallback callback)
    {
        bytesRead = 0;
        callback.Dispose();
        _stream ??= new MemoryStream(_body, writable: false);
        var buf = new byte[(int)Math.Min(dataOut.Length, 64 * 1024)];
        bytesRead = _stream.Read(buf, 0, buf.Length);
        if (bytesRead > 0) dataOut.Write(buf, 0, bytesRead);
        return bytesRead > 0;
    }

    public bool Skip(long bytesToSkip, out long bytesSkipped, IResourceSkipCallback callback)
    {
        callback.Dispose();
        if (_stream == null || !_stream.CanSeek)
        {
            bytesSkipped = -2; // CEF 约定：负值表示跳过失败
            return false;
        }
        _stream.Seek(bytesToSkip, SeekOrigin.Current);
        bytesSkipped = bytesToSkip;
        return true;
    }

    public void Cancel()
    {
        // CEF 对 Cancel 的线程模型无保证，资源统一在 Read 流结束/对象回收时释放
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    // 已废弃的旧接口成员（CEF 只会走 Open/Read），与官方 ResourceHandler 一律抛出
    bool IResourceHandler.ProcessRequest(IRequest request, ICallback callback)
        => throw new NotImplementedException();

    bool IResourceHandler.ReadResponse(Stream dataOut, out int bytesRead, ICallback callback)
        => throw new NotImplementedException();

    // ---------------- 路由 ----------------

    private async Task HandleAsync(string url, string method, byte[]? body, string? contentType)
    {
        var s = PrivacySettings.Current;
        Uri u;
        try { u = new Uri(url); }
        catch
        {
            _status = 400;
            _body = Encoding.UTF8.GetBytes("bad url");
            return;
        }
        var path = u.AbsolutePath;
        var pathAndQuery = path + u.Query;

        // ① WS 桥指令
        if (path.StartsWith("/__ws/", StringComparison.Ordinal))
        {
            var (status, json) = await ClashWsBridge.HandleAsync(_browserId, path["/__ws/".Length..],
                body ?? Array.Empty<byte>());
            _status = status;
            _mime = "application/json; charset=utf-8";
            _body = Encoding.UTF8.GetBytes(json);
            return;
        }

        // ② 面板后端免配置 + 语言覆盖
        if (path == "/config.js")
        {
            _mime = "text/javascript; charset=utf-8";
            _body = Encoding.UTF8.GetBytes(ClashPanel.BuildConfigJs(s));
            return;
        }

        // ③ 手动节点编辑页
        if (path == "/config")
        {
            _mime = "text/html; charset=utf-8";
            _body = ClashPanel.ConfigPage();
            return;
        }
        if (path == "/config/save" && method == "POST")
        {
            var (st, bytes) = ClashPanel.SaveConfig(body);
            _status = st;
            _mime = "application/json; charset=utf-8";
            _body = bytes;
            return;
        }

        // ④ 控制器 REST 反代（原样转发，密钥由 ClashApi 统一附加）
        if (IsApi(path))
        {
            var result = await ClashApi.SendControllerAsync(s, method, pathAndQuery, body, contentType);
            if (result == null)
            {
                _status = 502;
                _body = Encoding.UTF8.GetBytes("mihomo 控制器不可达");
                return;
            }
            _status = result.Value.Status;
            _mime = result.Value.ContentType ?? "application/json";
            _body = result.Value.Body;
            // TUN 自动提权：PATCH 成功不代表 TUN 真起来了（无权创建网卡时内核
            // 静默失败、运行态仍是关）——成功响应先行返回，提权流程后台进行
            if (result.Value.Status == 204 && method == "PATCH" && path == "/configs")
                _ = MaybeElevateForTunAsync(s, body);
            return;
        }

        // ⑤ 面板静态文件
        (_body, _mime) = ClashPanel.ServeStatic(path);
    }

    // 控制器路径前缀（其余路径按面板静态文件处理）
    private static readonly string[] ApiPrefixes =
    {
        "/version", "/configs", "/proxies", "/rules", "/connections", "/traffic",
        "/logs", "/memory", "/group", "/providers", "/dns", "/restart", "/upgrade",
        "/cache", "/gc", "/websocket",
    };

    private static bool IsApi(string path) => ApiPrefixes.Any(p => path == p || path.StartsWith(p + "/"));

    // ---------------- TUN 自动提权 ----------------

    /// <summary>并发闸：提权流程（UAC 等用户应答）同时只跑一个。</summary>
    private static readonly SemaphoreSlim TunGate = new(1, 1);

    /// <summary>PATCH /configs 成功后的 TUN 跟进：开 TUN 但内核无权创建网卡
    /// （未提权运行的 mihomo）→ 自动提权重启（UAC，配置已带 TUN 块）；
    /// 关 TUN → 回写设置。后台执行，不阻塞面板响应。</summary>
    private static async Task MaybeElevateForTunAsync(PrivacySettings s, byte[]? body)
    {
        bool? tunOn = null;
        try
        {
            if (body is { Length: > 0 })
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("tun", out var tun)
                    && tun.TryGetProperty("enable", out var en)
                    && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    tunOn = en.GetBoolean();
            }
        }
        catch { /* 非 JSON/非 TUN 修改：不跟进 */ }
        if (tunOn == null) return;

        if (tunOn == false)
        { // 内核运行态已由 PATCH 关掉，只需把持久化偏好收回
            if (s.ClashTunOn) { s.ClashTunOn = false; s.Save(); }
            return;
        }

        if (!await TunGate.WaitAsync(0)) return; // 已有提权流程在跑
        try
        {
            await Task.Delay(1200); // 给内核一点时间应用/失败
            var on = await GetTunEnabledAsync(s);
            if (on != false)
            { // true=运行态已开（内核提权或本就有效）；null=控制器不可达，不动
                if (on == true)
                {
                    if (!s.ClashTunOn) { s.ClashTunOn = true; s.Save(); }
                    await ClashCoreManager.EnsureTunRoutesAsync(s); // 偶发路由漏装自愈
                }
                return;
            }

            // 运行态仍是关：无权创建 TUN 网卡 → 提权重启（UAC 由用户应答，
            // runas 阻塞到应答为止；配置落盘已含 TUN，起来即生效）
            s.ClashTunOn = true;
            s.Save();
            RiLog.Info("[mihomo] TUN 需要管理员权限，请求提权重启（UAC）…");
            try
            {
                await Task.Run(() => ClashCoreManager.RestartElevated(s));
                await ClashCoreManager.EnsureTunRoutesAsync(s);
            }
            catch (Exception ex)
            { // 用户取消 UAC 等：偏好收回，内核维持原状
                RiLog.Info("[mihomo] TUN 提权重启未完成：" + ex.Message);
                s.ClashTunOn = false;
                s.Save();
            }
        }
        finally { TunGate.Release(); }
    }

    /// <summary>查询内核运行态的 TUN 开关；控制器不可达返回 null。</summary>
    private static async Task<bool?> GetTunEnabledAsync(PrivacySettings s)
    {
        try
        {
            var r = await ClashApi.SendControllerAsync(s, "GET", "/configs");
            if (r is not { Status: 200 }) return null;
            using var doc = JsonDocument.Parse(r.Value.Body);
            if (!doc.RootElement.TryGetProperty("tun", out var tun)
                || ! tun.TryGetProperty("enable", out var en)) return null;
            return en.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string? TryHeader(IRequest request, string name)
    {
        try { return request.GetHeaderByName(name); }
        catch { return null; }
    }

    private static byte[]? ReadBody(IRequest request)
    {
        try
        {
            var post = request.PostData;
            if (post == null) return null;
            MemoryStream? ms = null;
            foreach (var el in post.Elements)
            {
                // 流式/Request 体 CEF 不带给 scheme handler（PostData 为空）——
                // 页面 shim 已在 fetch 出口物化成缓冲体，这里只见 Bytes
                if (el.Type == PostDataElementType.File) continue;
                ms ??= new MemoryStream();
                ms.Write(el.Bytes);
            }
            return ms?.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// WS 桥的 C# 侧：面板 shim 的 open/close/send 指令建连收发，
/// mihomo 的帧经 ExecuteJavaScriptAsync 推回页面 shim（__riWs.onFrame 等）。
/// 推送按 browserId 经 MainForm 从真实标签页控件解析 IBrowser（scheme 处理器
/// 拿到的 IBrowser/IFrame 包装都是调用作用域内的短命对象，不可持有）；
/// 浏览器/页面不在（窗口关闭、跳走）即拆除通道。
/// </summary>
internal static class ClashWsBridge
{
    private sealed class Channel
    {
        public required int Id;
        public required int BrowserId;
        /// <summary>底层连接；控制器连不上时为 null（通道即刻作废，只为发一次 onError）。</summary>
        public required WebSocket? Ws;
    }

    private static readonly ConcurrentDictionary<int, Channel> Channels = new();

    public static async Task<(int Status, string Json)> HandleAsync(int browserId, string cmd, byte[] body)
    {
        int id = 0;
        string? url = null, data = null;
        try
        {
            using var doc = JsonDocument.Parse(body.Length > 0 ? body : Encoding.UTF8.GetBytes("{}"));
            if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number) id = i.GetInt32();
            if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
            if (doc.RootElement.TryGetProperty("data", out var d)) data = d.GetString();
        }
        catch { return (400, "{\"ok\":false,\"error\":\"bad request\"}"); }
        if (id <= 0) return (400, "{\"ok\":false,\"error\":\"bad id\"}");

        switch (cmd)
        {
            case "open":
                await OpenAsync(browserId, id, url);
                return (200, "{\"ok\":true}");
            case "close":
                if (Channels.TryRemove(id, out var ch) && ch.Ws != null)
                    _ = ch.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client", CancellationToken.None);
                return (200, "{\"ok\":true}");
            case "send":
                if (Channels.TryGetValue(id, out var ch2) && ch2.Ws is { State: WebSocketState.Open } && data != null)
                    await ch2.Ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(data)),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                return (200, "{\"ok\":true}");
        }
        return (404, "{\"ok\":false}");
    }

    private static async Task OpenAsync(int browserId, int id, string? url)
    {
        // 面板按 http→ws 换协议拼出来的地址（clash://console/traffic?token=…）：
        // 只取路径+查询，连到 mihomo 控制器（密钥由 ClashApi 附加，token 参数被忽略）
        string pathAndQuery;
        try { pathAndQuery = new Uri(url ?? "").PathAndQuery; }
        catch
        {
            // url 非法：页面上已无对应流，只能放弃
            return;
        }

        var ws = await ClashApi.ConnectWsAsync(PrivacySettings.Current, pathAndQuery);
        var ch = new Channel { Id = id, BrowserId = browserId, Ws = ws };
        Channels[id] = ch;
        Push(ch, ws == null ? $"window.__riWs.onError({id})" : $"window.__riWs.onOpen({id})");
        if (ws == null)
        {
            Channels.TryRemove(id, out _);
            return;
        }
        _ = Task.Run(() => PumpAsync(id));
    }

    private static async Task PumpAsync(int id)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (Channels.TryGetValue(id, out var ch) && ch.Ws is { State: WebSocketState.Open })
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await ch.Ws!.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("closed by controller");
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);

                // 文本帧整体到齐再回推（分片帧拼半截 JSON 面板解不开）
                Push(ch, $"window.__riWs.onFrame({id},{JsonSerializer.Serialize(sb.ToString())})");
            }
        }
        catch
        {
            // 断连/取消：走 finally 通知
        }
        finally
        {
            if (Channels.TryRemove(id, out var ch))
            {
                try { ch.Ws?.Dispose(); } catch { /* 已断 */ }
                Push(ch, $"window.__riWs.onClose({id},1006,'')");
            }
        }
    }

    private static void CloseChannel(int id)
    {
        if (Channels.TryRemove(id, out var ch))
        {
            try { ch.Ws?.Dispose(); } catch { /* 已断 */ }
        }
    }

    /// <summary>向发起来源浏览器的主框架执行 JS（fire-and-forget）。浏览器/框架
    /// 已不在（窗口关闭、页面跳走）时拆除通道并中断底层 WS。
    /// ExecuteJavaScriptAsync 必须在 CEF UI 线程调用（其他线程静默丢弃），且只能
    /// 用从 ChromiumWebBrowser 解析出的持久 IBrowser（scheme 处理器在 Create 里
    /// 拿到的包装在调用返回后即被释放，只可取其 Identifier 值）。</summary>
    private static void Push(Channel ch, string js)
    {
        try
        {
            Cef.UIThreadTaskFactory.StartNew(() =>
            {
                try
                {
                    if (!Channels.ContainsKey(ch.Id)) return;
                    var ib = MainForm.FindBrowserById(ch.BrowserId);
                    var frame = ib?.MainFrame;
                    if (frame == null || frame.IsDisposed)
                    { // 浏览器/页面已不在（窗口关闭、跳走）→ 拆通道中断底层 WS
                        if (Channels.TryRemove(ch.Id, out _))
                            try { ch.Ws.Abort(); } catch { /* 已断 */ }
                        return;
                    }
                    frame.ExecuteJavaScriptAsync(js);
                }
                catch { /* CEF 关闭竞态 */ }
            });
        }
        catch { /* Cef 已关闭 */ }
    }
}
