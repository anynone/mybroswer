using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RiBrowser.Privacy;

/// <summary>一个代理节点/分组的最小视图（来自 GET /proxies）。</summary>
/// <param name="Name">节点或分组名。</param>
/// <param name="Type">"Selector"/"URLTest"/"Fallback"/"Direct"… 分组才有 All。</param>
/// <param name="Now">分组当前选中节点名。</param>
/// <param name="All">分组成员列表（节点为空列表）。</param>
/// <param name="DelayMs">最近一次延迟测试结果（ms；0=无记录）。</param>
internal sealed record ClashProxy(string Name, string Type, string? Now, List<string> All, int DelayMs);

/// <summary>GET /configs 的关键字段。</summary>
internal sealed record ClashConfig(string Mode, int MixedPort);

/// <summary>一条活动连接的最小视图（GET /connections）：通道徽标按域名对账用。</summary>
/// <param name="Host">目标域名（host 为空时回退 destinationIP）。</param>
/// <param name="Chains">代理链（内层节点在外，如 ["节点出口", "分组名"]）。</param>
/// <param name="Rule">命中规则（如 Match / DomainSuffix + payload）。</param>
internal sealed record ClashConnection(string Host, List<string> Chains, string Rule);

/// <summary>
/// mihomo（Clash 内核）REST 控制器客户端——浏览器深度集成 Clash 的地基：
/// 换节点（PUT /proxies/{group}）、切模式（PATCH /configs）、测速
/// （GET /proxies/{name}/delay）、连接/订阅等全部能力都在这套 API 上。
/// 传输自动选择（实测 Verge Rev 默认关闭 HTTP 控制器）：
/// ① 枚举 Windows 命名管道 verge-mihomo-*（Verge 与内核的私有通道，同套 REST API，
///    管道名按 sidecar/production 模式变化、配置文件里的会过期，必须枚举现存的）；
/// ② 没有管道时回退 HTTP 控制器（ClashApiBase，自建内核/其他 GUI 场景）。
/// 密钥优先用设置值，留空时从 Verge 运行时配置只读发现。
/// </summary>
internal static class ClashApi
{
    private static readonly HttpClient Http = new()
    {
        // 控制器在本机，正常毫秒级返回；探测失败要快速让位（门禁/状态轮询）
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static (string Pipe, HttpClient Client)? _pipeClient;

    /// <summary>面板反代用：原样转发任意控制器请求，返回 (状态码, Content-Type, 响应体)。
    /// 控制器不可达返回 null。</summary>
    public static async Task<(int Status, string? ContentType, byte[] Body)?> SendControllerAsync(
        PrivacySettings s, string method, string pathAndQuery, byte[]? body = null, string? contentType = null)
    {
        try
        {
            using var resp = await SendAsync(s, new HttpMethod(method), pathAndQuery, body, contentType);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return ((int)resp.StatusCode,
                resp.Content.Headers.ContentType?.MediaType is { } mt ? mt + (resp.Content.Headers.ContentType.CharSet is { } cs ? "; " + cs : "") : null,
                bytes);
        }
        catch { return null; }
    }

    /// <summary>建立控制器的 WebSocket 连接（面板实时流 /traffic /connections /logs）：
    /// 管道模式手工完成 HTTP/1.1 升级握手再交给 WebSocket.CreateFromStream；
    /// 无管道时用 ClientWebSocket 走 HTTP 控制器。失败返回 null。</summary>
    public static async Task<System.Net.WebSockets.WebSocket?> ConnectWsAsync(
        PrivacySettings s, string pathAndQuery)
    {
        // ① 内置内核控制器（clash 模式一律自管）
        if (ClashCoreManager.CoreRunning)
        {
            try
            {
                var ws = new System.Net.WebSockets.ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + s.ClashSelfSecret);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await ws.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{s.ClashSelfControllerPort}{pathAndQuery}"), cts.Token);
                return ws;
            }
            catch { return null; }
        }

        // ② 命名管道：HTTP/1.1 Upgrade 握手（逐字节读响应头，避免吞掉 WS 帧）
        var pipeName = DiscoverPipe();
        if (pipeName != null)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2500);
                var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                var secret = EffectiveSecret(s);
                var handshake =
                    $"GET {pathAndQuery} HTTP/1.1\r\nHost: localhost\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
                    (secret.Length > 0 ? $"Authorization: Bearer {secret}\r\n" : "") + "\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(handshake);
                await pipe.WriteAsync(reqBytes, 0, reqBytes.Length);

                var head = new List<byte>();
                var b = new byte[1];
                while (head.Count < 8192)
                {
                    var n = await pipe.ReadAsync(b, 0, 1);
                    if (n == 0) break;
                    head.Add(b[0]);
                    var h = head.Count;
                    if (h >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r')
                        break;
                }
                var statusLine = Encoding.ASCII.GetString(head.TakeWhile(c => c != '\n').ToArray());
                if (!statusLine.Contains("101"))
                {
                    await pipe.DisposeAsync();
                }
                else
                {
                    return System.Net.WebSockets.WebSocket.CreateFromStream(pipe,
                        new System.Net.WebSockets.WebSocketCreationOptions { IsServer = false });
                }
            }
            catch { /* 落到 HTTP 控制器 */ }
        }

        // ② HTTP 控制器（自管内核优先于用户配置）
        try
        {
            var ws = new System.Net.WebSockets.ClientWebSocket();
            var self = ClashCoreManager.CoreRunning;
            var secret2 = self ? s.ClashSelfSecret : s.ClashApiSecret.Trim();
            if (secret2.Length > 0)
                ws.Options.SetRequestHeader("Authorization", "Bearer " + secret2);
            var httpBase = self
                ? $"http://127.0.0.1:{s.ClashSelfControllerPort}"
                : Base(s);
            var wsBase = httpBase.Replace("https://", "wss://").Replace("http://", "ws://");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await ws.ConnectAsync(new Uri(wsBase + pathAndQuery), cts.Token);
            return ws;
        }
        catch { return null; }
    }

    /// <summary>verge 管道是否存在（外部内核存活判定，自管内核让位判断用）。</summary>
    public static bool VergePipeAlive() => DiscoverPipe() != null;

    /// <summary>枚举本机存活的 verge-mihomo-* 管道（内核退出时消失，天然反映存活）。</summary>
    private static string? DiscoverPipe()
    {
        try
        {
            const string prefix = @"\\.\pipe\";
            return System.IO.Directory.GetFiles(prefix)
                .Select(p => p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p)
                .FirstOrDefault(n => n.StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>命名管道上的 REST 客户端（HTTP/1.1 over NamedPipeClientStream：
    /// 自定义流无 ALPN，须强制 1.1——默认 HTTP/2 prior-knowledge 内核端不答）。</summary>
    private static HttpClient? PipeClient()
    {
        var pipe = DiscoverPipe();
        if (pipe == null) return null;
        if (_pipeClient is { } pc && pc.Pipe == pipe) return pc.Client;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await s.ConnectAsync(2500, ct);
                return s;
            },
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = new Uri("http://localhost"),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        _pipeClient = (pipe, client);
        return client;
    }

    /// <summary>Bearer 密钥：设置值优先；否则只读 Verge 运行时配置（external-controller
    /// 关闭时 secret 也写在那里）。</summary>
    private static string EffectiveSecret(PrivacySettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ClashApiSecret)) return s.ClashApiSecret.Trim();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml");
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("secret:", StringComparison.Ordinal))
                    return line["secret:".Length..].Trim();
        }
        catch { /* 无 Verge 配置 → 无密钥（自建内核场景） */ }
        return "";
    }

    private static string Base(PrivacySettings s) => s.ClashApiBase.Trim().TrimEnd('/');

    /// <summary>统一请求入口。控制器选择链：内置内核控制器（clash 模式一律自管，
    /// 见 ClashCoreManager）→ verge 管道（密钥自动发现）→ 用户配置的 HTTP 控制器。
    /// 调用方负责 using 响应。</summary>
    private static async Task<HttpResponseMessage> SendAsync(PrivacySettings s, HttpMethod method, string path, object? body = null, string? contentType = null)
    {
        var self = ClashCoreManager.CoreRunning;
        var pipeClient = self ? null : PipeClient();
        var url = self
            ? $"http://127.0.0.1:{s.ClashSelfControllerPort}" + path
            : pipeClient != null ? "http://localhost" + path : Base(s) + path;
        var secret = self
            ? s.ClashSelfSecret
            : pipeClient != null ? EffectiveSecret(s) : s.ClashApiSecret.Trim();
        var req = new HttpRequestMessage(method, url);
        if (secret.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (body is byte[] raw)
        { // 面板反代的原样字节：JsonSerializer 会把 byte[] 编成 Base64 字符串，内核解不出直接 400
            req.Content = new ByteArrayContent(raw);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType ?? "application/json");
        }
        else if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await (pipeClient ?? Http).SendAsync(req);
    }

    private static async Task<JsonElement?> GetJsonAsync(PrivacySettings s, string path)
    {
        try
        {
            using var resp = await SendAsync(s, HttpMethod.Get, path);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        }
        catch { return null; } // 控制器不在/超时/密钥错误：统一表现为"不可达"
    }

    /// <summary>控制器是否可达（GET /version）。</summary>
    public static async Task<string?> GetVersionAsync(PrivacySettings s) =>
        (await GetJsonAsync(s, "/version"))?.GetProperty("version").GetString();

    /// <summary>当前模式（rule/global/direct）与混合端口。</summary>
    public static async Task<ClashConfig?> GetConfigsAsync(PrivacySettings s)
    {
        var j = await GetJsonAsync(s, "/configs");
        if (j == null) return null;
        var mode = j.Value.TryGetProperty("mode", out var m) ? m.GetString() : null;
        var port = j.Value.TryGetProperty("mixed-port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;
        return new ClashConfig(mode ?? "", port);
    }

    /// <summary>全部活动连接（通道徽标数据源）。控制器不可达返回 null。</summary>
    public static async Task<List<ClashConnection>?> GetConnectionsAsync(PrivacySettings s)
    {
        var j = await GetJsonAsync(s, "/connections");
        if (j == null || !j.Value.TryGetProperty("connections", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<ClashConnection>();
        foreach (var c in arr.EnumerateArray())
        {
            var host = "";
            if (c.TryGetProperty("metadata", out var m))
            {
                if (m.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } hs) host = hs;
                else if (m.TryGetProperty("destinationIP", out var ip)) host = ip.GetString() ?? "";
            }
            var chains = new List<string>();
            if (c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array)
                chains = ch.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            var rule = c.TryGetProperty("rule", out var r) ? r.GetString() ?? "" : "";
            if (c.TryGetProperty("rulePayload", out var rp) && rp.GetString() is { Length: > 0 } payload)
                rule += " · " + payload;
            if (host.Length > 0) list.Add(new ClashConnection(host, chains, rule));
        }
        return list;
    }

    /// <summary>全部节点与分组。分组成员列表在 "all"，最近延迟在 "history" 末尾。</summary>
    public static async Task<List<ClashProxy>?> GetProxiesAsync(PrivacySettings s)
    {
        var j = await GetJsonAsync(s, "/proxies");
        if (j == null || !j.Value.TryGetProperty("proxies", out var map)) return null;

        var list = new List<ClashProxy>();
        foreach (var kv in map.EnumerateObject())
        {
            var v = kv.Value;
            var type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var now = v.TryGetProperty("now", out var n) ? n.GetString() : null;
            var all = v.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>();
            var delay = 0;
            if (v.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array && h.GetArrayLength() > 0)
            {
                var last = h[h.GetArrayLength() - 1];
                if (last.TryGetProperty("delay", out var d) && d.ValueKind == JsonValueKind.Number)
                    delay = d.GetInt32();
            }
            list.Add(new ClashProxy(kv.Name, type, now, all, delay));
        }
        return list;
    }

    /// <summary>切换分组当前节点（运行时动作，不写配置文件）。</summary>
    public static async Task<bool> SelectAsync(PrivacySettings s, string group, string name)
    {
        try
        {
            using var resp = await SendAsync(s, HttpMethod.Put, "/proxies/" + Uri.EscapeDataString(group),
                new Dictionary<string, object> { ["name"] = name });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>切换模式："rule" / "global" / "direct"。</summary>
    public static async Task<bool> SetModeAsync(PrivacySettings s, string mode)
    {
        try
        {
            using var resp = await SendAsync(s, new HttpMethod("PATCH"), "/configs",
                new Dictionary<string, object> { ["mode"] = mode });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>经指定节点/分组测一个 URL 的延迟（mihomo 自己发起请求，走完整隧道）。
    /// 失败（超时/节点不通）返回 null。门禁用它探测"内网真的可达"。</summary>
    public static async Task<int?> DelayAsync(PrivacySettings s, string proxyName, string url, int timeoutMs = 4000)
    {
        try
        {
            var path = $"/proxies/{Uri.EscapeDataString(proxyName)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
            using var resp = await SendAsync(s, HttpMethod.Get, path);
            if (!resp.IsSuccessStatusCode) return null;
            var j = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            return j.TryGetProperty("delay", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt32() : null;
        }
        catch { return null; }
    }

    /// <summary>挑"主分组"：模式为 global 时用 GLOBAL 的当前节点；否则取第一个
    /// 非 GLOBAL 的 Selector 分组（GLOBAL 在 mihomo 里也是 Selector，须排除；
    /// 订阅配置惯例第一个真分组即出站主组）。</summary>
    public static string? MainProxy(List<ClashProxy> proxies, string mode)
    {
        if (mode == "global")
            return proxies.FirstOrDefault(p => p.Name == "GLOBAL")?.Now;
        var g = proxies.FirstOrDefault(p => p.Name != "GLOBAL"
            && p.Type is "Selector" or "URLTest" or "Fallback" && p.All.Count > 0);
        return g?.Now ?? g?.All.FirstOrDefault();
    }

    /// <summary>启动/拉起 Clash Verge 后等待控制器就绪（最多 waitMs）。</summary>
    public static async Task<bool> WaitControllerAsync(PrivacySettings s, int waitMs)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await GetVersionAsync(s) != null) return true;
            await Task.Delay(400);
        }
        return false;
    }
}
