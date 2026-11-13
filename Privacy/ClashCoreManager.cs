using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RiBrowser.Privacy;

/// <summary>
/// 自管 mihomo 内核：本机没有任何 Clash 内核时（无 verge 管道、无 HTTP 控制器
/// 应答），浏览器自己下载并托管 mihomo——订阅经 proxy-providers 进入内核，
/// 配置由浏览器生成，浏览器仍是唯一控制面（ClashApi 选择顺序：
/// verge 管道 → 自管控制器 → 用户配置的 HTTP 控制器）。
/// 有外部内核时永远让位（混合端口会冲突），只做运行时动作、不碰外部内核文件。
/// TUN 需要管理员权限：开 TUN 时经 RestartElevated 提权重启（UAC），提权内核
/// 普通权限进程杀不掉 → 下次启动接管复用（改配置走控制器热重载）。
/// </summary>
internal static class ClashCoreManager
{
    private static Process? _core;
    /// <summary>当前内核是否提权运行（我们 runas 启动的，或接管的提权遗留）——
    /// 杀不掉，Stop/Start 走接管 + 控制器热重载路径。</summary>
    private static bool _elevated;
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

        // TUN 需要提权内核；用户在控制台开过后持久化在设置里，
        // 提权重启/接管时按它决定 TUN 是否随内核启动。
        // dns-hijack：应用的 DNS（无论配了哪个服务器）进 TUN 即被劫持，
        // 国外域名 fake-ip 由出口节点远端解析（DNS 零泄漏），国内域名走
        // 内部 DNS（国内 DoH）解析后按 GEOIP,CN 直连
        if (s.ClashTunOn)
        {
            root["tun"] = new Dictionary<string, object>
            {
                ["enable"] = true,
                ["stack"] = "mixed",
                ["auto-route"] = true,
                ["auto-detect-interface"] = true,
                // Windows 多宿主 DNS：系统并行向各网卡 DNS 发查询，发往路由器的
                // 走同网段链路路由绕过 TUN（DNS 泄露根因）。strict-route 加防火墙
                // 规则拦截其他网卡上的 53 端口，只留 TUN 侧解析
                ["strict-route"] = true,
                ["dns-hijack"] = new List<object> { "any:53" },
            };
            root["dns"] = new Dictionary<string, object>
            {
                ["enable"] = true,
                ["ipv6"] = false,
                ["listen"] = "0.0.0.0:53",
                ["enhanced-mode"] = "fake-ip",
                ["fake-ip-range"] = "7.7.0.0/16",
                // 保留：局域网名、Windows 联网检测/NTP 等对 IP 敏感的查询返回真实 IP
                ["fake-ip-filter"] = new List<object>
                {
                    "*.lan", "+.local", "+.msftconnecttest.com", "+.msftncsi.com",
                    "time.*.com", "time.*.gov", "time.*.apple.com", "time1.cloud.tencent.com",
                    "+.ntp.org", "+.pool.ntp.org",
                },
                // 启动阶段解析 DoH 域名的基础直连 DNS
                ["default-nameserver"] = new List<object> { "223.5.5.5", "119.29.29.29" },
                // 代理节点自身域名的解析（国内直连，保证节点可达）
                ["proxy-server-nameserver"] = new List<object> { "223.5.5.5", "119.29.29.29" },
                ["nameserver"] = new List<object> { "8.8.8.8", "1.1.1.1" },
                ["fallback"] = new List<object> { "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query" },
                ["fallback-filter"] = new Dictionary<string, object>
                {
                    ["geoip"] = true,
                    ["geoip-code"] = "CN",
                    ["ipcidr"] = new List<object> { "240.0.0.0/4" },
                },
                // DNS 上游按分流规则出站：境外 DNS/DoH 命中 MATCH 走代理，
                // 否则 nameserver 的 8.8.8.8/1.1.1.1 明文查询直连出去基本不可用
                ["respect-rules"] = true,
            };
        }

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

        // 规则优先级：回环兜底 → 用户 rules（可覆盖后面所有内置分流）→
        // 隐私检测站例外 → 中国域名直连 → MATCH 兜底。
        // GEOSITE 域名库做中国直连的原因：fake-ip 下 GEOIP 类规则不为域名触发
        // 解析、会整体漏到 MATCH；域名规则没有这个问题。
        // geosite:cn 社区列表把 browserleaks 等检测站也收进去了，直连会让
        // 测漏页显示家宽 IP、被误判泄露 → 检测站强制走代理。
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
        }

        // 内置分流插到用户 MATCH 之前（MATCH 是终结规则，放后面永远不生效）
        var builtIn = new List<object>
        {
            "DOMAIN-SUFFIX,browserleaks.com,节点选择",
            "DOMAIN-SUFFIX,browserleaks.net,节点选择",
            "DOMAIN-SUFFIX,browserleaks.org,节点选择",
            "GEOSITE,CN,DIRECT",
        };
        var matchIdx = rules.FindIndex(r => (r.ToString() ?? "")
            .StartsWith("MATCH", StringComparison.OrdinalIgnoreCase));
        if (matchIdx >= 0) rules.InsertRange(matchIdx, builtIn);
        else
        {
            rules.AddRange(builtIn);
            rules.Add("MATCH,节点选择");
        }
        root["rules"] = rules;

        return new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(root);
    }

    /// <summary>启动（或重启以应用新配置）；内核就绪（控制器应答）后返回，
    /// 失败抛异常说明原因。零节点也常驻（内置"直连"占位）。</summary>
    public static void Start(PrivacySettings s)
    {
        // 上次会话遗留的提权内核杀不掉（普通权限进程无权 Kill），接管复用：
        // 配置有变经控制器热重载推送，避免端口被占导致启动失败
        if (TryAdoptElevatedCore(s))
        {
            ReloadViaController(s);
            RiLog.Info("[mihomo] 接管上次会话的提权内核（TUN 保持可用）");
            if (s.ClashTunOn)
                _ = EnsureTunRoutesAsync(s); // 重载后校验路由真装上了（首启偶发漏装）
            return;
        }

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
        _elevated = false;
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

    /// <summary>以管理员权限重启内核（UAC 确认；用户取消抛 Win32Exception）。
    /// 配置先落盘（含 TUN 块），提权内核直接带 TUN 起来。ShellExecute 的
    /// runas 无法重定向输出，提权后内核日志不再进 riLog。</summary>
    public static void RestartElevated(PrivacySettings s)
    {
        if (_elevated && CoreRunning)
        { // 已是提权内核：只热重载（配置里已带 TUN）
            ReloadViaController(s);
            return;
        }
        Stop();
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
            UseShellExecute = true,
            Verb = "runas", // UAC
            // ShellExecute 路径 CreateNoWindow 无效，控制台黑框须用 WindowStyle 藏
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        _core = Process.Start(psi) ?? throw new InvalidOperationException("mihomo 提权启动失败");
        _elevated = true;
        File.WriteAllText(PidPath, _core.Id.ToString());

        // UAC 确认后内核仍要几秒就绪；订阅拉取慢时上限放宽
        for (var i = 0; i < 80 && !_core.HasExited; i++)
        {
            if (ProbeController(s)) break;
            Thread.Sleep(250);
        }
        if (_core.HasExited)
            throw new InvalidOperationException($"提权 mihomo 异常退出（代码 {_core.ExitCode}）");
        if (!ProbeController(s))
            throw new InvalidOperationException("提权 mihomo 控制器未就绪");
        RiLog.Info("[mihomo] 提权内核就绪：TUN 可用");
        if (s.ClashTunOn)
            _ = EnsureTunRoutesAsync(s);
    }

    /// <summary>TUN 接管路由自愈：提权内核首次带 TUN 启动时偶发路由未装上
    /// （网卡在、0.0.0.0/0 却不指向它，流量仍走物理网卡；提权内核日志收不到，
    /// 静默失败只能靠查路由表发现）。缺失则开关一次 TUN 让内核重装。</summary>
    public static async Task EnsureTunRoutesAsync(PrivacySettings s)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(attempt == 0 ? 2500 : 4000);
            if (TunRouteInstalled()) return;
            RiLog.Info($"[mihomo] TUN 路由未装上，开关重装（第 {attempt + 1} 次）");
            await ClashApi.SendControllerAsync(s, "PATCH", "/configs",
                System.Text.Encoding.UTF8.GetBytes("{\"tun\":{\"enable\":false}}"), "application/json");
            await Task.Delay(1200);
            await ClashApi.SendControllerAsync(s, "PATCH", "/configs",
                System.Text.Encoding.UTF8.GetBytes("{\"tun\":{\"enable\":true}}"), "application/json");
        }
    }

    /// <summary>默认路由是否已指向 TUN 网卡（mihomo TUN 固定用 198.18.0.1/30 网段，
    /// route print 里默认路由的接口列为该地址即接管生效）。查询失败按已装处理。</summary>
    private static bool TunRouteInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = "print -4",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return Regex.IsMatch(output, @"0\.0\.0\.0\s+0\.0\.0\.0\s+\d+\.\d+\.\d+\.\d+\s+198\.18\.0\.1");
        }
        catch { return true; }
    }

    /// <summary>把最新生成的配置经控制器热重载推给运行中的内核
    /// （PUT /configs?force=true，面板「重载配置」同一机制；提权内核改配置靠它）。</summary>
    public static bool ReloadViaController(PrivacySettings s)
    {
        try
        {
            var yaml = GenerateConfigYaml(s);
            File.WriteAllText(ConfigPath, yaml); // pid 文件与磁盘保持一致
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Put,
                $"http://127.0.0.1:{s.ClashSelfControllerPort}/configs?force=true");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", s.ClashSelfSecret);
            req.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { path = "", payload = yaml }),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            RiLog.Error("ClashReload", ex);
            return false;
        }
    }

    /// <summary>pid 文件指向的 mihomo 还活着、确实提权、控制器应答 → 可接管。
    /// （普通权限的遗留内核走原路径杀掉重启，保住 stdout 日志管道。）</summary>
    private static bool TryAdoptElevatedCore(PrivacySettings s)
    {
        try
        {
            if (_elevated && CoreRunning) return true;
            if (!File.Exists(PidPath)) return false;
            var pid = int.Parse(File.ReadAllText(PidPath).Trim());
            var p = Process.GetProcessById(pid); // 不存在抛 ArgumentException
            if (p is not { HasExited: false } || p.ProcessName != "mihomo") return false;
            if (!IsProcessElevated(p)) return false;
            if (!ProbeController(s)) return false;
            _core = p;
            _elevated = true;
            return true;
        }
        catch { return false; }
    }

    // ---- 进程提权判定（token TokenElevation）----
    private const uint TOKEN_QUERY = 0x8;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>目标进程是否以管理员运行。判定失败的场合（未提权进程对提权进程
    /// 的句柄/令牌访问受限）按"是提权"兜底——接管错一个普通内核只是丢日志，
    /// 判反了会让端口被占、浏览器启动失败。</summary>
    private static bool IsProcessElevated(Process p)
    {
        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
        if (hProc == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out var tok)) return true;
            try
            {
                return GetTokenInformation(tok, TokenElevation, out var elev,
                    sizeof(uint), out _) && elev != 0;
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(hProc); }
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
