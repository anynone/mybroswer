# 内网安全访问部署指南（RiBrowser × Clash）

> 2026-11 重写：浏览器代理层从内置 sing-box 切换为 Clash（mihomo）深度集成。
> 旧 sing-box 模式已下线（迁移说明见文末），服务端 Reality 指引保留为附录。

## 架构

```
RiBrowser（唯一操作界面）
   │  CEF 'proxy' preference（fixed_servers，永不回退直连）
   ▼
socks5://127.0.0.1:<混合端口>  ←——— mihomo 内核（三来源，自动选择）
   │                               ① 接管 Clash Verge Rev 的内核（推荐，零配置）
   │                               ② 浏览器自管内核（无 Verge 时自动下载/托管）
   │                               ③ 用户配置的任意 HTTP 控制器内核
   ▼
公司内网 / 互联网（按订阅规则分流）
```

职责边界：**订阅、节点、分组、规则分流由 Clash 侧负责；浏览器负责强制走通道、
开门前确认通道真的通、把状态透出来**。日常操作（换节点/切模式/测速/查连接）
全部在浏览器内完成，无需打开 Verge 窗口。

---

## 场景 A：已装 Clash Verge Rev（推荐，零配置）

1. Verge 正常安装并导入订阅，确认内核在跑（托盘图标/能上网即可）。
2. 浏览器 设置 → 代理 → 模式选「Clash（接管本机内核，推荐）」。
3. 完成。浏览器自动：
   - 发现 Verge 与内核之间的**命名管道**（`verge-mihomo-*`，枚举现存的，
     配置文件里的名字可能过期，不依赖），密钥从 Verge 运行时配置**只读**发现；
   - 流量指向混合端口（默认 7897，可在设置改）；
   - 盾牌状态、换节点、控制台、通道徽标全部就绪。

注意：
- 全程 **API-only**：浏览器只调用 mihomo 控制器接口（换节点/切模式/测速均为
  运行时动作），**绝不修改 Verge 的任何配置文件**，双主无冲突。
- Verge 的"外部控制器"HTTP 开关**无需打开**（管道模式下它是关着的也能接管）。
- 浏览器与 Verge 不同时启动自己的内核：检测到外部内核（管道/控制器应答）
  时自管内核永远让位。

## 场景 B：没装 Verge——浏览器自管内核

1. 设置 → 代理 → 模式「Clash」，填写**订阅链接**（必填）。
2. 勾选「本机无 Clash 时浏览器自管 mihomo 内核」（默认开）。
3. 浏览器自动下载 mihomo（GitHub Releases；出网路径依次尝试直连 → 常见本机
   代理端口 7897/7890），生成最小配置并托管进程：
   - 混合端口 = 设置的混合端口；HTTP 控制器 127.0.0.1:9099 + 自动生成密钥
     （浏览器独享，面板/页面接触不到）；
   - 订阅进 `proxy-providers`：内核自己拉取解析、每 24h 更新、每 10min 健康检查，
     生成「节点选择」分组；
   - 崩溃遗留的孤儿内核由 pid 文件 + 进程名双校验回收。
4. 自检：命令行运行 `RiBrowser.exe --clashselftest`，全 PASS 即链路健康。

> 已知边界：provider 的 `proxy: DIRECT` 字段在 mihomo 1.19.34 上会让订阅拉取
> 静默失败，生成的配置不带该字段（订阅域名需可直连访问，或本机已有可用代理）。

## 场景 C：其他 Clash GUI / 手搭内核

内核只要开着 HTTP 控制器即可：设置 → Clash 分组里填控制器地址（默认
`http://127.0.0.1:9097`）与密钥。无管道时浏览器按此地址连接。

---

## 浏览器侧安全特性（默认全开）

| 特性 | 行为 |
|---|---|
| 启动门禁 | 开浏览器依次确认：混合端口监听 → 控制器可达 → **内网探测地址经隧道真的通**，全过才放行 Tab 导航。失败提供 重试/启动 Clash Verge/仍要进入（自担） |
| 零直连泄漏 | CEF fixed_servers 模式永不回退直连；通道断时所有请求硬失败，不会静默降级 |
| 断线内页 | 通道断开时新导航换成中文引导页，每 5s 自动重试原地址，通道恢复即自动放行 |
| 通道徽标 | 每个标签页 favicon 角标：绿点=走代理、灰点=直连（规则分流命中）；悬停显示代理链与命中规则 |
| 控制器密钥 | 管道/自管模式下密钥不落浏览器设置；控制台面板由本地反代统一附加，页面侧不可见 |

**内网探测地址**：默认是 gstatic（只验证"出得了网"）。公司部署时应改为真实
内网 URL（如 `http://oa.公司域/healthz`），门禁才真正验证"进了公司内网"。

## Clash 控制台（浏览器内）

盾牌菜单 → 「Clash 控制台（浏览器内）」：metacubexd 全功能面板（连接实时表/
流量曲线/规则/日志/分组节点）。实现：本地回环服务承载面板静态文件并把 API
（含 WebSocket 实时流）反代到内核控制器；面板首次使用自动下载（GitHub），
`defaultBackendURL` 注入实现零配置。

## 故障排查

| 症状 | 处置 |
|---|---|
| 盾牌红：混合端口未监听 | Verge/内核没跑：盾牌菜单 → 打开 Clash Verge；或确认自管内核未被安全软件拦截 |
| 盾牌黄：控制器不可达 | 密钥不匹配（场景 C 检查设置里的密钥；场景 A 检查 Verge 版本是否改了配置目录） |
| 门禁"内网探测失败" | 当前出口节点不通：盾牌换节点后重试；或探测地址本身改了 |
| 自管内核订阅拉取失败 | 订阅域名需直连可达；`--clashselftest` 看详细错误；检查 `%LocalAppData%\RiBrowser\mihomo\` 下日志 |
| 端口冲突（7897 被占） | 设置里改混合端口（浏览器与内核同步换）；自管内核控制器固定 9099，被占时换端口设置 |
| 面板打不开 | 删除 `%LocalAppData%\RiBrowser\panel\` 让其重新下载 |

诊断工具：`RiBrowser.exe --clashselftest`（自管内核端到端自检）、
`--genproxyconfig`（导出旧 sing-box 配置对照）、日志 `%LocalAppData%\RiBrowser\rilog.txt`。

---

## 从旧 sing-box 模式迁移

1. 把原来「代理设置」里的服务器参数交给订阅：在 Verge 里添加对应的
   节点/自建订阅（服务端不用动，附录的 vless+Reality 服务端配置继续有效）。
2. 浏览器设置 → 模式切到「Clash」。
3. sing-box 模式已从界面下线；若 settings.json 里仍是 `"ProxyMode": "singbox"`，
   浏览器会标注"已下线模式"并继续按旧参数工作，切换后即退役。
   需要临时回退：手改 `%LocalAppData%\RiBrowser\settings.json` 的 ProxyMode。

---

## 附录：服务端（公司侧）——vless + Reality，让探测者看到"一个正常网站"

> 运营商宽带独立 IP + 端口映射场景。Reality 不持有自签证书，"借用"真实大站
> 证书做 TLS 握手，扫描者看到的与访问真实网站无法区分；仅持私钥客户端能完成
> 代理握手，其余连接无感回落到伪装站点。

### 1. 生成密钥对（服务器上执行一次）

```bash
sing-box generate reality-keypair
# PrivateKey: 服务器私钥（只放服务器配置）
# PublicKey:  填到订阅节点配置
openssl rand -hex 8   # → short_id
```

### 2. 服务器 config.json（唯一对外端口 443）

```json
{
  "log": { "level": "warn" },
  "inbounds": [
    {
      "type": "vless",
      "listen": "::",
      "listen_port": 443,
      "users": [
        { "name": "office", "uuid": "<uuid>", "flow": "xtls-rprx-vision" }
      ],
      "tls": {
        "enabled": true,
        "server_name": "www.microsoft.com",
        "reality": {
          "enabled": true,
          "handshake": { "server": "www.microsoft.com", "server_port": 443 },
          "private_key": "<PrivateKey>",
          "short_id": ["<short_id>"]
        }
      }
    }
  ],
  "outbounds": [ { "type": "direct", "tag": "direct" } ]
}
```

### 3. 客户端侧

订阅里对应的节点（Clash/mihomo 格式）：

```yaml
proxies:
  - name: "公司内网"
    type: vless
    server: <公司公网IP或域名>
    port: 443
    uuid: <uuid>
    flow: xtls-rprx-vision
    tls: true
    servername: www.microsoft.com
    reality-opts:
      public-key: <PublicKey>
      short-id: <short_id>
    client-fingerprint: chrome   # 与浏览器 UA 一致，防被动指纹识别
```

规则建议（Verge 订阅的 Merge/规则里）：

```yaml
rules:
  - DOMAIN-SUFFIX,公司域,公司内网   # 内网域名固定走公司节点
  - IP-CIDR,10.0.0.0/8,公司内网,no-resolve
  - IP-CIDR,172.16.0.0/12,公司内网,no-resolve
  - IP-CIDR,192.168.0.0/16,公司内网,no-resolve
  - MATCH,<其余默认分组>
```

DNS 防泄露：在 Verge/内核配置开启 DoH 且内网域名走公司 DNS 解析，
避免向运营商 DNS 泄露访问目标。
