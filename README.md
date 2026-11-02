# Polaris 浏览器

基于 **Electron 45 / Chromium 156**（当前最新内核）的定制浏览器。HTTP 请求头与原生 Chrome 完全一致，并支持**页面级时区配置**（`Date` / `Intl` / 倒计时等全部走所选时区，与系统时区无关）。

## 快速开始

```bash
npm install
npm start          # 启动浏览器
npm run smoke      # 69 项自动化验证（时区 / UA / 请求头 / DevTools / 扩展 / 隐私防护），输出 smoke-result.json
npm run shot       # 对 SHOT_URLS 中的页面截图（开发用）
```

## 核心能力

### 1. 请求头与 Chrome 一致（三层保障）

| 层 | 手段 | 覆盖内容 |
|---|---|---|
| 网络层 | `session.setUserAgent()` + `app.userAgentFallback` | 所有 HTTP(S) 请求的 `User-Agent`、`Accept-Language` |
| 渲染层 | CDP `Emulation.setUserAgentOverride`（含 `userAgentMetadata`） | `navigator.userAgent` / `userAgentData`、Client Hints 头 |
| 兜底层 | `webRequest.onBeforeSendHeaders` 改写 | `user-agent`、`sec-ch-ua`、`sec-ch-ua-full-version-list`、`sec-ch-ua-platform`、`accept-language` |

UA 字符串为**手工构造**（Electron 45 会默认注入 `Polaris/0.1.0`，必须剔除），逐字节等于：

```
Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.8078.75 Safari/537.36
```

`Sec-CH-UA` 的 GREASE 品牌（`"Not;A=Brand";v="99"` 等）按 Chrome 156 的规则轮换生成；`platformVersion` 按 Windows 版本号映射（Win11 → `15.0.0`）。设置页也支持切换为自定义 UA。

### 2. 时区配置（不走系统，全场景一致）

设置 → 时区，选择任意 IANA 时区（如 `America/New_York`），**立即生效、无需重启**。

时区一致性采用"**进程统一 + 垫片兜底**"双保险，覆盖全部场景（含跨站 iframe 与其内部 Web Worker）：

| 机制 | 作用域 | 说明 |
|---|---|---|
| CDP `Emulation.setTimezoneOverride` | 渲染进程级（含同进程全部 frame、Worker 线程） | 原生 ICU 改写，无 JS 痕迹。主路径 |
| 关闭站点隔离（`--disable-site-isolation-trials`，与 Android Chrome 默认一致） | 进程模型 | 跨站 iframe 不再进入独立渲染进程，CDP 覆盖原生直达（含其 Worker） |
| 时区垫片（`src/main/tz-shim.js`） | 任意 frame 的主世界 | 若个别 iframe 仍被隔离（CDP 触达不到），在 document_start 重写 `Date`/`Intl` 本地时间语义兜底；CDP 已覆盖的 frame 自动退位 |

- `Date`、`Intl.DateTimeFormat`、`getTimezoneOffset`、`toString`、`toLocaleString`、构造/解析、**Web Worker 内**——全部按所选时区输出，主框架与 iframe 输出逐字符一致（含本地化时区名，如"北美东部标准时间"）
- 新标签页的实时时钟即为可视化验证
- 传空字符串清除覆写，已加载页面（含 iframe、Worker）实时恢复系统时区
- 时区在每次导航/新页面加载时自动重新应用

`npm run smoke`（69 项断言）会实际验证：主框架 + 跨站 iframe + 两者的 Web Worker 在 `America/New_York` 下的时区/偏移/`toString`、DevTools 关闭后覆盖自动恢复、本地扩展安装/列表/popup 加载、语言/字体/Canvas/WebRTC 隐私防护，以及改回系统后的实时恢复。

### 3. 开发者工具

- `F12` / `Ctrl+Shift+I` / 右键菜单"检查元素"打开 DevTools
- DevTools 前端与仿真用的 CDP 调试器互斥：打开 DevTools 时自动让出调试通道（该标签页的 UA/时区覆盖暂停），**关闭 DevTools 后自动恢复覆盖**，无需刷新页面（smoke 已覆盖该路径）

### 4. Chrome 扩展

菜单 → 扩展管理（或工具栏 🧩 按钮），支持三种安装方式：

- **Chrome 应用商店在线安装**：粘贴商店链接或扩展 ID，从 Google 官方更新服务（Omaha 协议）下载 CRX 并安装；常用扩展（uBlock Origin、Tampermonkey、Dark Reader、Vue/React Devtools）提供一键安装
- **本地安装**：`.crx` / `.zip` 安装包（自动剥离 CRX2/CRX3 头并解压），或已解包目录（含 manifest.json）
- 已安装扩展显示在工具栏（🧩 旁），点击弹出扩展的工具栏弹窗（browserAction/action popup）；重启浏览器自动恢复

实现基于 Chromium 内核的扩展子系统（`session.loadExtension`）。注意：Electron 支持扩展 API 子集，**Manifest V3 后台 Service Worker 类扩展的部分功能受限**；商店在线安装要求网络能访问 Google 服务（当前开发环境无法直连 Google，实测返回拦截错误页——此时用本地 `.crx` 安装即可，Chrome 商店网页上可直接下载 crx 文件）。

### 5. 隐私防护（语言 / 字体 / Canvas / WebRTC）

设置页新增"浏览器语言"与"隐私防护"两块，全部默认开启：

- **浏览器语言隐藏**：选择任意语言（如 en-US）后，`navigator.language` / `navigator.languages`、Intl 默认 locale、`Accept-Language` 请求头四者同步一致，不暴露系统语言；改回"跟随系统"实时恢复。三层实现：CDP `setUserAgentOverride(acceptLanguage)` + `setLocaleOverride`（原生主路径）→ 页面垫片 getter 兜底（含跨站 iframe）→ 重启后 `--lang` 深入 Web Worker 等全部执行环境
- **字体指纹防护（宽度探测已阻断，人设一致）**：对 `offsetWidth`/`offsetHeight`/`clientWidth`/`clientHeight`/`getBoundingClientRect`/`measureText` 做**读取端补偿**——当元素/上下文指定的字体族不在放行集合时，读取瞬间临时切换为回退字体、量完立即恢复。放行集合 = 通用族 + 页面 webfont + **与 UA 人设一致的标准 Windows 字体集**（Segoe UI / Segoe UI Emoji / Arial / Calibri 等约 45 个拉丁与符号字体，探测时按"已安装"真实度量）；**全部中文字体与其它非标准字体一律恒报"未安装"**——既隐藏中文环境，又不会出现"Windows 却探测不到 Segoe UI Emoji"这类自相矛盾信号（Emoji 渲染风格探测由此与 UA 一致地指向 Windows）。页面渲染与样式读回完全不变（无篡改痕迹）；配合 `measureText` 度量量化与 `document.fonts.check` 同规则返回，Canvas/内联样式两大宽度探测通道全部失效。`queryLocalFonts` API 保留在场（与真实 Chrome 相同），枚举权限由主进程恒拒（与用户拒绝授权的行为不可辨别）。
- **Canvas 指纹**：默认**不做扰动**——画布渲染与真实 Chrome 完全一致（相同内容哈希一致），避免"画布哈希不匹配任何真实系统"触发检测站的"OS 供应商不确定"回退；measureText 度量同样保持原生小数。需要跨站防关联时可在设置页开启"Canvas 指纹扰动"（种子确定性微扰，换种子即换指纹）
- **WebRTC 防泄露**：拦截页面可见的本机 IP——`onicecandidate` / `addEventListener('icecandidate')` 交付的 host/srflx 裸 IP 候选不放行，`localDescription` 系列 SDP 的裸 IP 候选行移除、`c=` 连接行改写为占位地址，`getStats` 的 local-candidate 统计清除 address/ip；mDNS（.local）与 relay（TURN）候选不受影响。注：Chromium 156 已移除 `--force-webrtc-ip-handling-policy` 内核级开关（实测二进制中不存在该字符串），故采用反检测浏览器通行的页面层封锁
- **隐私防护白名单**（设置页，即时生效）：白名单域名（含子域）不启用任何伪装——CDP 层下发"清除"恢复**系统时区与系统语言**（`Accept-Language` 同步），页面层时区/语言/字体/Canvas/WebRTC 垫片全部直通（本地字体可正常探测）；User-Agent 伪装保留（与 Chrome 一致的基础行为）。跨站 iframe 按各自 frame 的域名独立判定；从白名单站导航到普通站（或反向）即时切换

`npm run smoke` 覆盖以上全部：语言 5 项（含实时恢复）、字体 10 项（中文字体隐藏 / 标准 Windows 字体在场 / 检测站原样重放 0 命中 / Worker 内探测失效 / API 在场等）、Canvas 2 项、WebRTC 2 项、白名单 4 项（豁免站点系统时区/语言/字体 + 非白名单对照仍走伪装）。

### 6. 完整浏览器功能（⋮ 菜单全家桶）

- **隐身窗口**（Ctrl+Shift+N）：独立内存会话（磁盘零残留），UA/时区/语言/隐私防护同步生效，不写历史；深色 Chrome 风格外壳
- **历史记录**（`browser://history/`）：按日分组、搜索、单条删除、最近关闭的标签页快捷打开；内部页（browser://）与 Chrome 语义一致不进历史
- **下载内容**（Ctrl+J，`browser://downloads/`）：进度条、暂停/继续/取消、打开文件、打开所在文件夹、重新下载、完成通知
- **书签**：地址栏星标 / Ctrl+D 收藏与取消；书签管理页（重命名/删除/搜索）；书签栏（Ctrl+Shift+B 切换，所有窗口同步）
- **撤销关闭标签页**（Ctrl+Shift+T）
- **页内查找**（Ctrl+F）：Chrome 式浮动查找栏，命中计数、上/下一个、Enter/Shift+Enter/Esc
- **打印 / 另存为 PDF**（Ctrl+P，系统打印对话框）
- **缩放**：Chrome 档位（25%~300%），菜单显示当前百分比，Ctrl+加减/0 快捷键，F11 全屏
- **清除浏览数据**（Ctrl+Shift+Del）：浏览记录 / 缓存 / Cookie / 下载记录 / 站点数据，按需勾选（设置页）
- **任务管理器**（Shift+Esc，`browser://tasks/`）：各进程内存/CPU 实时列表，可结束标签页/扩展渲染进程（主进程与 GPU 保护不可结束）

以上功能在 smoke 中均有断言覆盖（历史记录/搜索/删除、书签收藏/重命名/取消、撤销关闭栈、缩放、页内查找命中、隐身窗口会话生效与不落历史）。

## 目录结构

```
src/main/          主进程
  main.js          入口、设置、IPC、CDP 应用时机、进程模型开关
  ua.js            Chrome UA / Client Hints / GREASE 构造
  emulation.js     CDP setUserAgentOverride + setTimezoneOverride
  tz-shim.js       跨站 iframe 时区垫片（Date/Intl 全语义重写 + 反检测伪装）
  fp-shim.js       隐私指纹垫片（语言 getter / 字体测量量化 / Canvas 种子化扰动 / WebRTC IP 封锁）
  headers.js       请求头兜底改写
  tabs.js          WebContentsView 标签页管理（含 DevTools 打开）
  extensions.js    扩展管理（CRX 解包 / 商店下载 / 加载 / 工具栏弹窗）
  history.js       浏览历史与最近关闭（JSON 持久化）
  bookmarks.js     书签存储
  downloads.js     下载跟踪（进度/暂停/续传/重下/打开）
  internal-scheme.js  browser:// 内部页面协议（安全上下文）
  navigate.js      地址栏输入解析（URL / 搜索词 / localhost）
  smoke.js         自动化验证（69 项断言）
ui/
  shell/           浏览器外壳（标签栏 + 工具栏 + 扩展图标区）
  pages/           newtab / settings / extensions / history / downloads / bookmarks / tasks / version / error
src/preload/       contextBridge（仅 browser:// 暴露 API）+ 全 frame 时区垫片注入
settings.json      用户配置（时区 / 语言 / UA / 搜索引擎 / 书签栏）
```

## 已知限制（诚实清单）

1. **站点隔离已关闭**（换取时区进程级全覆盖，与 Android Chrome 默认行为一致）：跨站 iframe 与主框架共享渲染进程，损失的是防 Spectre 类侧信道的隔离防御。若需要同时保留站点隔离与全场景时区一致，需 Chromium 二开（源码级改 ICU 默认时区，所有进程/Worker 天然生效）。
2. **垫片的理论可检测性**：垫片仅在被隔离的 iframe 中激活（当前进程模型下极少触发）。它重写了 `Date`/`Intl` 并伪装了 `Function.prototype.toString`，应对常规检测足够，但无法对抗理论上无界的深度反篡改分析。
3. **字体探测残留（窄）**：Canvas `measureText` 与内联样式 `offsetWidth` 两大主流宽度探测通道已被读取端补偿阻断（恒报"未安装"）；页面**静态样式表** CSS 指定字体再读宽度的组合（检测库不使用，需自建 `<style>` 类 + 读宽）不经内联路径，为残留通道；理论上还剩画布逐像素字形分析（已有 Canvas 种子化扰动干扰）。完全消除需源码级字体白名单（Chromium 二开）。
4. **WebRTC 为页面层封锁**：内核级"禁用非代理 UDP"开关已在 Chromium 156 移除，页面层封锁覆盖 JS 可见面（候选事件/SDP/getStats）；内核仍会实际收集候选（连接能力不受损）。如需内核级彻底禁用，同样需要 Chromium 二开。
5. **Worker 内的语言/Canvas 防护**：语言经重启后由 `--lang` 覆盖到 Worker；Worker 内的 canvas/字体钩子不可注入（Worker 无 DOM，风险本身极低）。
6. **深层指纹**：UA 与 Client Hints 头已一致，但 `process.versions` 侧信道、证书栈等极深层指纹未逐一抹除（对普通网站不可见）。
7. `window.open` 等交互式弹窗一律转为新标签页，未做独立弹窗。

## 关于后续升级到 Chromium 二开

当前 Electron 路线已实现全场景时区一致（含跨站 iframe 与 Worker，69 项断言全过）。如果后续需要：

- 源码级时区默认值（在保留站点隔离防御的同时全场景生效）；
- 去除任何 Electron 运行时痕迹；
- 自有品牌编译（`is_chrome_branded=false` + 自定义 About 页）；

建议迁移到 Chromium 直接二开：磁盘约 120–200GB，单次全量编译 4–20 小时，每次跟进上游 rebase 约 2–4 周。本项目的导航/标签管理/设置页等 UI 与主进程逻辑可平移复用，替换的是内核接入层（`emulation.js`/`tz-shim.js` → 命令行开关 + 源码补丁）。
