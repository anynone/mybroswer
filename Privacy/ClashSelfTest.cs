using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RiBrowser.Privacy;

/// <summary>自管 mihomo 内核端到端自检（--clashselftest，CI/诊断用，不进 UI）：
/// 用本机 Verge 的订阅 profile 起一个一次性 HTTP 服务当"订阅链接"，
/// 以 force 模式启动自管内核（避开在跑的 Verge：混合端口 7890、控制器 9099），
/// 依次校验：内核下载位 → 配置生成 → 控制器就绪 → 订阅节点装入 → 经节点测速。
/// 全过 exit 0，任一失败 exit 1。</summary>
internal static class ClashSelfTest
{
    public static void Run()
    {
        // profiles 目录里混有 Verge 的增强模板（Merge/Groups，无 proxies 字段）——
        // 必须挑真正含顶层 proxies 的订阅文件，否则 mihomo 报 "must have a proxies field"
        var profile = Directory.EnumerateFiles(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev", "profiles"), "*.yaml")
            .FirstOrDefault(f => File.ReadLines(f).Any(l => l.StartsWith("proxies:", StringComparison.Ordinal)));
        if (profile == null)
        {
            Console.WriteLine("FAIL: 未找到含 proxies 的 Verge 订阅 profile（自检需要它充当订阅内容）");
            Environment.Exit(1);
        }

        // 一次性订阅服务：任何 GET 都回 profile 内容
        var subServer = new HttpListener();
        subServer.Prefixes.Add("http://127.0.0.1:18999/");
        subServer.Start();
        _ = Task.Run(async () =>
        {
            while (subServer.IsListening)
            {
                var ctx = await subServer.GetContextAsync();
                Console.WriteLine("[sub] 请求 " + ctx.Request.Url);
                var bytes = File.ReadAllBytes(profile);
                ctx.Response.ContentType = "text/yaml";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var s = new PrivacySettings
        {
            ProxyMode = "clash",
            ClashSelfManage = true,
            ClashSubUrl = "http://127.0.0.1:18999/sub",
            ClashMixedPort = 7890,           // 让位在跑的 Verge（7897）
            ClashSelfControllerPort = 9099,
            ClashSelfSecret = "selftest-secret",
        };

        var ok = true;
        void Step(string name, bool pass, string detail = "")
        {
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            if (!pass) ok = false;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        try
        {
            Console.WriteLine("== 自管 mihomo 内核自检 ==");
            Step("配置生成", ClashCoreManager.GenerateConfigYaml(s).Contains("proxy-providers"));

            ClashCoreManager.Start(s);
            Step("内核启动 + 控制器就绪", ClashCoreManager.ProbeController(s), "127.0.0.1:9099");

            using (var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9099/proxies"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ClashSelfSecret);
                var j = JsonDocument.Parse(http.Send(req).Content.ReadAsStringAsync().Result).RootElement;
                var group = j.GetProperty("proxies").EnumerateObject()
                    .FirstOrDefault(kv => kv.Name == "节点选择");
                var all = group.Value.TryGetProperty("all", out var a)
                    ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : new List<string>();
                // COMPATIBLE 是 provider 空时的占位节点——出现即代表订阅没真正装入
                var loaded = all.Count > 0 && !all.Contains("COMPATIBLE");
                Step("订阅节点装入（proxy-providers）", loaded,
                    loaded ? $"节点选择组成员 {all.Count} 个（首节点 {all[0]}）" : "仅 COMPATIBLE 占位（订阅拉取/解析失败）");
            }

            using (var req = new HttpRequestMessage(HttpMethod.Get,
                "http://127.0.0.1:9099/proxies/DIRECT/delay?url=https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=4000"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ClashSelfSecret);
                var r = http.Send(req).Content.ReadAsStringAsync().Result;
                Step("经内核测速（REST 完整性）", r.Contains("delay"), r);
            }

            // ClashApi 选择链：管道优先——本机 Verge 在跑时命中的是它（设计如此），
            // 自管内核独立运行时（无 Verge 机器）才会落到自管控制器
            var ver = ClashApi.GetVersionAsync(s).GetAwaiter().GetResult();
            Step("ClashApi 控制器选择链可达", ver != null,
                "version=" + ver + (ClashApi.VergePipeAlive() ? "（命中 Verge 管道，设计优先级）" : "（自管/HTTP 控制器）"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  异常：" + ex.Message);
            ok = false;
        }

        // 场景二：手动节点（无订阅）——公司直发 trojan 域名/端口/密钥的形态：
        // 把 Verge profile 里的 proxies 块抽出来当手写配置
        try
        {
            var manual = ExtractProxiesBlock(profile);
            Step("手动配置解析", manual.Contains("proxies:"), $"抽出 {manual.Length} 字节");
            var s2 = new PrivacySettings
            {
                ProxyMode = "clash",
                ClashSelfManage = true,
                ClashManualYaml = manual,
                ClashMixedPort = 7890,
                ClashSelfControllerPort = 9099,
                ClashSelfSecret = "selftest-secret",
            };
            ClashCoreManager.Start(s2);
            Step("手动模式内核启动", ClashCoreManager.ProbeController(s2));
            using (var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9099/proxies"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s2.ClashSelfSecret);
                var j = JsonDocument.Parse(http.Send(req).Content.ReadAsStringAsync().Result).RootElement;
                var all = j.GetProperty("proxies").EnumerateObject()
                    .FirstOrDefault(kv => kv.Name == "节点选择");
                var members = all.Value.TryGetProperty("all", out var arr)
                    ? arr.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : new List<string>();
                Step("手动节点装入", members.Count > 0 && !members.Contains("COMPATIBLE"),
                    "组成员：" + string.Join(",", members));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  手动模式异常：" + ex.Message);
            ok = false;
        }
        finally
        {
            ClashCoreManager.Stop();
            // 自管内核在独立目录有独立 pid 文件，Stop 已回收；清掉测试密钥痕迹
            try { subServer.Stop(); } catch { /* 已停 */ }
        }
        Console.WriteLine(ok ? "== 全部通过 ==" : "== 存在失败 ==");
        Environment.Exit(ok ? 0 : 1);
    }

    /// <summary>从完整 Clash 配置里抽出顶层 proxies: 块（到下一个顶层键为止）。</summary>
    private static string ExtractProxiesBlock(string yamlFile)
    {
        var lines = File.ReadAllLines(yamlFile);
        var outLines = new List<string>();
        var inBlock = false;
        foreach (var line in lines)
        {
            if (!inBlock)
            {
                if (line.StartsWith("proxies:", StringComparison.Ordinal)) { inBlock = true; outLines.Add(line); }
            }
            else
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break; // 下一个顶层键
                outLines.Add(line);
            }
        }
        return string.Join("\n", outLines);
    }
}
