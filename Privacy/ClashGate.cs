namespace RiBrowser.Privacy;

/// <summary>门禁结果：Passed 全过放行 / Skipped 用户选择进入（浏览器进入网络锁定态）/
/// Aborted 关闭窗口放弃进入。</summary>
internal enum GateResult { Passed, Skipped, Aborted }

/// <summary>Clash 启动门禁（仅提醒）：浏览器放行浏览前依次确认
/// ① 混合端口在监听 ② mihomo 控制器可达 ③ 内网探测地址经代理链路可达。
/// 失败给两个出口：重试 / 仍要进入——进入后浏览器处于不安全状态，
/// 网络访问全部禁止，直到配置可用代理自动解锁（见 MainForm 网络锁定）。</summary>
internal sealed class ClashGate : Form
{
    private readonly PrivacySettings _s;
    private readonly Label _status;
    private readonly Label _detail;
    private readonly Button _retry;
    private readonly Button _skip;
    private GateResult _result = GateResult.Aborted;

    public static GateResult Run(PrivacySettings s)
    {
        using var f = new ClashGate(s);
        Application.Run(f);
        return f._result;
    }

    private ClashGate(PrivacySettings s)
    {
        _s = s;
        Text = "内网通道检查";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 170);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        _status = new Label
        {
            Text = "正在启动检查…",
            AutoSize = false,
            Bounds = new Rectangle(20, 22, 430, 22),
            Font = new Font("Segoe UI", 10.5f),
        };
        _detail = new Label
        {
            Text = "",
            AutoSize = false,
            Bounds = new Rectangle(20, 50, 430, 42),
            ForeColor = UiColors.TextMuted,
        };

        _retry = new Button { Text = "重试", Bounds = new Rectangle(20, 108, 88, 30), Visible = false };
        _retry.Click += (_, _) => StartAttempt();
        _skip = new Button { Text = "仍要进入", Bounds = new Rectangle(116, 108, 88, 30), Visible = false };
        _skip.Click += (_, _) => { _result = GateResult.Skipped; Close(); };

        Controls.AddRange(new Control[] { _status, _detail, _retry, _skip });
        Shown += (_, _) => StartAttempt();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        DpiUtil.ScaleTree(this, DeviceDpi / 96f);
    }

    private void Status(string text)
    {
        _status.Text = text;
        _detail.Text = "";
    }

    private void Fail(string detail)
    {
        _status.Text = "内网通道未就绪";
        _detail.Text = detail;
        _retry.Visible = _skip.Visible = true;
    }

    private async void StartAttempt()
    {
        _retry.Visible = _skip.Visible = false;
        try
        {
            if (await TryOnceAsync())
            {
                _result = GateResult.Passed;
                Close();
            }
        }
        catch (Exception ex)
        {
            Fail("检查异常：" + ex.Message);
        }
    }

    private async Task<bool> TryOnceAsync()
    {
        // ① 混合端口（最多 ~4.5s）
        Status($"正在探测 Clash 混合端口 127.0.0.1:{_s.ClashMixedPort} …");
        var portUp = false;
        for (var i = 0; i < 9 && !portUp; i++)
        {
            portUp = await Task.Run(() => ProxyManager.TcpProbe("127.0.0.1", _s.ClashMixedPort, 700));
            if (!portUp) await Task.Delay(350);
        }
        if (!portUp)
        {
            Fail("混合端口未监听：本机没有正在运行的 Clash 内核。\n「仍要进入」将以安全锁定模式打开浏览器——禁止全部网页访问，配置代理后自动解锁。");
            return false;
        }

        // ② 控制器 API
        Status($"正在连接 mihomo 控制器 {_s.ClashApiBase} …");
        if (!await ClashApi.WaitControllerAsync(_s, 6000))
        {
            Fail("控制器 API 不可达：检查 控制器地址/密钥 是否与 Clash 设置一致。\n「仍要进入」将以安全锁定模式打开浏览器。");
            return false;
        }

        // ③ 内网探测：经主分组当前节点请求探测地址（mihomo 自己发请求，走完整隧道）
        var probe = _s.ClashProbeUrl.Trim();
        if (probe.Length == 0) return true;
        Status($"正在验证内网连通（经代理请求 {probe}）…");
        var cfg = await ClashApi.GetConfigsAsync(_s);
        var proxies = await ClashApi.GetProxiesAsync(_s);
        var main = cfg != null && proxies != null ? ClashApi.MainProxy(proxies, cfg.Mode) : null;
        if (main == null) return true; // 控制器就绪但无分组信息：放行，页面实际可达性由内核决定
        var delay = await ClashApi.DelayAsync(_s, main, probe);
        if (delay == null)
        {
            Fail($"内网探测失败：经当前出口「{main}」请求 {probe} 不通。\n请先在本机 Clash / 节点配置里处理，或「仍要进入」进入安全锁定模式。");
            return false;
        }
        RiLog.Info($"[gate] 内网通道就绪：{main} → {probe} ({delay}ms)");
        return true;
    }
}
