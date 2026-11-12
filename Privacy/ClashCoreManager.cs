using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace RiBrowser.Privacy;

/// <summary>
/// 自管 mihomo 内核：本机没有任何 Clash 内核时（无 verge 管道、无 HTTP 控制器
/// 应答），浏览器自己下载并托管 mihomo——订阅经 proxy-providers 进入内核，
/// 配置由浏览器生成，浏览器仍是唯一控制面（ClashApi 选择顺序：
/// verge 管道 → 自管控制器 → 用户配置的 HTTP 控制器）。
/// 有外部内核时永远让位（混合端口会冲突），只做运行时动作、不碰外部内核文件。
/// </summary>
internal static class ClashCoreManager
{
    private static Process? _core;
    public static bool CoreRunning => _core is { HasExited: false };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "mihomo");
    /// <summary>预制内核（随浏览器分发的 kernel\mihomo.exe），优先于本地缓存使用。</summary>
    private static string BundledExePath => Path.Combine(AppContext.BaseDirectory, "kernel", "mihomo.exe");
    private static string LocalExePath => Path.Combine(Dir, "mihomo.exe");
    private static string ExePath => File.Exists(BundledExePath) ? BundledExePath : LocalExePath;
    private static string ConfigPath => Path.Combine(Dir, "config.yaml");
    private static string PidPath => Path.Combine(Dir, "mihomo.pid");

    /// <summary>内核可用：预制存在或本地缓存存在（无需在线下载）。</summary>
    public static bool BinaryReady => File.Exists(BundledExePath) || File.Exists(LocalExePath);

    /// <summary>clash 模式一律使用内置内核（常驻：无节点时带"直连"占位，
    /// 保证控制台/状态链路始终可用），不依赖、不探测本机 Clash GUI。</summary>
    public static bool ShouldRun(PrivacySettings s) => s.ProxyMode == "clash";

    private static bool HasNodeSource(PrivacySettings s) =>
        !string.IsNullOrWhiteSpace(s.ClashSubUrl) || ParseManualYaml(s.ClashManualYaml).Names.Count > 0;

    /// <summary>解析手动节点配置（Clash YAML）：顶层读取 proxies（必需）与
    /// rules（可选）。返回节点名列表/规则原文结构与错误（全部为空表示"未配置"）。</summary>
    public static (List<string> Names, string? Error) ParseManualYaml(string yaml)
    {
        yaml = yaml ?? "";
        if (yaml.Trim().Length == 0) return (new List<string>(), null);
        try
        {
            var root = new YamlDotNet.Serialization.DeserializerBuilder().Build()
                .Deserialize<object>(yaml) as Dictionary<object, object>;
            if (root == null)
                return (new List<string>(), "顶层必须是 YAML 映射（键值结构），不是列表或纯文本");
            if (!root.TryGetValue("proxies", out var proxiesObj) || proxiesObj is not List<object> proxies
                || proxies.Count == 0)
                return (new List<string>(), "缺少顶层 proxies:（节点列表），至少要有一个节点");

            var names = new List<string>();
            foreach (var p in proxies)
            {
                if (p is not Dictionary<object, object> node || !node.TryGetValue("name", out var n) ||
                    string.IsNullOrWhiteSpace(n.ToString()))
                    return (new List<string>(), "proxies 里有节点缺 name 字段或结构不对（每个节点必须是 - name: … 开头的映射）");
                names.Add(n.ToString()!);
            }
            if (root.TryGetValue("rules", out var rulesObj) && rulesObj is List<object> { Count: > 0 })
                return (names, null);
            return (names, null);
        }
        catch (Exception ex)
        {
            return (new List<string>(), "YAML 解析失败：" + ex.Message);
        }
    }

    /// <summary>外部内核存活：verge 管道在，或用户配置的 HTTP 控制器应答。</summary>
    public static bool ExternalCoreAlive(PrivacySettings s)
    {
        if (ClashApi.VergePipeAlive()) return true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1500) };
            using var req = new HttpRequestMessage(HttpMethod.Get,
                s.ClashApiBase.Trim().TrimEnd('/') + "/version");
            if (!string.IsNullOrWhiteSpace(s.ClashApiSecret))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", s.ClashApiSecret.Trim());
            using var resp = http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>生成内置内核配置：混合端口/控制器固定；节点来源可并存——内置"直连"
    /// 占位（保证零配置也能起内核，控制台/状态链路可用）、手动节点（顶层 proxies，
    /// 公司直发 trojan 域名/端口/密钥的场景）与订阅（proxy-providers）。
    /// 分组「节点选择」聚合全部来源；rules 优先用户手动配置（缺 MATCH 自动补，防直连泄漏）。</summary>
    public static string GenerateConfigYaml(PrivacySettings s)
    {
        var root = new Dictionary<string, object>
        {
            ["mixed-port"] = s.ClashMixedPort,
            ["external-controller"] = $"127.0.0.1:{s.ClashSelfControllerPort}",
            ["secret"] = s.ClashSelfSecret,
            ["log-level"] = "warning",
            ["allow-lan"] = false,
            // 用 mmdb 格式 + 预制 geo 文件（内核急切下载默认 metadb 会卡死在
            // GitHub 不可直连的网络——内网环境零外联启动的关键）
            ["geoip-format"] = "mmdb",
        };

        // 内置"直连"占位 + 手动节点（原样并入）
        var proxies = new List<object>
        {
            new Dictionary<string, object> { ["name"] = "直连", ["type"] = "direct" },
        };
        List<string>? manualNames = null;
        List<object>? manualRules = null;
        var manual = s.ClashManualYaml ?? "";
        if (manual.Trim().Length > 0)
        {
            var user = new YamlDotNet.Serialization.DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(manual);
            if (user.TryGetValue("proxies", out var p) && p is List<object> pl && pl.Count > 0)
            {
                proxies.AddRange(pl);
                manualNames = pl.OfType<Dictionary<object, object>>()
                    .Where(n => n.ContainsKey("name"))
                    .Select(n => n["name"].ToString()!).ToList();
            }
            if (user.TryGetValue("rules", out var r) && r is List<object> rl && rl.Count > 0)
                manualRules = rl;
        }
        root["proxies"] = proxies;

        // 订阅：proxy-providers（内核自己拉取 + 健康检查）
        var hasSub = !string.IsNullOrWhiteSpace(s.ClashSubUrl);
        if (hasSub)
        {
            root["proxy-providers"] = new Dictionary<string, object>
            {
                ["sub"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = s.ClashSubUrl.Trim(),
                    ["interval"] = 86400,
                    ["path"] = "providers/sub.yaml",
                    ["health-check"] = new Dictionary<string, object>
                    {
                        ["enable"] = true,
                        ["url"] = "https://www.gstatic.com/generate_204",
                        ["interval"] = 600,
                    },
                },
            };
        }

        var group = new Dictionary<string, object> { ["name"] = "节点选择", ["type"] = "select" };
        // 手动节点排前（select 默认选第一个：配置了节点即默认走它），内置"直连"垫底
        var groupMembers = new List<object>();
        if (manualNames is { Count: > 0 }) groupMembers.AddRange(manualNames);
        groupMembers.Add("直连");
        group["proxies"] = groupMembers;
        if (hasSub) group["use"] = new List<string> { "sub" };
        root["proxy-groups"] = new List<object> { group };

        // 规则：回环直连兜底；用户 rules 接管——目标不存在的条目自动丢弃
        // （常见于整份配置粘贴：rules 引用原文里的分组名，这里只有 节点选择/DIRECT/节点名）
        var rules = new List<object> { "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve" };
        if (manualRules != null)
        {
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "节点选择" };
            if (manualNames != null) foreach (var n in manualNames) valid.Add(n);
            foreach (var r in manualRules)
            {
                var text = r.ToString() ?? "";
                var parts = text.Split(',');
                var target = parts.Length switch
                {
                    >= 3 when parts[0].Equals("MATCH", StringComparison.OrdinalIgnoreCase) => parts[1],
                    >= 3 => parts[2],
                    2 => parts[1],
                    _ => "",
                };
                if (target.Length > 0 && valid.Contains(target)) rules.Add(text);
                else RiLog.Info($"[mihomo] 丢弃无法解析的规则（目标分组不存在）：{text}");
            }
            if (!rules.Any(r => r.ToString()?.StartsWith("MATCH", StringComparison.OrdinalIgnoreCase) == true))
                rules.Add("MATCH,节点选择");
        }
        else rules.Add("MATCH,节点选择");
        root["rules"] = rules;

        return new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(root);
    }

    /// <summary>启动（或重启以应用新配置）；内核就绪（控制器应答）后返回，
    /// 失败抛异常说明原因。零节点也常驻（内置"直连"占位）。</summary>
    public static void Start(PrivacySettings s)
    {
        Stop();
        StopStaleByPidFile();
        EnsureBinaryAsync().Wait();
        EnsureGeoFiles();
        if (string.IsNullOrWhiteSpace(s.ClashSelfSecret))
        {
            s.ClashSelfSecret = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..24];
            s.Save();
        }
        Directory.CreateDirectory(Path.Combine(Dir, "providers"));
        File.WriteAllText(ConfigPath, GenerateConfigYaml(s));

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"-d \"{Dir}\" -f \"{ConfigPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, // 内核日志 UTF-8，防中文乱码
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        _core = Process.Start(psi) ?? throw new InvalidOperationException("mihomo 启动失败");
        File.WriteAllText(PidPath, _core.Id.ToString());
        // 内核输出双写：riLog 落盘可诊断（WinExe 无控制台，Console 单写会全部丢失）
        _core.OutputDataReceived += (_, e) => { if (e.Data is { } line) RiLog.Info("[mihomo] " + line); };
        _core.ErrorDataReceived += (_, e) => { if (e.Data is { } line) RiLog.Info("[mihomo] " + line); };
        _core.BeginOutputReadLine();
        _core.BeginErrorReadLine();

        // 等控制器就绪（订阅拉取可能要几秒）
        for (var i = 0; i < 40 && !_core.HasExited; i++)
        {
            if (ProbeController(s)) break;
            Thread.Sleep(250);
        }
        if (_core.HasExited)
            throw new InvalidOperationException($"mihomo 异常退出（代码 {_core.ExitCode}），检查订阅链接");
        if (!ProbeController(s))
            throw new InvalidOperationException("mihomo 控制器未就绪（端口被占用？）");
        RiLog.Info($"[mihomo] 自管内核就绪：127.0.0.1:{s.ClashMixedPort} · 控制器 {s.ClashSelfControllerPort}");
    }

    /// <summary>GET /version（带自管密钥）。</summary>
    public static bool ProbeController(PrivacySettings s)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1500) };
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{s.ClashSelfControllerPort}/version");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", s.ClashSelfSecret);
            using var resp = http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>geo 数据文件与内核同目录预制（Country.mmdb / geosite.dat / ASN.mmdb），
    /// 缺失时从浏览器输出 kernel\ 拷入——内网零外联启动。</summary>
    private static void EnsureGeoFiles()
    {
        foreach (var f in new[] { "Country.mmdb", "geosite.dat", "ASN.mmdb" })
        {
            var dst = Path.Combine(Dir, f);
            if (File.Exists(dst)) continue;
            var src = Path.Combine(AppContext.BaseDirectory, "kernel", f);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(src, dst, overwrite: false);
            }
        }
    }

    public static void Stop()
    {
        try { if (_core is { HasExited: false }) _core.Kill(entireProcessTree: true); }
        catch { /* 退出中重复 Stop 忽略 */ }
        _core = null;
    }

    /// <summary>浏览器崩溃遗留的孤儿内核：pid 文件 + 进程名双校验后回收。</summary>
    private static void StopStaleByPidFile()
    {
        try
        {
            if (!File.Exists(PidPath)) return;
            var pid = int.Parse(File.ReadAllText(PidPath).Trim());
            var p = Process.GetProcessById(pid); // 不存在会抛 ArgumentException
            if (p is { HasExited: false } && p.ProcessName == "mihomo")
                p.Kill(entireProcessTree: true);
        }
        catch { /* 已退出/已回收 */ }
        finally
        {
            try { File.Delete(PidPath); } catch { /* 只读等极端情况 */ }
        }
    }

    /// <summary>确保 mihomo 内核可用：预制（随浏览器分发）或本地缓存存在即免下载；
    /// 都没有才在线下载（直连失败依次试系统代理、本机常见 Clash 混合端口）。</summary>
    public static async Task EnsureBinaryAsync()
    {
        if (BinaryReady) return;
        Directory.CreateDirectory(Dir);

        const string api = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";
        string? url = null, version = null;
        foreach (var proxy in ProxyCandidates())
        {
            try
            {
                using var http = new HttpClient(new HttpClientHandler { Proxy = proxy })
                    { Timeout = TimeSpan.FromSeconds(20) };
                using var req = new HttpRequestMessage(HttpMethod.Get, api);
                req.Headers.UserAgent.ParseAdd("RiBrowser/1.0");
                using var resp = await http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var json = System.Text.Json.JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync()).RootElement;
                version = json.GetProperty("tag_name").GetString() ?? "?";
                // 主构建 = mihomo-windows-amd64-v<版本>.zip（排除 go1xx 兼容版与 v2/v3 变体）
                var asset = json.GetProperty("assets").EnumerateArray().FirstOrDefault(a =>
                    a.GetProperty("name").GetString() is { } n
                    && Regex.IsMatch(n, @"^mihomo-windows-amd64-v\d+\.\d+\.\d+\.zip$"));
                if (asset.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                    throw new InvalidOperationException("mihomo 发行包中没有 windows-amd64 主构建资产");
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
            catch { /* 换下一个出网路径 */ }
        }
        if (url == null)
            throw new InvalidOperationException("无法访问 GitHub 获取 mihomo（需直连或本机代理）");
        Console.WriteLine($"[mihomo] 下载 {version} …");

        var zipPath = Path.Combine(Dir, "mihomo.zip");
        var ok = false;
        foreach (var proxy in ProxyCandidates())
        {
            try
            {
                using var http = new HttpClient(new HttpClientHandler { Proxy = proxy })
                    { Timeout = TimeSpan.FromMinutes(4) };
                using var fs = File.Create(zipPath);
                await (await http.GetStreamAsync(url)).CopyToAsync(fs);
                ok = true;
                break;
            }
            catch { /* 换下一个出网路径 */ }
        }
        if (!ok) throw new InvalidOperationException("mihomo 下载失败（直连与本机代理都不通）");

        ZipFile.ExtractToDirectory(zipPath, Dir, overwriteFiles: true);
        File.Delete(zipPath);
        // 资产结构：zip 根下 mihomo-windows-amd64.exe（统一改名 mihomo.exe）
        var exe = Directory.EnumerateFiles(Dir, "mihomo*.exe", SearchOption.AllDirectories).First();
        if (!exe.Equals(ExePath, StringComparison.OrdinalIgnoreCase))
            File.Move(exe, ExePath, overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(Dir))
            try { Directory.Delete(sub, recursive: true); } catch { /* 占用时下次再清 */ }
        Console.WriteLine($"[mihomo] 内核就绪：{ExePath}");
    }

    /// <summary>出网路径候选：直连 → 系统代理 → 本机常见混合端口。</summary>
    private static WebProxy?[] ProxyCandidates() =>
        new WebProxy?[] { null, new WebProxy("http://127.0.0.1:7897"), new WebProxy("http://127.0.0.1:7890") };
}
