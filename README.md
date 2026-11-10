# Ri 浏览器

基于 **CEF（Chromium Embedded Framework）** 内核的 Windows 多标签浏览器。
内核版本随 [CefSharp.WinForms.NETCore](https://www.nuget.org/packages/CefSharp.WinForms.NETCore) 发布（当前为 Chromium 156），完整内核随程序自带，不依赖系统安装的 Edge/Chrome。

界面为 Chrome 风格无边框窗口：标签条集成窗口控制按钮（可拖动移动窗口、双击最大化、拖边贴屏分屏、边缘缩放），标签页圆角顶与工具栏融合，胶囊地址栏（聚焦蓝描边）。

## 运行

```bash
dotnet run          # 开发调试
dotnet build        # 仅编译
```

编译产物在 `bin/Debug/net9.0-windows/win-x64/RiBrowser.exe`。

发布独立可分发的版本（生成单目录程序，目标机器无需安装 .NET）：

```bash
dotnet publish -c Release
```

## 功能（v0.1）

- 多标签页：新建、关闭（× 或中键）、切换，标签过多时自动收窄
- 地址栏：自动识别网址/搜索词，不是网址时用百度搜索；Enter 或点击放大镜前往
- 后退 / 前进 / 刷新（加载中变停止）/ 主页
- `target="_blank"` 弹窗自动转为新标签页
- 文件下载（系统另存为对话框 + 完成提示）
- 磁盘缓存与登录状态持久化（`%LocalAppData%\RiBrowser\Cache`）
- 高 DPI 支持

## 快捷键

| 按键 | 功能 |
| --- | --- |
| Ctrl+T / Ctrl+W | 新建 / 关闭标签页 |
| Ctrl+Tab / Ctrl+Shift+Tab | 切换标签页 |
| Ctrl+L 或 F6 | 聚焦地址栏 |
| Ctrl+R 或 F5 | 刷新 |
| Alt+← / Alt+→ | 后退 / 前进 |

## 项目结构

```
Program.cs      入口：CEF 初始化与退出清理
MainForm.cs     主窗口：标签条、工具栏、地址栏、标签管理
TabItem.cs      自绘标签控件（标题、加载指示、关闭按钮）
BrowserTab.cs   标签数据模型（Chromium 控件 + 标签项）
Handlers.cs     CEF 处理器：弹窗转标签、快捷键、下载
```

## 隐私防护（引擎已生效，移植自 Polaris 浏览器）

入口：工具栏右侧 **⋮ 菜单 →「隐私防护」** 或快捷键 **Ctrl+,**。所有修改即时保存并**即时生效**（无需刷新页面）。

可配置项（`%LocalAppData%\RiBrowser\settings.json`）：

- **身份伪装**：User-Agent（与 Chrome 一致 / 自定义）、浏览器语言、页面时区、位置伪装（Geolocation 返回自设坐标）
- **指纹防护**：字体指纹防护（默认开）、WebRTC 防泄露（默认开）、Canvas 指纹扰动（默认关，可换种子）
- **白名单**：不启用任何伪装的域名（含子域），按 frame 独立判定

**位置伪装**（默认开，洛杉矶市中心 34.0522, -118.2437）：网页 `navigator.geolocation` 拿到的就是自设坐标（如 Google Maps 定位到设定城市）。机制：CDP `Emulation.setGeolocationOverride`（同 DevTools 传感器面板）+ `GeolocationPermissionHandler` 对定位权限请求自动放行（拿到的本就是伪造坐标，放行无泄露风险）。关闭时下发超界坐标强制"位置不可用"——即使伪装期的授权被持久化到 profile 也不会回落真实定位；白名单站点完全原生。建议与伪装时区配套使用。CEF 内核本身无可用定位数据源，未开启伪装时 Geolocation API 本就取不到真实位置。

实现架构（对齐参考项目 Polaris）：

| 层 | 手段 |
| --- | --- |
| CDP 覆盖（`PrivacyEngine`） | `Emulation.setUserAgentOverride`（UA + Client Hints + Accept-Language + navigator.language(s)）、`setTimezoneOverride`（ICU 级）、`setLocaleOverride`（Intl locale），经 `CefThread.ExecuteOnUiThread` 在 CEF UI 线程下发 |
| 垫片注入（`ShimRenderHandler`） | 每个 frame 的 V8 上下文创建时（document_start 时机）注入垫片：语言 getter 兜底、**字体读取端补偿**（measureText/offsetWidth/getBoundingClientRect 读取时临时剥离非白名单字体——中文字体恒报"未安装"，标准 Windows 字体在场）、Canvas 种子化扰动、WebRTC 本机 IP 候选封锁、时区垫片兜底 |
| 白名单 | CDP 层按主 frame URL 豁免；垫片层各 frame 按自身域名自检直通 |

自检：`RiBrowser.exe --selftest` 会在页面内执行与指纹检测站同类的探测并把结果打到 stdout（语言、时区、中文字体隐藏、标准字体在场等断言）。

已知限制（与参考项目一致）：WebRTC 为页面层封锁；Worker 内 canvas/字体钩子不可注入（已通过 Blob 前插覆盖 Worker 内 OffscreenCanvas 测量）；静态样式表字体探测为残留通道；浏览器语言需重启后经 `--lang` 深入 Web Worker。

## 开发者工具

- **右键页面 → 开发者工具**（会直接定位并选中右键位置的元素，与 Chrome「检查」一致）
- **⋮ 菜单 → 更多工具 → 开发者工具**
- 快捷键 **F12** 或 **Ctrl+Shift+I**

## 隐私防护（P1 已完成入口与配置，引擎逐步接入）

入口：工具栏右侧 **⋮ 菜单 →「隐私防护」** 或快捷键 **Ctrl+,**。所有修改即时保存到
`%LocalAppData%\RiBrowser\settings.json`。

当前支持配置（引擎接入后逐项生效）：

- **身份伪装**：User-Agent（与 Chrome 一致 / 自定义）、浏览器语言、页面时区
- **指纹防护**：字体指纹防护（默认开）、WebRTC 防泄露（默认开）、Canvas 指纹扰动（默认关，可换种子）
- **白名单**：不启用任何伪装的域名（含子域）

已接线生效的启动级配置：自定义 UA（网络层）、`--disable-site-isolation-trials`、`--lang`（语言设置重启后深入 Worker）。

时区/语言覆盖、字体/Canvas/WebRTC 垫片、请求头兜底改写为后续阶段（P2~P5）。

## 二次开发提示

- 首页/搜索引擎：改 `MainForm.HomePage` 和 `SearchUrl` 常量。
- 代理设置：`Program.cs` 中 `CefSettings.Proxy` 相关属性。
- 拦截/修改请求：给每个 `ChromiumWebBrowser` 实现 `IRequestHandler`。
- 鼠标手势、书签栏、历史记录等可在此基础上扩展；CEF 的 Cookie、历史、DevTools（右键 → 检查）等能力都已随内核可用。
