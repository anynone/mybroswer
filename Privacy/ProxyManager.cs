using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace RiBrowser.Privacy;

/// <summary>
/// 代理模块：
/// - manual：浏览器直接连接用户提供的 SOCKS5 / HTTP 代理端口（CEF proxy-server 参数）
/// - singbox：托管 sing-box 内核子进程（首次自动从 GitHub 下载），把远端
///   shadowsocks / vmess / vless / trojan 服务器转换为本地混合端口（socks+http），
///   浏览器再连本地端口 —— 即"直接连接到 proxy 服务器"。
/// 代理在启动时读取配置（CefSettings.ProxyServer 命令行），修改需重启浏览器。
/// </summary>
internal static class ProxyManager
{
    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(4),
    };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "singbox");

    private static string ExePath => Path.Combine(Dir, "sing-box.exe");
    private static string ConfigPath => Path.Combine(Dir, "config.json");

    private static Process? _core;

    public static bool CoreRunning => _core is { HasExited: false };

    // ==================== CEF 接入 ====================

    /// <summary>当前配置对应的 CEF proxy-server 参数；直连返回 null。</summary>
    public static string? CefProxyServer(PrivacySettings s)
    {
        switch (s.ProxyMode)
        {
            case "manual" when ValidHost(s.ProxyHost) && s.ProxyPort is > 0 and < 65536:
                return $"{(s.ProxyScheme == "http" ? "http" : "socks5")}://{s.ProxyHost}:{s.ProxyPort}";
            case "singbox" when ValidHost(s.SbServer):
                return $"socks5://127.0.0.1:{s.SbLocalPort}";
            default:
                return null;
        }
    }

    private static bool ValidHost(string? h) =>
        !string.IsNullOrWhiteSpace(h) && Uri.CheckHostName(h.Trim()) != UriHostNameType.Unknown;

    /// <summary>是否需要启动 sing-box 内核。</summary>
    public static bool NeedsCore(PrivacySettings s) =>
        s.ProxyMode == "singbox" && ValidHost(s.SbServer);

    // ==================== 内核管理 ====================

    /// <summary>确保 sing-box.exe 存在（首次自动下载 Windows amd64 发行包）。</summary>
    public static async Task EnsureBinaryAsync()
    {
        if (File.Exists(ExePath)) return;
        Directory.CreateDirectory(Dir);

        // GitHub API 解析最新版 windows-amd64 资产名（带 UA，否则 403）
        const string api = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.UserAgent.ParseAdd("RiBrowser/1.0");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var asset = json.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() is { } n
                && n.EndsWith("windows-amd64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("sing-box 发行包中没有 windows-amd64 资产");
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var version = json.GetProperty("tag_name").GetString() ?? "?";
        Console.WriteLine($"[proxy] 下载 sing-box {version} …");

        var zipPath = Path.Combine(Dir, "sing-box.zip");
        using (var fs = File.Create(zipPath))
            await (await Http.GetStreamAsync(url)).CopyToAsync(fs);

        ZipFile.ExtractToDirectory(zipPath, Dir, overwriteFiles: true);
        File.Delete(zipPath);
        // 发行包结构：sing-box-<ver>-windows-amd64/sing-box.exe → 提到根
        var nested = Directory.EnumerateFiles(Dir, "sing-box.exe", SearchOption.AllDirectories).First();
        if (nested != ExePath) File.Move(nested, ExePath, overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(Dir))
            try { Directory.Delete(sub, recursive: true); } catch { /* 占用时下次再清 */ }
        Console.WriteLine($"[proxy] sing-box 就绪：{ExePath}");
    }

    /// <summary>按协议生成 sing-box 出站配置对象（vmess/vless/trojan 组装 TLS、Reality、ws 传输）。</summary>
    public static Dictionary<string, object> BuildOutbound(PrivacySettings s)
    {
        var server = s.SbServer.Trim();
        Dictionary<string, object> outbound = s.SbProto switch
        {
            "vmess" => new()
            {
                ["type"] = "vmess", ["tag"] = "proxy",
                ["server"] = server, ["server_port"] = s.SbPort,
                ["uuid"] = s.SbUuid, ["security"] = "auto",
            },
            "vless" => new()
            {
                ["type"] = "vless", ["tag"] = "proxy",
                ["server"] = server, ["server_port"] = s.SbPort,
                ["uuid"] = s.SbUuid,
            },
            "trojan" => new()
            {
                ["type"] = "trojan", ["tag"] = "proxy",
                ["server"] = server, ["server_port"] = s.SbPort,
                ["password"] = s.SbPassword,
            },
            _ => new()
            {
                ["type"] = "shadowsocks", ["tag"] = "proxy",
                ["server"] = server, ["server_port"] = s.SbPort,
                ["method"] = string.IsNullOrWhiteSpace(s.SbMethod) ? "aes-128-gcm" : s.SbMethod.Trim(),
                ["password"] = s.SbPassword,
            },
        };

        // TLS（vmess/vless/trojan；Reality 仅 vless）
        var useTls = s.SbTls && s.SbProto is "vmess" or "vless" or "trojan";
        if (useTls)
        {
            var tls = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrWhiteSpace(s.SbSni) ? server : s.SbSni.Trim(),
                ["insecure"] = s.SbInsecure,
            };
            var alpn = s.SbAlpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (alpn.Length > 0) tls["alpn"] = alpn;
            // uTLS 指纹：vless 默认 chrome（客户端 TLS 指纹与本浏览器 UA 一致，防被动指纹识别）
            var fp = string.IsNullOrWhiteSpace(s.SbFingerprint) && s.SbProto == "vless"
                ? "chrome"
                : s.SbFingerprint;
            if (!string.IsNullOrWhiteSpace(fp))
            {
                tls["utls"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["fingerprint"] = fp.Trim(),
                };
            }
            if (s.SbProto == "vless" && !string.IsNullOrWhiteSpace(s.SbRealityKey))
            {
                tls["reality"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["public_key"] = s.SbRealityKey.Trim(),
                    ["short_id"] = s.SbRealitySid.Trim(),
                };
            }
            outbound["tls"] = tls;
        }

        // vless flow（xtls-rprx-vision）
        if (s.SbProto == "vless" && !string.IsNullOrWhiteSpace(s.SbFlow))
            outbound["flow"] = s.SbFlow.Trim();

        // 传输层（vmess/vless：ws）
        if (s.SbProto is "vmess" or "vless" && s.SbTransport == "ws")
        {
            var ws = new Dictionary<string, object>
            {
                ["type"] = "ws",
                ["path"] = string.IsNullOrWhiteSpace(s.SbWsPath) ? "/" : s.SbWsPath.Trim(),
            };
            if (!string.IsNullOrWhiteSpace(s.SbWsHost))
                ws["headers"] = new Dictionary<string, object> { ["Host"] = s.SbWsHost.Trim() };
            outbound["transport"] = ws;
        }

        return outbound;
    }

    /// <summary>入站列表：浏览器走的本地混合端口（仅回环）；
    /// TUN 模式追加虚拟网卡入站，接管全系统流量（auto_route + 严格路由防绕过）。</summary>
    private static object[] Inbounds(PrivacySettings s)
    {
        var list = new List<object>
        {
            new Dictionary<string, object>
            {
                ["type"] = "mixed", ["tag"] = "in",
                // 仅回环：局域网其他用户无法探测/盗用本机代理通道
                ["listen"] = "127.0.0.1", ["listen_port"] = s.SbLocalPort,
            },
        };
        if (s.SbTun)
        {
            list.Add(new Dictionary<string, object>
            {
                ["type"] = "tun", ["tag"] = "tun-in",
                ["address"] = new[] { "172.19.0.1/30" },
                ["mtu"] = 9000,
                ["auto_route"] = true,   // 接管默认路由（全系统流量）
                ["strict_route"] = true, // 严格路由：阻止其他程序绕过 TUN 直连
                ["stack"] = "mixed",
            });
        }
        return list.ToArray();
    }

    /// <summary>生成完整 config.json 内容（代理设置页预览 + 写盘共用）。
    /// 防探测硬化：DNS 防泄露（DoH 经隧道，不泄露域名给运营商明文 DNS）、
    /// 路由 auto_detect_interface（杜绝绕过隧道的直连泄漏）。</summary>
    public static string GenerateConfigJson(PrivacySettings s)
    {
        var outbound = BuildOutbound(s);
        var route = new Dictionary<string, object>
        {
            // 官方推荐规则：嗅探协议 + 劫持 DNS 进 sing-box 模块（TUN 下接管系统 DNS）
            ["rules"] = new object[]
            {
                new Dictionary<string, object> { ["action"] = "sniff" },
                new Dictionary<string, object> { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            },
            ["final"] = "proxy",
            // 绑定默认网卡出站：防止 sing-box 在多网卡/多路由下意外泄漏直连
            ["auto_detect_interface"] = true,
        };

        var config = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object> { ["level"] = "warn", ["timestamp"] = true },
            ["inbounds"] = Inbounds(s),
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = route,
        };

        // DNS 防泄露：域名经代理隧道加密解析（DoH/DoT），运营商只见加密流量；
        // 关闭时回退 sing-box 默认（本机系统 DNS）。
        // sing-box 1.12+ 新 DNS 格式：servers 为类型化对象（https/udp/tls）。
        if (s.SbDnsProtect && !string.IsNullOrWhiteSpace(s.SbDnsServer))
        {
            var addr = s.SbDnsServer.Trim();
            var (type, host) = addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? ("https", addr[8..])
                : addr.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
                    ? ("tls", addr[6..])
                    : ("udp", addr);
            host = host.Split('/')[0]; // 去路径（新格式默认 /dns-query）

            config["dns"] = new Dictionary<string, object>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = type,
                        ["tag"] = "dns-remote",
                        ["server"] = host,
                        ["detour"] = "proxy", // 查询走隧道，杜绝明文 DNS 泄露
                    },
                },
                ["final"] = "dns-remote",
                ["strategy"] = "prefer_ipv4",
            };
        }

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>写入 config.json（本地混合入站 + 远端协议出站）。</summary>
    private static void WriteConfig(PrivacySettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigPath, GenerateConfigJson(s));
    }

    /// <summary>启动 sing-box 内核（配置缺失/端口被占时抛异常说明原因）。</summary>
    public static void StartCore(PrivacySettings s)
    {
        StopCore();
        if (!NeedsCore(s)) return;
        try
        {
            EnsureBinaryAsync().Wait();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[proxy] sing-box 下载失败：" + ex.Message);
            throw new InvalidOperationException($"sing-box 内核下载失败：{ex.Message}");
        }

        WriteConfig(s);
        // TUN 模式需要创建虚拟网卡：必须以管理员权限启动（弹 UAC），
        // 提权进程无法重定向输出——退出码判断成败
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "run -c \"" + ConfigPath + "\"",
            WorkingDirectory = Dir,
        };
        if (s.SbTun)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas"; // UAC 提权
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        try
        {
            _core = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("TUN 模式需要管理员权限（已取消 UAC 授权）");
        }
        if (_core == null) throw new InvalidOperationException("sing-box 启动失败");

        if (!s.SbTun)
        {
            _core.OutputDataReceived += (_, e) => { if (e.Data is { } line) Console.WriteLine("[singbox] " + line); };
            _core.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Console.WriteLine("[singbox] " + line); };
            _core.BeginOutputReadLine();
            _core.BeginErrorReadLine();
        }

        // 等本地入站端口就绪（最多 5s），失败则带出内核退出原因
        for (var i = 0; i < 50 && !_core.HasExited; i++)
        {
            if (TcpProbe("127.0.0.1", s.SbLocalPort, 200)) break;
            Thread.Sleep(100);
        }
        if (_core.HasExited)
            throw new InvalidOperationException($"sing-box 异常退出（代码 {_core.ExitCode}），检查服务器参数");
        if (!TcpProbe("127.0.0.1", s.SbLocalPort, 500))
            throw new InvalidOperationException($"sing-box 本地端口 {s.SbLocalPort} 未就绪（可能被占用）");
        Console.WriteLine($"[proxy] sing-box 已启动：127.0.0.1:{s.SbLocalPort} → {s.SbProto} {s.SbServer}:{s.SbPort}");
    }

    public static void StopCore()
    {
        try
        {
            if (_core is { HasExited: false }) _core.Kill(entireProcessTree: true);
        }
        catch { /* 退出中重复 Stop 忽略 */ }
        _core = null;
    }

    // ==================== 连通测试 ====================

    /// <summary>TCP 直连探测（端口级连通性）。</summary>
    public static bool TcpProbe(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, port).Wait(timeoutMs) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置页"测试连接"：按模式探测代理链路各跳。</summary>
    public static async Task<string> TestConnectionAsync(PrivacySettings s)
    {
        if (s.ProxyMode == "off") return "当前是直连模式，无代理可测试。";
        if (s.ProxyMode == "manual")
        {
            if (!ValidHost(s.ProxyHost)) return "代理地址无效。";
            var ok = await Task.Run(() => TcpProbe(s.ProxyHost.Trim(), s.ProxyPort));
            return ok
                ? $"已连通 {s.ProxyScheme}://{s.ProxyHost}:{s.ProxyPort}"
                : $"无法连接 {s.ProxyHost}:{s.ProxyPort}（地址/端口或防火墙）";
        }

        // singbox：本地入站 → 远端服务器 两跳
        if (!ValidHost(s.SbServer)) return "sing-box 服务器地址无效。";
        var remote = await Task.Run(() => TcpProbe(s.SbServer.Trim(), s.SbPort));
        if (!remote) return $"无法连接远端 {s.SbServer}:{s.SbPort}（本机网络到服务器不通）";

        if (CoreRunning)
        {
            var local = await Task.Run(() => TcpProbe("127.0.0.1", s.SbLocalPort));
            return local
                ? $"链路就绪：127.0.0.1:{s.SbLocalPort} → {s.SbProto} {s.SbServer}:{s.SbPort}"
                : "远端可达，但本地入站端口未监听（重启浏览器以拉起 sing-box）";
        }
        return remote ? "远端服务器可达；重启浏览器后启动 sing-box 内核。" : "";
    }

    /// <summary>内核是否已下载（设置页状态显示）。</summary>
    public static bool BinaryReady => File.Exists(ExePath);
}
