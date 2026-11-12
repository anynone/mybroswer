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

    /// <summary>生成并写入 config.json（本地混合入站 + 远端协议出站）。</summary>
    private static void WriteConfig(PrivacySettings s)
    {
        Dictionary<string, object> outbound = s.SbProto switch
        {
            "vmess" => new()
            {
                ["type"] = "vmess", ["tag"] = "proxy",
                ["server"] = s.SbServer.Trim(), ["server_port"] = s.SbPort, ["uuid"] = s.SbUuid,
            },
            "vless" => new()
            {
                ["type"] = "vless", ["tag"] = "proxy",
                ["server"] = s.SbServer.Trim(), ["server_port"] = s.SbPort, ["uuid"] = s.SbUuid,
            },
            "trojan" => new()
            {
                ["type"] = "trojan", ["tag"] = "proxy",
                ["server"] = s.SbServer.Trim(), ["server_port"] = s.SbPort, ["password"] = s.SbPassword,
            },
            _ => new()
            {
                ["type"] = "shadowsocks", ["tag"] = "proxy",
                ["server"] = s.SbServer.Trim(), ["server_port"] = s.SbPort,
                ["method"] = s.SbMethod, ["password"] = s.SbPassword,
            },
        };

        var config = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object> { ["level"] = "warn", ["timestamp"] = true },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "mixed", ["tag"] = "in",
                    ["listen"] = "127.0.0.1", ["listen_port"] = s.SbLocalPort,
                },
            },
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = new Dictionary<string, object> { ["final"] = "proxy" },
        };

        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
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
        _core = Process.Start(new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "run -c \"" + ConfigPath + "\"",
            WorkingDirectory = Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (_core == null) throw new InvalidOperationException("sing-box 启动失败");

        _core.OutputDataReceived += (_, e) => { if (e.Data is { } line) Console.WriteLine("[singbox] " + line); };
        _core.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Console.WriteLine("[singbox] " + line); };
        _core.BeginOutputReadLine();
        _core.BeginErrorReadLine();

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
