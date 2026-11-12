# 内网安全访问部署指南（运营商宽带独立 IP + 端口映射 + sing-box）

## 威胁模型

公司宽带持有运营商分配的独立公网 IP，通过端口映射把内网服务暴露出来。
公网上的攻击者会持续做**端口扫描、TLS 指纹识别、主动探测**，目标是：

1. 识别出"这是一个代理端口"（进而爆破、封锁、溯源）
2. 直接攻击映射进来的内网服务
3. 通过明文 DNS / 流量元数据推断你在访问哪些内网资源

对应的两端职责：**服务端负责"不被识别"，客户端（本浏览器）负责"不泄漏"**。

---

## 一、服务端（公司侧）——vless + Reality，让探测者看到"一个正常网站"

Reality 是专门对抗主动探测的传输安全层：不持有任何自签证书，直接"借用"
一个真实大站的证书做 TLS 握手。扫描器连上来看到的是合法证书与真实站点
指纹，与访问该真实网站无法区分；只有持有私钥的客户端能完成代理握手，
其余连接会被无感回落到伪装站点。

### 1. 生成密钥对（服务器上执行一次）

```bash
sing-box generate reality-keypair
# 输出：
# PrivateKey: <服务器私钥，只放在服务器配置里>
# PublicKey:  <公钥，填到浏览器「代理设置」的 Reality public key>
```

再生成一个 short_id（8/16 位十六进制）：

```bash
openssl rand -hex 8
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
        {
          "name": "office",
          "uuid": "<把 sing-box generate uuid 的输出填在这里>",
          "flow": "xtls-rprx-vision"
        }
      ],
      "tls": {
        "enabled": true,
        "server_name": "www.microsoft.com",
        "reality": {
          "enabled": true,
          "handshake": {
            "server": "www.microsoft.com",
            "server_port": 443
          },
          "private_key": "<上面生成的 PrivateKey>",
          "short_id": ["<上面生成的 short_id>"]
        }
      }
    }
  ],
  "outbounds": [
    { "type": "direct", "tag": "direct" }
  ]
}
```

运行：`sing-box run -c config.json`（建议 systemd / 计划任务开机自启）。

### 3. 安全清单（逐条核对）

- [ ] **只开放一个端口**：防火墙/端口映射仅放行 443（TCP）。SSH、管理后台、
      数据库等一律不映射、不对公网开放；服务器本身 SSH 改用密钥登录。
- [ ] ** Reality 伪装站点选境外大站**（如 `www.microsoft.com`、`www.apple.com`）：
      国内宽带到它连通性好，且与真实业务流量混在一起无法区分。
- [ ] **短 ID 定期轮换**：换 short_id 只需两端同步改，不换密钥对。
- [ ] **UUID 视为密码**：泄漏即换（`sing-box generate uuid`），客户端同步改。
- [ ] **不要在同一 IP 上再开 HTTP 明文代理**：明文 HTTP 代理可被 ISP 与扫描器
      直接识别并注入；本浏览器"手动代理(HTTP)"模式仅限内网测试环境使用。
- [ ] **映射目标最小化**：端口映射只转发到 sing-box 所在机器的 443，
      不做"整段端口范围映射"；内网其他服务经代理隧道访问，而不是直接映射。
- [ ] **日志轮换**：sing-box warn 级日志只含错误不含完整访问记录；
      如需审计在服务端另配，注意日志文件的访问权限。

---

## 二、客户端（本浏览器）——默认已做的防泄漏

| 防护点 | 状态 | 说明 |
|---|---|---|
| 本地入站仅回环 | 默认 | sing-box 只监听 `127.0.0.1`，局域网其他用户无法探测/盗用 |
| DNS 防泄露 | 默认开 | 域名经隧道用 DoH（IP 字面量）解析，运营商只见加密流量；内网私有域名需公司 DNS 时才关闭 |
| 路由绑定网卡 | 默认 | `auto_detect_interface`，杜绝绕过隧道的直连泄漏 |
| uTLS 指纹 | vless 默认 chrome | 客户端 TLS 指纹与浏览器 UA 一致，防被动指纹识别 |
| WebRTC 泄露 | 默认开 | 隐藏页面可见的本机 IP（隐私防护 → WebRTC 防泄露） |
| 全流量走代理 | 默认 | 路由 final=proxy，无分流规则，不存在"国内直连"侧信道 |

### 客户端配置（菜单 → 代理设置）

- 协议 `vless`；服务器 = 公司宽带独立 IP（或其 DDNS 域名）；端口 443
- UUID = 服务端 users[].uuid
- TLS：启用；SNI = 伪装站点（与服务端 server_name 一致，如 `www.microsoft.com`）；
  指纹 = `chrome`
- Reality：public key = 服务端密钥对的 PublicKey；short id = 服务端 short_id
- 防探测：DNS 防泄露保持开启（DoH 可自定义为公司自建 DoH）
- 浏览器隐私防护的伪装时区/语言建议与公司所在地一致，减少流量画像差异

### 为什么不用 shadowsocks/trojan 做对外端口

- shadowsocks（含 2022）无 TLS 外壳，主动探测可做协议指纹区分；
- trojan 有真证书但 SNI 指向自有域名，证书透明度日志可被检索关联；
- Reality 两点都没有：无自有证书、无回落特征，是对"被探测"最优解。
  （vmess/vless+ws+TLS 适合套 CDN 的场景，公司独立 IP 直连场景优先 Reality。）

---

## 三、验证

1. 浏览器「代理设置」→ 配置预览：核对 config.json 与服务端参数一一对应
2. 测试连接：远端可达 + 本地入站就绪
3. 访问 `https://www.browserscan.net/zh/bot-detection` 等检查出口 IP 为公司宽带 IP
4. 在公司外网络抓包验证：只见与"伪装站点 SNI 相同"的 TLS 流量，无明文 DNS
