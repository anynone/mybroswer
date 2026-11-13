using System.IO.Compression;
using System.Net;
using System.Text;

namespace RiBrowser.Privacy;

/// <summary>
/// 浏览器内 Clash 控制台（metacubexd 面板）的资产与内嵌页，经 clash://console
/// 自定义 scheme 由 ClashSchemeHandler 进程内应答——不开任何本地 TCP 端口：
/// 面板静态文件（首次使用时下载）、config.js 动态生成（后端免配置 + 面板语言
/// 覆盖）、手动节点编辑页。origin（clash://console）永久固定，localStorage
/// 里的已连后端/面板设置跨启动保留，metacubexd 不会再当首次访问闪登录框。
/// </summary>
internal static class ClashPanel
{
    /// <summary>控制台地址。</summary>
    public const string Url = "clash://console";

    /// <summary>控制台编辑页保存手动节点后回调（MainForm 挂 Ui(ApplyProxyNow)：
    /// 校验 → 写设置 → 内置内核重启生效 → 锁定自动解除）。</summary>
    public static Action? OnManualSaved;

    private static string _distDir = "";

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "panel");

    /// <summary>面板文件（metacubexd gh-pages 产物）就绪：已存在立即返回 true；
    /// 缺失时下载解压（直连失败走本地 Clash 混合端口）。均会应用后端地址补丁。</summary>
    public static async Task<bool> EnsureFilesAsync(PrivacySettings s)
    {
        var index = Path.Combine(Root, "index.html");
        if (File.Exists(index))
        {
            _distDir = Root;
            PatchPanelDist();
            return true;
        }
        var ok = await EnsureDistAsync(s);
        if (ok) PatchPanelDist();
        return ok;
    }

    /// <summary>面板后端地址补丁：metacubexd 对非 http(s) 的后端地址会强制重拼
    /// scheme（clash://console → clash://clash://console，再被 URL 规范化成
    /// clash://clash//console），REST/WS/显示全坏。改为放行 clash://。
    /// 幂等（补丁后模式串不再命中），面板重新下载后由 EnsureFilesAsync 重打。</summary>
    private static void PatchPanelDist()
    {
        try
        {
            var nuxt = Path.Combine(Root, "_nuxt");
            if (!Directory.Exists(nuxt)) return;
            const string needle = "e.startsWith(`http://`)||e.startsWith(`https://`)?e:";
            const string repl = "e.startsWith(`http://`)||e.startsWith(`https://`)"
                + "||e.startsWith(`clash://`)?e:";
            foreach (var f in Directory.EnumerateFiles(nuxt, "*.js"))
            {
                var text = File.ReadAllText(f);
                if (!text.Contains(needle)) continue;
                File.WriteAllText(f, text.Replace(needle, repl));
                RiLog.Info("[panel] 后端地址补丁：" + Path.GetFileName(f));
            }
        }
        catch (Exception ex) { RiLog.Error("PanelPatch", ex); }
    }

    /// <summary>生成面板 config.js：后端免配置（defaultBackendURL 即控制台自身，
    /// REST 与 WS 都由 scheme 处理器承接）+ 面板语言覆盖（面板按 navigator.language
    /// 选语言，会被浏览器防指纹的 en-US 伪装带偏成英文；在应用包加载前覆盖之）。</summary>
    internal static string BuildConfigJs(PrivacySettings s)
    {
        var lang = s.ClashPanelLang.Trim();
        return "window.__METACUBEXD_CONFIG__ = {\n" +
               $"  defaultBackendURL: '{Url}',\n" +
               "  githubToken: '',\n}\n" +
               (lang.Length > 0
                   ? $"Object.defineProperty(navigator, 'language', {{ get: function () {{ return '{lang}'; }} }});\n" +
                     $"Object.defineProperty(navigator, 'languages', {{ get: function () {{ return ['{lang}']; }} }});\n"
                   : "");
    }

    /// <summary>面板静态文件。非文件路径 SPA 回退 index.html；index/200.html 注入
    /// WS shim（clash:// 不支持 WebSocket，仿 WebSocket 类经 __ws 桥收发）与
    /// 「手动节点配置」悬浮入口（面板本身只是仪表盘，不能编辑配置）。</summary>
    internal static (byte[] Body, string Mime) ServeStatic(string path)
    {
        var rel = path.TrimStart('/');
        if (rel.Length == 0) rel = "index.html";
        var full = Path.GetFullPath(Path.Combine(_distDir.Length > 0 ? _distDir : Root, rel));
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            return (Encoding.UTF8.GetBytes("forbidden"), "text/plain; charset=utf-8"); // 路径穿越防护
        if (!File.Exists(full))
        { // SPA 回退：非文件路径一律回 index.html
            full = Path.Combine(Root, "index.html");
            if (!File.Exists(full))
                return (Encoding.UTF8.GetBytes(
                    "<!DOCTYPE html><meta charset=\"utf-8\"><body style=\"font:14px 'Segoe UI';padding:40px\">" +
                    "控制台面板尚未就绪（下载中或 GitHub 不可达），稍后重试。"),
                    "text/html; charset=utf-8");
        }

        var name = Path.GetFileName(full);
        if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || name.Equals("200.html", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(full);
            // shim 必须先于面板应用包执行：classic 内联脚本在 defer 的 module 之前跑
            var withShim = html.Contains("<head>", StringComparison.OrdinalIgnoreCase)
                ? html.Replace("<head>", "<head><script>" + WsShimJs + "</script>", StringComparison.OrdinalIgnoreCase)
                : "<script>" + WsShimJs + "</script>" + html;
            var btn = """
                <div style="position:fixed;right:16px;bottom:16px;z-index:2147483647">
                <a href="/config" style="display:inline-block;padding:9px 18px;border-radius:20px;
                background:#1a73e8;color:#fff;font:13px/1 'Segoe UI','Microsoft YaHei',sans-serif;
                text-decoration:none;box-shadow:0 2px 6px rgba(0,0,0,.25)">＋ 手动节点配置</a></div>
                """;
            withShim = withShim.Contains("</body>", StringComparison.OrdinalIgnoreCase)
                ? withShim.Replace("</body>", btn + "</body>", StringComparison.OrdinalIgnoreCase)
                : withShim + btn;
            return (Encoding.UTF8.GetBytes(withShim), "text/html; charset=utf-8");
        }

        using (var fs = File.OpenRead(full))
        {
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            return (ms.ToArray(), MimeOf(Path.GetExtension(full)));
        }
    }

    /// <summary>面板运行时 shim（必须在应用包前执行）：
    /// ① fetch 包装——面板的 REST 客户端（ky v2）对非 http scheme 的前缀拼接会
    ///    把 scheme 再拼一遍（clash://clash//console/…），这里折叠回正常地址；
    /// ② 仿 WebSocket——clash:// 不支持 WebSocket，帧经 clash://console/__ws 桥
    ///    （fetch 收发指令，C# 侧持 ClientWebSocket 连 mihomo，帧回推 __riWs.onFrame）。
    /// 面板按 http→ws 换协议构造的 ws 地址在这里只取路径部分。</summary>
    private const string WsShimJs = """
        (function () {
        if (window.__riWs) return;
        // 旧版面板曾把后端地址双拼（clash://clash//console）写进 localStorage，
        // 加载时自愈；fetch/WS 出口兜底折叠（Storage 的 setItem 属性不可覆写，
        // 拦不了写入，只能读改写 + 出口修）
        var D = 'clash://clash//';
        function fix(u) { return String(u).split(D).join('clash://'); }
        try {
          var el0 = localStorage.getItem('endpointList');
          if (el0 && el0.indexOf(D) >= 0) localStorage.setItem('endpointList', fix(el0));
        } catch (e) {}
        var of = window.fetch;
        window.fetch = function (input, init) {
          try {
            if (typeof input === 'object' && input && input.url) {
              var r = input;
              var u2 = r.url.indexOf(D) === 0 ? fix(r.url) : r.url;
              if (r.body != null) {
                // 流式/Request 体：CEF 的 scheme handler 拿不到非缓冲体（PostData
                // 缺失，内核会收到空体报 400），先物化成 ArrayBuffer 再发
                return r.clone().arrayBuffer().then(function (buf) {
                  var h = {};
                  r.headers.forEach(function (v, k) { h[k] = v; });
                  return of.call(window, u2, { method: r.method, headers: h, body: buf });
                }).catch(function () { return of.call(window, input, init); });
              }
              if (u2 !== r.url) input = new Request(u2, { method: r.method, headers: r.headers });
            } else if (typeof input === 'string' && input.indexOf(D) === 0) {
              input = fix(input);
            }
          } catch (e) {}
          return of.call(window, input, init);
        };
        var live = {};
        function rpc(cmd, obj) {
          try {
            fetch('clash://console/__ws/' + cmd, { method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(obj) }).catch(function () {});
          } catch (e) {}
        }
        function F(url) {
          var self = this;
          self.url = fix(url); self.readyState = 0; self.protocol = '';
          self.extensions = ''; self.bufferedAmount = 0; self.binaryType = 'blob';
          self._id = ++F._seq; self._ls = {};
          live[self._id] = self;
          rpc('open', { id: self._id, url: self.url });
        }
        F._seq = 0; F.CONNECTING = 0; F.OPEN = 1; F.CLOSING = 2; F.CLOSED = 3;
        F.prototype.send = function (d) { rpc('send', { id: this._id, data: String(d) }); };
        F.prototype.close = function (code, reason) {
          if (this.readyState === 3) return;
          this.readyState = 3;
          rpc('close', { id: this._id });
          this._fire('close', { code: 1000, reason: '', wasClean: true, target: this });
        };
        F.prototype.addEventListener = function (t, fn) { (this._ls[t] || (this._ls[t] = [])).push(fn); };
        F.prototype.removeEventListener = function (t, fn) {
          var a = this._ls[t] || [], i = a.indexOf(fn); if (i >= 0) a.splice(i, 1);
        };
        F.prototype._fire = function (t, ev) {
          var h = this['on' + t];
          if (typeof h === 'function') { try { h.call(this, ev); } catch (e) { console.error(e); } }
          var a = (this._ls[t] || []).slice();
          for (var i = 0; i < a.length; i++) { try { a[i].call(this, ev); } catch (e) { console.error(e); } }
        };
        window.__riWs = {
          onOpen: function (id) {
            var f = live[id];
            if (f && f.readyState === 0) { f.readyState = 1; f._fire('open', { target: f }); }
          },
          onFrame: function (id, data) {
            var f = live[id];
            if (f && f.readyState === 1) f._fire('message', { data: data, target: f });
          },
          onError: function (id) {
            var f = live[id];
            if (f) f._fire('error', { target: f });
          },
          onClose: function (id, code, reason) {
            var f = live[id];
            if (!f) return;
            delete live[id];
            if (f.readyState !== 3) {
              f.readyState = 3;
              f._fire('close', { code: code || 1006, reason: reason || '', wasClean: true, target: f });
            }
          }
        };
        window.WebSocket = F;
        })();
        """;

    /// <summary>手动节点编辑页（控制台内嵌，模板预填当前配置）。</summary>
    internal static byte[] ConfigPage()
    {
        var cur = PrivacySettings.Current.ClashManualYaml;
        if (string.IsNullOrWhiteSpace(cur)) cur = RiBrowser.ClashNodesForm.Template;
        var html = ConfigPageHtml.Replace("@YAML@", cur.Replace("&", "&amp;").Replace("<", "&lt;"));
        return Encoding.UTF8.GetBytes(html);
    }

    /// <summary>编辑页保存：校验 YAML → 写设置 → 通知 MainForm 重启内核生效。</summary>
    internal static (int Status, byte[] Body) SaveConfig(byte[]? body)
    {
        string yaml = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                body is { Length: > 0 } b ? b : Encoding.UTF8.GetBytes("{}"));
            yaml = doc.RootElement.TryGetProperty("yaml", out var y) ? y.GetString() ?? "" : "";
        }
        catch { /* 非法请求体按空处理 */ }

        var (names, error) = ClashCoreManager.ParseManualYaml(yaml);
        var ok = yaml.Trim().Length == 0 || error == null; // 清空也允许（回到内置直连占位）
        if (!ok)
            return (400, Encoding.UTF8.GetBytes(
                $$"""{"ok":false,"error":{{System.Text.Json.JsonSerializer.Serialize(error)}}}"""));
        var s = PrivacySettings.Current;
        s.ClashManualYaml = yaml;
        s.Save();
        try { OnManualSaved?.Invoke(); }
        catch (Exception ex) { RiLog.Error("PanelCfgSave", ex); }
        return (200, Encoding.UTF8.GetBytes(
            $$"""{"ok":true,"names":{{System.Text.Json.JsonSerializer.Serialize(names)}},"restart":true}"""));
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
