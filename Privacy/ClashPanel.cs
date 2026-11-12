using System.IO.Compression;
using System.Net;

namespace RiBrowser.Privacy;

/// <summary>
/// 浏览器内 Clash 控制台（metacubexd 面板）：
/// 本地回环 HTTP 服务承载面板静态文件，并把面板发起的控制器请求（含
/// /traffic /connections /logs 等 WebSocket 实时流）反代到 mihomo——
/// 管道模式下远端面板根本连不上内核，必须本地反代。
/// 面板免配置：metacubexd 启动时读 config.js 里的 defaultBackendURL，
/// 服务端启动时按当前端口生成它（端口每次启动取空闲值）。
/// 仅回环监听 + 随机端口；上游密钥由反代统一附加，面板侧无需填。
/// </summary>
internal static class ClashPanel
{
    private static HttpListener? _listener;
    private static string _distDir = "";

    /// <summary>面板基址（http://127.0.0.1:port）；未启动为 null。</summary>
    public static string? BaseUrl { get; private set; }

    /// <summary>控制台编辑页保存手动节点后回调（MainForm 挂 Ui(ApplyProxyNow)：
    /// 校验 → 写设置 → 内置内核重启生效 → 锁定自动解除）。</summary>
    public static Action? OnManualSaved;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "panel");

    /// <summary>确保面板服务已启动（幂等）。面板文件缺失时先下载（直连失败走
    /// 本地 Clash 混合端口）。失败返回 null。</summary>
    public static async Task<string?> EnsureStartedAsync(PrivacySettings s)
    {
        if (BaseUrl != null) return BaseUrl;
        if (!await EnsureDistAsync(s)) return null;

        var port = 0;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        BaseUrl = $"http://127.0.0.1:{port}";
        WriteConfig(s);
        RiLog.Info("[panel] 控制台就绪 " + BaseUrl);
        _ = Task.Run(() => LoopAsync(s, listener));
        return BaseUrl;
    }

    /// <summary>生成面板 config.js：后端免配置 + 面板语言覆盖（面板按
    /// navigator.language 选语言，会被浏览器防指纹的 en-US 伪装带偏成英文；
    /// 在应用包加载前覆盖之）。端口/语言变化后调用即生效。</summary>
    private static void WriteConfig(PrivacySettings s)
    {
        var lang = s.ClashPanelLang.Trim();
        File.WriteAllText(Path.Combine(_distDir, "config.js"),
            "window.__METACUBEXD_CONFIG__ = {\n" +
            $"  defaultBackendURL: '{BaseUrl}',\n" +
            "  githubToken: '',\n}\n" +
            (lang.Length > 0
                ? $"Object.defineProperty(navigator, 'language', {{ get: function () {{ return '{lang}'; }} }});\n" +
                  $"Object.defineProperty(navigator, 'languages', {{ get: function () {{ return ['{lang}']; }} }});\n"
                : ""));
    }

    /// <summary>设置变更（面板语言）后重写 config.js；面板未启动则由下次启动写入。</summary>
    public static void RefreshConfig(PrivacySettings s)
    {
        if (BaseUrl == null || _distDir.Length == 0) return;
        try { WriteConfig(s); }
        catch (Exception ex) { RiLog.Error("PanelCfg", ex); }
    }

    private static async Task LoopAsync(PrivacySettings s, HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // Stop()
            _ = Task.Run(() => HandleAsync(s, ctx));
        }
    }

    // 控制器路径前缀（其余路径按面板静态文件处理）
    private static readonly string[] ApiPrefixes =
    {
        "/version", "/configs", "/proxies", "/rules", "/connections", "/traffic",
        "/logs", "/memory", "/group", "/providers", "/dns", "/restart", "/upgrade",
        "/cache", "/gc", "/websocket",
    };

    private static bool IsApi(string path) => ApiPrefixes.Any(p => path == p || path.StartsWith(p + "/"));

    private static async Task HandleAsync(PrivacySettings s, HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var pathAndQuery = path + ctx.Request.Url?.Query;

            // 浏览器内手动节点编辑页（控制台右下角入口直达）：
            // 探测不通锁定时自动打开控制台，用户在这里加公司代理，保存即生效
            if (path == "/config")
            {
                ServeConfigPage(ctx);
                return;
            }
            if (path == "/config/save" && ctx.Request.HttpMethod == "POST")
            {
                await SaveConfigAsync(ctx);
                return;
            }

            if (IsApi(path))
            {
                if (ctx.Request.IsWebSocketRequest)
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    var upstream = await ClashApi.ConnectWsAsync(s, pathAndQuery);
                    if (upstream == null)
                    {
                        await wsCtx.WebSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus
                            .InternalServerError, "控制器不可达", CancellationToken.None);
                        return;
                    }
                    // 双向泵：浏览器 ⇆ 控制器，任一侧关闭则整体结束
                    await Task.WhenAll(
                        PumpAsync(wsCtx.WebSocket, upstream),
                        PumpAsync(upstream, wsCtx.WebSocket));
                    return;
                }

                await ProxyHttpAsync(s, ctx, pathAndQuery);
                return;
            }

            ServeStatic(ctx, path);
        }
        catch (Exception ex)
        {
            RiLog.Error("Panel", ex);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* 已关闭 */ }
        }
    }

    /// <summary>HTTP 反代：方法/路径/查询/请求体原样转发，响应整体回写。</summary>
    private static async Task ProxyHttpAsync(PrivacySettings s, HttpListenerContext ctx, string pathAndQuery)
    {
        byte[]? body = null;
        if (ctx.Request.HasEntityBody)
        {
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            body = ms.ToArray();
        }
        var result = await ClashApi.SendControllerAsync(s, ctx.Request.HttpMethod, pathAndQuery, body);
        if (result == null)
        {
            ctx.Response.StatusCode = 502;
            var bytes = System.Text.Encoding.UTF8.GetBytes("mihomo 控制器不可达");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = result.Value.Status;
        if (result.Value.ContentType != null) ctx.Response.ContentType = result.Value.ContentType;
        ctx.Response.ContentLength64 = result.Value.Body.Length;
        await ctx.Response.OutputStream.WriteAsync(result.Value.Body);
        ctx.Response.Close();
    }

    private static async Task PumpAsync(
        System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (from.State == System.Net.WebSockets.WebSocketState.Open
                   && to.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var r = await from.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await to.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "closed", CancellationToken.None);
                    break;
                }
                await to.SendAsync(new ArraySegment<byte>(buf, 0, r.Count),
                    r.MessageType, r.EndOfMessage, CancellationToken.None);
            }
        }
        catch { /* 任一侧断开即结束 */ }
    }

    private static void ServeStatic(HttpListenerContext ctx, string path)
    {
        var rel = path.TrimStart('/');
        if (rel.Length == 0) rel = "index.html";
        var full = Path.GetFullPath(Path.Combine(_distDir, rel));
        if (!full.StartsWith(_distDir, StringComparison.OrdinalIgnoreCase))
        { // 路径穿越防护
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }
        if (!File.Exists(full))
        {
            // SPA 回退：非文件路径一律回 index.html
            full = Path.Combine(_distDir, "index.html");
            if (!File.Exists(full)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        }
        ctx.Response.ContentType = MimeOf(Path.GetExtension(full));
        // 控制台首页注入「手动节点配置」悬浮入口（面板本身只是仪表盘，不能编辑配置）
        if (string.Equals(Path.GetFileName(full), "index.html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(full), "200.html", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(full);
            var btn = """
                <div style="position:fixed;right:16px;bottom:16px;z-index:2147483647">
                <a href="/config" style="display:inline-block;padding:9px 18px;border-radius:20px;
                background:#1a73e8;color:#fff;font:13px/1 'Segoe UI','Microsoft YaHei',sans-serif;
                text-decoration:none;box-shadow:0 2px 6px rgba(0,0,0,.25)">＋ 手动节点配置</a></div>
                """;
            html = html.Contains("</body>", StringComparison.OrdinalIgnoreCase)
                ? html.Replace("</body>", btn + "</body>", StringComparison.OrdinalIgnoreCase)
                : html + btn;
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
            return;
        }
        ctx.Response.ContentLength64 = new FileInfo(full).Length;
        using (var fs = File.OpenRead(full))
            fs.CopyTo(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    /// <summary>手动节点编辑页（控制台内嵌，模板预填当前配置）。</summary>
    private static void ServeConfigPage(HttpListenerContext ctx)
    {
        var cur = PrivacySettings.Current.ClashManualYaml;
        if (string.IsNullOrWhiteSpace(cur)) cur = RiBrowser.ClashNodesForm.Template;
        var html = ConfigPageHtml.Replace("@YAML@", cur.Replace("&", "&amp;").Replace("<", "&lt;"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static async Task SaveConfigAsync(HttpListenerContext ctx)
    {
        string yaml = "";
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            var body = System.Text.Json.JsonDocument.Parse(ms.ToArray()).RootElement;
            yaml = body.TryGetProperty("yaml", out var y) ? y.GetString() ?? "" : "";
        }
        catch { /* 非法请求体按空处理 */ }

        var (names, error) = ClashCoreManager.ParseManualYaml(yaml);
        var ok = yaml.Trim().Length == 0 || error == null; // 清空也允许（回到内置直连占位）
        if (!ok)
        {
            await WriteJsonAsync(ctx, 400, $$"""{"ok":false,"error":{{System.Text.Json.JsonSerializer.Serialize(error)}}}""");
            return;
        }
        var s = PrivacySettings.Current;
        s.ClashManualYaml = yaml;
        s.Save();
        try { OnManualSaved?.Invoke(); }
        catch (Exception ex) { RiLog.Error("PanelCfgSave", ex); }
        await WriteJsonAsync(ctx, 200,
            $$"""{"ok":true,"names":{{System.Text.Json.JsonSerializer.Serialize(names)}},"restart":true}""");
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private const string ConfigPageHtml = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>手动节点配置</title>
        <style>
          body{margin:0;font-family:"Segoe UI","Microsoft YaHei",sans-serif;background:#f8f9fa;color:#202124}
          .wrap{max-width:760px;margin:0 auto;padding:28px 20px}
          h1{font-size:20px;margin:0 0 6px}
          .sub{font-size:13px;color:#5f6368;margin:0 0 16px;line-height:1.7}
          textarea{width:100%;height:46vh;box-sizing:border-box;border:1px solid #dadce0;border-radius:10px;
                   padding:12px;font:13px/1.6 Consolas,monospace;background:#fff;color:#202124;resize:vertical}
          textarea:focus{outline:2px solid #1a73e8;border-color:transparent}
          .bar{margin-top:14px;display:flex;gap:10px;align-items:center}
          button{padding:10px 26px;border:none;border-radius:20px;background:#1a73e8;color:#fff;
                 font:14px/1 'Segoe UI','Microsoft YaHei',sans-serif;cursor:pointer}
          button:disabled{background:#9aa0a6;cursor:default}
          #st{font-size:13px;color:#5f6368}
          #st.err{color:#d93025}
          #st.ok{color:#1e8e3e}
        </style></head><body><div class="wrap">
        <h1>手动节点配置（Clash 格式）</h1>
        <p class="sub">顶层写 <b>proxies</b>（必需，节点列表）与 <b>rules</b>（可选，分流规则）；
        端口/控制器由浏览器托管。保存后内置内核自动重启，网络锁定随之解除。</p>
        <textarea id="y" spellcheck="false">@YAML@</textarea>
        <div class="bar">
          <button id="save" onclick="save()">保存并生效</button>
          <span id="st"></span>
        </div>
        <script>
        async function save(){
          const st=document.getElementById('st'),btn=document.getElementById('save');
          btn.disabled=true;st.className='';st.textContent='校验并生效中…';
          try{
            const r=await fetch('/config/save',{method:'POST',
              headers:{'Content-Type':'application/json'},
              body:JSON.stringify({yaml:document.getElementById('y').value})});
            const j=await r.json();
            if(!j.ok){st.className='err';st.textContent=j.error;btn.disabled=false;return;}
            st.className='ok';
            st.textContent='已保存，内核重启中（'+(j.names.length?j.names.join('、'):'仅内置直连')+'）——'
              +'约 2 秒后生效，被锁页面自动恢复。';
          }catch(e){st.className='err';st.textContent='保存失败：'+e;btn.disabled=false;}
        }
        </script></div></body></html>
        """;

    private static string MimeOf(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".webp" => "image/webp",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".webmanifest" => "application/manifest+json",
        ".txt" => "text/plain; charset=utf-8",
        ".xml" => "text/xml",
        _ => "application/octet-stream",
    };

    /// <summary>面板文件（metacubexd gh-pages 产物）缺失时下载解压。直连失败时经
    /// 本地 Clash 混合端口重试（此场景 Clash 本就已运行）。</summary>
    private static async Task<bool> EnsureDistAsync(PrivacySettings s)
    {
        var index = Path.Combine(Root, "index.html");
        if (File.Exists(index))
        {
            _distDir = Root;
            return true;
        }
        Directory.CreateDirectory(Root);
        const string url = "https://codeload.github.com/MetaCubeX/metacubexd/zip/refs/heads/gh-pages";
        var zipPath = Path.Combine(Root, "panel.zip");
        try
        {
            var ok = await DownloadAsync(url, zipPath, null);
            if (!ok && s.ClashMixedPort is > 0 and < 65536)
                ok = await DownloadAsync(url, zipPath, $"http://127.0.0.1:{s.ClashMixedPort}");
            if (!ok) return false;

            ZipFile.ExtractToDirectory(zipPath, Root, overwriteFiles: true);
            File.Delete(zipPath);
            // zip 内是 metacubexd-gh-pages/ 单层目录 → 提到根
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(Root, Path.GetRelativePath(dir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(f, dest, overwrite: true);
                }
                try { Directory.Delete(dir, recursive: true); } catch { /* 下次再清 */ }
                break;
            }
            _distDir = Root;
            RiLog.Info("[panel] metacubexd 面板已下载");
            return File.Exists(index);
        }
        catch (Exception ex)
        {
            RiLog.Error("PanelDownload", ex);
            return false;
        }
    }

    private static async Task<bool> DownloadAsync(string url, string to, string? proxy)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = proxy != null ? new WebProxy(proxy) : null,
            })
            { Timeout = TimeSpan.FromMinutes(4) };
            using var fs = File.Create(to);
            await (await http.GetStreamAsync(url)).CopyToAsync(fs);
            return true;
        }
        catch { return false; }
    }
}
