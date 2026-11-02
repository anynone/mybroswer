'use strict';
const path = require('path');
const { app, BrowserWindow, ipcMain, session, webContents, dialog, Notification, WebContentsView } = require('electron');

const settingsStore = require('./settings');
const ua = require('./ua');
const emulation = require('./emulation');
const headers = require('./headers');
const tzshim = require('./tz-shim');
const fpshim = require('./fp-shim');
const allowlistUtil = require('./allowlist');
const { isAllowed } = allowlistUtil;
const history = require('./history');
const bookmarks = require('./bookmarks');
const dlMgr = require('./downloads');
const diag = require('./diaglog');
const extMgr = require('./extensions');
const TabManager = require('./tabs');
const { registerInternalScheme, handleInternalScheme } = require('./internal-scheme');
const smoke = require('./smoke');

const IS_SMOKE = process.argv.includes('--smoke');
const IS_SHOT = process.argv.includes('--shot');

app.name = 'Polaris';
if (process.platform === 'win32') app.setAppUserModelId('com.polaris.browser');

// 时区一致性优先：站点隔离会把跨站 iframe 放入独立进程（CDP 进程级时区覆盖触达不到）。
// 与 Android Chrome 一致地关闭强制隔离，让跨站 iframe 与主框架同进程 → 时区覆盖原生全覆盖；
// 垫片仍作为兜底（若个别 iframe 仍被隔离，页面脚本层保持一致）。
app.commandLine.appendSwitch('disable-site-isolation-trials');
// 注：Local Font Access（queryLocalFonts）特性保留开启 —— 与真实 Chrome 一致（API 存在），
// 枚举权限由下方 permission handler 恒拒，与用户拒绝授权的真实行为不可辨别
app.commandLine.appendSwitch('disable-features', 'SitePerProcess,IsolateOrigins,site-per-process');

// smoke 模式使用独立的临时 userData，避免污染真实配置
if (IS_SMOKE) {
  const os = require('os');
  const fs = require('fs');
  app.setPath('userData', fs.mkdtempSync(path.join(os.tmpdir(), 'polaris-smoke-')));
} else if (!app.requestSingleInstanceLock()) {
  app.quit();
}

// 启动级隐私开关（读取已保存设置，修改后重启生效）:
// 自定义语言经 --lang 进入所有执行环境（含 Web Worker，CDP 运行时覆盖触达不到的地方）。
// 注：WebRTC"禁用非代理 UDP"的命令行开关已在 Chromium 156 中移除（--force-webrtc-ip-handling-policy，
// pref 入口同样不存在），WebRTC 防泄露由 fp-shim 在页面层封锁实现，无需重启。
const earlySettings = settingsStore.load();
if (earlySettings.locale && earlySettings.locale !== 'system') {
  app.commandLine.appendSwitch('lang', earlySettings.locale);
}

registerInternalScheme(); // 必须在 app ready 之前调用

const state = {
  settings: null,
  env: null,
  win: null,   // 首个（或仅存的）普通窗口，shot/smoke 兼容用
  tm: null,
  wins: new Map(),      // winId -> { win, tm, incognito }
  sessions: new Set(),  // 已配置过的会话（默认 + 各隐身窗口）
};

function broadcastToBrowserPages(channel, data) {
  for (const wc of webContents.getAllWebContents()) {
    if (!wc.isDestroyed() && wc.getURL().startsWith('browser://')) wc.send(channel, data);
  }
}

// ---- ⋮ 菜单浮层（按窗口池化复用） ----
// 菜单以 WebContentsView 浮层呈现（叠在标签页内容之上），避免外壳 HTML 内的下拉
// 被页面内容视图遮挡。每个窗口的菜单视图只创建一次：关闭时仅从窗口摘下，
// 再次打开瞬间挂回（无渲染器冷启动延迟）。WebContentsView 不支持透明背景，
// 浮层宽度即面板宽度（300px），子菜单为面板内二级页面，高度由页面按内容上报。
const MENU_W = 308;   // 300px 面板 + 8px 右缘间距
const menuPool = new Map();   // hostWin.id -> { view, host, open, lastH }

function menuBounds(hostWin, height) {
  const [w, h] = hostWin.getContentSize();
  const menuH = Math.max(160, Math.min(height, h - 90));
  return { x: Math.max(0, w - MENU_W), y: 80, width: MENU_W, height: menuH };
}

function menuEntryOfSender(wc) {
  for (const entry of menuPool.values()) {
    if (entry.view.webContents === wc) return entry;
  }
  return null;
}

// 菜单开合状态同步给宿主外壳：⋮ 按钮在"三个点 / 三叶"图标间渐变切换
function syncMenuButton(entry) {
  try {
    if (!entry.host.isDestroyed()) entry.host.webContents.send('pol:ui-command', { type: 'menu-state', arg: entry.open });
  } catch { /* ignore */ }
}

// ---- 子菜单浮层（Chrome 风格：父菜单左侧弹出的独立视图） ----
// 悬停父项约 0.5s 或点击 → 弹出；移入保持；移开缓冲收起。
// 焦点协议：展开时主动把焦点交给子菜单视图；父菜单失焦时若焦点在子菜单
// 或处于展开/切换宽限期内则不收起——避免 attach 引发的焦点流转误关。
const SUB_W = 232;
const SUB_H = { history: 112, bookmarks: 112, tools: 144 };

function subWinDestroy(entry) {
  try { entry.subWin.destroy(); } catch { /* ignore */ }
}

function closeSub(entry) {
  const sub = entry && entry.sub;
  if (!sub) return;
  clearTimeout(sub.timer);
  entry.sub = null;
  // 透明子窗口池化：仅隐藏，下次展开零加载
  try { if (entry.subWin && !entry.subWin.isDestroyed()) entry.subWin.hide(); } catch { /* ignore */ }
}

function menuEntryByAnySender(wc) {
  for (const entry of menuPool.values()) {
    if (entry.view.webContents === wc) return entry;
    if (entry.subWin && !entry.subWin.isDestroyed() && entry.subWin.webContents === wc) return entry;
  }
  return null;
}

function openSub(entry, name, itemTop) {
  if (!entry || !entry.open || !entry.subWin || entry.subWin.isDestroyed()) return;
  const host = entry.host;
  clearTimeout(entry.sub && entry.sub.timer);
  if (entry.sub && entry.sub.name === name) return; // 已展开同一组
  try { entry.subWin.webContents.send('pol:sub-show', name); } catch { /* ignore */ }
  const b = menuBounds(host, entry.lastH);
  const subH = SUB_H[name] || 130;
  const cb = host.getContentBounds();
  const y = Math.max(cb.y + 8, Math.min(cb.y + b.y + (Number(itemTop) || 0) - 6, cb.y + cb.height - subH - 8));
  // 透明子窗口：贴在主窗口客户区坐标（子菜单右缘与父菜单左缘 1px 重合）
  entry.subWin.setBounds({
    x: cb.x + Math.max(0, b.x - SUB_W + 1),
    y,
    width: SUB_W,
    height: subH,
  });
  entry.subWin.showInactive();
  entry.sub = { name, timer: null };
  entry.subOpenedAt = Date.now();
}

function scheduleSubHide(entry, delay = 400) {
  if (!entry || !entry.sub) return;
  clearTimeout(entry.sub.timer);
  entry.sub.timer = setTimeout(() => closeSub(entry, 'scheduled'), delay);
}

function hideMenu(entry) {
  if (!entry || !entry.open) return;
  entry.open = false;
  entry.lastHideAt = Date.now();
  closeSub(entry, 'hideMenu');
  try { entry.host.contentView.removeChildView(entry.view); } catch { /* ignore */ }
  syncMenuButton(entry);
}

function closeMenu() {
  for (const entry of menuPool.values()) hideMenu(entry);
}

function openMenu(hostWin) {
  const entry = ensureMenu(hostWin);
  if (!entry) return;
  if (entry.open) { hideMenu(entry); return; } // 再次点击 ⋮ = 收起（toggle）
  // 菜单开着时点击 ⋮：菜单失焦会抢先一步收起（blur 先于 click 到达），
  // 这里的点击应视为"那次收起"，不再重新打开 —— 保证按钮是真正的开关
  if (entry.lastHideAt && Date.now() - entry.lastHideAt < 250) {
    syncMenuButton(entry);
    return;
  }
  entry.open = true;
  entry.view.setBounds(menuBounds(hostWin, entry.lastH));
  hostWin.contentView.addChildView(entry.view);
  syncMenuButton(entry);
  try { entry.view.webContents.focus(); } catch { /* ignore */ }
}

// 创建（并加载）菜单视图但不显示——启动后预热调用，用户首次点击时无任何加载等待
function ensureMenu(hostWin) {
  const rec = state.wins.get(hostWin.id);
  if (!rec || hostWin.isDestroyed()) return null;
  let entry = menuPool.get(hostWin.id);
  if (entry && entry.view.webContents.isDestroyed()) {
    menuPool.delete(hostWin.id);
    entry = null;
  }
  if (entry) return entry;
  const view = new WebContentsView({
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  entry = { view, host: hostWin, open: false, lastH: 700, lastHideAt: 0, incognito: !!rec.incognito, subWin: null, sub: null };
  menuPool.set(hostWin.id, entry);
  // 子菜单：透明无边框子窗口（WebContentsView 不支持透明背景，无法呈现圆角浮层），
  // 挂在宿主窗口之下池化预加载，展开零延迟；CSS 圆角与阴影真实生效。
  const subWin = new BrowserWindow({
    show: false,
    frame: false,
    transparent: true,
    resizable: false,
    skipTaskbar: true,
    parent: hostWin,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  subWin.setMenu(null);
  subWin.loadURL(`browser://menu/?incognito=${entry.incognito ? 1 : 0}&mode=sub`)
    .catch(() => {});
  entry.subWin = subWin;
  // 主窗移动/缩放/最小化时收起子菜单（位置依赖主窗）
  const park = () => closeSub(entry);
  hostWin.on('move', park);
  hostWin.on('minimize', park);
  view.webContents.loadURL(`browser://menu/?incognito=${rec.incognito ? 1 : 0}`)
    .catch(() => {});
  view.webContents.on('blur', () => setTimeout(() => {
    if (entry.sub && !entry.subWin.isDestroyed()) {
      const focused = entry.subWin.webContents.isFocused();
      const grace = Date.now() - (entry.subOpenedAt || 0) < 800;
      if (focused || grace) return;
    }
    hideMenu(entry);
  }, 140));
  // 窗口尺寸变化时重新定位（菜单贴右缘）并收起子菜单（其位置依赖主窗）
  hostWin.on('resize', () => {
    if (entry.open) {
      try { view.setBounds(menuBounds(hostWin, entry.lastH)); } catch { /* ignore */ }
      closeSub(entry);
    }
  });
  hostWin.once('closed', () => {
    entry.sub = null;
    try { if (entry.subWin && !entry.subWin.isDestroyed()) subWinDestroy(entry); } catch { /* ignore */ }
    try { view.webContents.destroy(); } catch { /* ignore */ }
    menuPool.delete(hostWin.id);
  });
  return entry;
}

function menuAct(hostWin, act) {
  const rec = state.wins.get(hostWin.id);
  const tm = rec && rec.tm;
  if (!tm) return;
  const goto = (url) => tm.createTab(url);
  const ui = (type, arg) => hostWin.webContents.send('pol:ui-command', { type, arg });
  switch (act) {
    case 'newtab': tm.createTab(); break;
    case 'incognito': createMainWindow({ incognito: true }); break;
    case 'history-page': goto('browser://history/'); break;
    case 'history-closed': goto('browser://history/#closed'); break;
    case 'downloads': goto('browser://downloads/'); break;
    case 'bookmark-add': ui('bookmark'); break;
    case 'bookmarks-page': goto('browser://bookmarks/'); break;
    case 'bookmarks-bar':
      updateSettings({ bookmarkBar: !(state.settings.bookmarkBar !== false) });
      break;
    case 'zoom-in': tm.zoomBy(1); break;
    case 'zoom-out': tm.zoomBy(-1); break;
    case 'fullscreen':
      try { hostWin.setFullScreen(!hostWin.isFullScreen()); } catch { /* ignore */ }
      break;
    case 'find': ui('find'); break;
    case 'print': tm.print(); break;
    case 'cleardata': goto('browser://settings/#clear'); break;
    case 'tasks': goto('browser://tasks/'); break;
    case 'extensions': goto('browser://extensions/'); break;
    case 'devtools': tm.devtools(); break;
    case 'settings': goto('browser://settings/'); break;
    case 'version': goto('browser://version/'); break;
    case 'quit': app.quit(); break;
    default: break;
  }
}

function computeEnv(settings) {
  const uaCustom = settings.userAgentMode === 'custom' && !!String(settings.customUserAgent || '').trim();
  const locale = settings.locale && settings.locale !== 'system' ? settings.locale : '';
  const langList = locale ? [locale, locale.split('-')[0]] : [];
  const acceptLanguages = locale ? ua.acceptLanguagesFor(locale) : ua.chromeAcceptLanguages();
  return {
    userAgent: uaCustom ? String(settings.customUserAgent).trim() : ua.chromeUA(),
    acceptLanguage: acceptLanguages.split(';')[0],             // zh-CN,zh（CDP 参数不接受 q 值）
    acceptLanguages,
    metadata: ua.userAgentMetadata(),
    timezone: settings.timezone || '',
    uaCustom,
    locale,                                                     // '' = 跟随系统
    allowlist: Array.isArray(settings.privacyAllowlist) ? settings.privacyAllowlist : [],
    fp: {                                                       // 指纹垫片配置
      locale,
      langs: langList,
      fontGuard: settings.fontGuard !== false,
      canvasNoise: settings.canvasNoise !== false,
      webrtc: settings.webrtcGuard !== false,
      seed: settings.fpSeed | 0 || 1,
    },
  };
}

function infoPayload() {
  return {
    app: 'Polaris 浏览器',
    chromeVersion: ua.chromeFullVersion(),
    electronVersion: process.versions.electron,
    nodeVersion: process.versions.node,
    platform: process.platform,
    userAgent: state.env ? state.env.userAgent : ua.chromeUA(),
    userAgentMetadata: state.env && state.env.metadata ? state.env.metadata : null,
    timezone: state.env ? state.env.timezone : '',
    systemTimezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    locale: state.env ? state.env.locale : '',
    // 操作系统语言（不受 --lang 影响），供设置页展示"跟随系统"项
    systemLocale: typeof app.getSystemLocale === 'function' ? app.getSystemLocale() : app.getLocale(),
  };
}

function publicSettings() {
  return { ...state.settings, info: infoPayload() };
}

// 三层保证 UA/请求头与 Chrome 一致:
//   1) session.setUserAgent + app.userAgentFallback  —— 网络层默认值
//   2) CDP Emulation.setUserAgentOverride(userAgentMetadata) —— JS 层 navigator + Sec-CH-UA 头
//   3) webRequest.onBeforeSendHeaders —— 出口请求头兜底改写
function updateSettings(patch, { broadcast = true } = {}) {
  const next = { ...state.settings, ...patch };
  state.settings = next;
  settingsStore.save(next);

  state.env = computeEnv(next);
  for (const ses of state.sessions) {
    ses.setUserAgent(state.env.userAgent, state.env.acceptLanguage);
    headers.install(ses, state.env);
  }
  app.userAgentFallback = state.env.userAgent;

  emulation.applyAll(state.env, webContents);

  // 时区变更：向所有已加载页面的全部 frame（含跨进程 iframe）实时下发垫片更新
  tzshim.sweepAll(webContents.getAllWebContents(), state.env.timezone, state.env.allowlist);
  // 语言/字体/Canvas 指纹垫片同理实时更新
  fpshim.sweepAll(webContents.getAllWebContents(), state.env.fp, state.env.allowlist);

  if (broadcast) {
    for (const wc of webContents.getAllWebContents()) {
      if (!wc.isDestroyed()) wc.send('pol:settings-changed', publicSettings());
    }
  }
}

function internalOnly(e) {
  let url = '';
  try {
    url = e.sender && !e.sender.isDestroyed() ? e.sender.getURL() : '';
  } catch { /* ignore */ }
  if (!url.startsWith('browser://')) throw new Error(`仅内部页面可调用该接口 (caller: ${url || 'unknown'})`);
}

function registerIpc() {
  // 任意 frame 的 preload 在 document_start 同步询问生效配置（时区 + 指纹防护）；
  // 按发送 frame 的 URL 判定白名单：命中站点不下发任何伪装（系统时区/语言、无垫片）
  ipcMain.on('pol:frame-tz', (e) => {
    const env = state.env;
    if (!env) {
      e.returnValue = { tz: '', fp: null };
      return;
    }
    let frameUrl = '';
    try { frameUrl = e.senderFrame.url; } catch { /* ignore */ }
    const allow = isAllowed(frameUrl, env.allowlist);
    e.returnValue = allow
      ? { tz: '', fp: { locale: '', langs: [], fontGuard: false, canvasNoise: false, webrtc: false, seed: env.fp.seed } }
      : { tz: env.timezone, fp: env.fp };
  });

  ipcMain.handle('pol:settings-get', (e) => { internalOnly(e); return publicSettings(); });
  ipcMain.handle('pol:settings-set', (e, patch) => {
    internalOnly(e);
    if (patch && typeof patch === 'object') {
      if (patch.timezone !== undefined && patch.timezone !== '' &&
        !Intl.supportedValuesOf('timeZone').includes(patch.timezone)) {
        throw new Error(`无效的时区 ID: ${patch.timezone}`);
      }
      if (patch.locale !== undefined && patch.locale !== 'system' &&
        !/^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$/.test(patch.locale)) {
        throw new Error(`无效的语言代码: ${patch.locale}`);
      }
      if (patch.privacyAllowlist !== undefined) {
        if (!Array.isArray(patch.privacyAllowlist)) throw new Error('白名单必须是域名数组');
        patch.privacyAllowlist = allowlistUtil.normalizeList(patch.privacyAllowlist);
      }
      updateSettings(patch);
    }
    return publicSettings();
  });
  ipcMain.handle('pol:timezones', (e) => { internalOnly(e); return Intl.supportedValuesOf('timeZone'); });
  ipcMain.handle('pol:info', (e) => { internalOnly(e); return infoPayload(); });
  ipcMain.handle('pol:app-quit', (e) => { internalOnly(e); app.quit(); });

  // ---- 扩展管理 ----
  const extOk = (r) => (r && typeof r === 'object' && 'ok' in r ? r : { ok: !!r });
  ipcMain.handle('pol:ext-list', (e) => { internalOnly(e); return extMgr.list(); });
  ipcMain.handle('pol:ext-install-store', async (e, input) => {
    internalOnly(e);
    try { return extOk({ ok: true, ...(await extMgr.installFromStore(input)) }); }
    catch (err) { return { ok: false, error: err.message }; }
  });
  ipcMain.handle('pol:ext-install-file', async (e) => {
    internalOnly(e);
    const r = dialog.showOpenDialogSync(state.win, {
      title: '选择扩展文件（.crx / .zip）',
      filters: [{ name: 'Chrome 扩展', extensions: ['crx', 'zip'] }],
      properties: ['openFile'],
    });
    if (!r || !r[0]) return { ok: false, canceled: true };
    try { return extOk({ ok: true, ...(await extMgr.installFromPath(r[0])) }); }
    catch (err) { return { ok: false, error: err.message }; }
  });
  ipcMain.handle('pol:ext-install-dir', async (e) => {
    internalOnly(e);
    const r = dialog.showOpenDialogSync(state.win, {
      title: '选择已解包的扩展目录（含 manifest.json）',
      properties: ['openDirectory'],
    });
    if (!r || !r[0]) return { ok: false, canceled: true };
    try { return extOk({ ok: true, ...(await extMgr.installFromPath(r[0])) }); }
    catch (err) { return { ok: false, error: err.message }; }
  });
  ipcMain.handle('pol:ext-remove', (e, id) => { internalOnly(e); return extMgr.remove(id); });
  ipcMain.handle('pol:ext-popup', (e, id, rect) => {
    internalOnly(e);
    return extMgr.openPopup(BrowserWindow.fromWebContents(e.sender), id, rect || {});
  });

  // 多窗口：按发送者所在窗口解析对应的 TabManager（普通窗口/隐身窗口各自独立）
  const recOf = (e) => {
    internalOnly(e);
    const w = BrowserWindow.fromWebContents(e.sender);
    const rec = w && state.wins.get(w.id);
    if (!rec) throw new Error('浏览器窗口尚未就绪');
    return rec;
  };
  const tm = (e) => recOf(e).tm;
  ipcMain.handle('pol:tab-create', (e, url) => tm(e).createTab(url || 'browser://newtab/', { active: true }));
  ipcMain.handle('pol:tab-close', (e, id) => tm(e).close(id));
  ipcMain.handle('pol:tab-activate', (e, id) => tm(e).activate(id));
  ipcMain.handle('pol:tab-navigate', (e, id, input) => tm(e).navigate(id, input));
  ipcMain.handle('pol:tab-navigate-current', (e, input) => tm(e).navigateCurrent(input));
  ipcMain.handle('pol:tab-back', (e, id) => tm(e).back(id));
  ipcMain.handle('pol:tab-forward', (e, id) => tm(e).forward(id));
  ipcMain.handle('pol:tab-reload', (e, id) => {
    try {
      diag.log(`ipc:tab-reload id=${id}`);
      tm(e).reload(id);
    } catch (err) {
      diag.log(`ipc:tab-reload ERROR ${err.message}`);
      throw err;
    }
  });
  ipcMain.handle('pol:tabs-snapshot', (e) => { diag.log('ipc:tabs-snapshot'); return tm(e).snapshot(); });
  // 外壳诊断打点通道（仅内部页面）：把按钮点击路径的现场写入诊断日志
  ipcMain.handle('pol:diag', (e, m) => {
    try {
      let url = '';
      try { url = e.sender.getURL(); } catch { /* ignore */ }
      diag.log(`ui(${url.slice(9, 30)}) ${String(m).slice(0, 200)}`);
    } catch { /* ignore */ }
    return true;
  });
  ipcMain.handle('pol:tab-stop', (e, id) => tm(e).stop(id));
  ipcMain.handle('pol:tab-devtools', (e) => tm(e).devtools());

  // ---- 标签页扩展能力：查找 / 打印 / 缩放 / 撤销关闭 ----
  ipcMain.handle('pol:tab-find', (e, text, opts) => { tm(e).find(text, opts || {}); });
  ipcMain.handle('pol:tab-find-stop', (e) => { tm(e).stopFind(); });
  ipcMain.handle('pol:tab-print', (e) => { tm(e).print(); });
  ipcMain.handle('pol:tab-zoom', (e, dir) => { tm(e).zoomBy(dir); });
  ipcMain.handle('pol:tab-zoom-set', (e, factor) => { tm(e).setZoom(factor); });
  ipcMain.handle('pol:tab-zoom-get', (e) => tm(e).getZoom());
  ipcMain.handle('pol:tab-reopen', (e) => tm(e).reopenClosed());

  // ---- ⋮ 菜单浮层 ----
  ipcMain.handle('pol:menu-open', (e) => {
    internalOnly(e);
    const w = BrowserWindow.fromWebContents(e.sender);
    if (w) openMenu(w);
  });
  ipcMain.handle('pol:menu-act', (e, act) => {
    internalOnly(e);
    const entry = menuEntryByAnySender(e.sender);
    if (!entry) return;
    // 父菜单页失焦时页面会自发 __close；若焦点只是转入子菜单（或处于展开宽限期）则忽略
    if (act === '__close' && e.sender === entry.view.webContents
      && entry.sub && !entry.subWin.isDestroyed()
      && (entry.subWin.webContents.isFocused() || Date.now() - (entry.subOpenedAt || 0) < 800)) {
      return;
    }
    const host = entry.host;
    hideMenu(entry);
    if (act && act !== '__close' && !host.isDestroyed()) menuAct(host, act);
  });
  // 菜单页上报面板实际高度（主页面/子页面切换时），浮层随之调整
  ipcMain.handle('pol:menu-size', (e, h) => {
    internalOnly(e);
    const height = Number(h);
    if (!height) return;
    // 子菜单页上报：自适应子菜单浮层高度
    for (const entry of menuPool.values()) {
      if (entry.sub && entry.subWin && !entry.subWin.isDestroyed() && entry.subWin.webContents === e.sender) {
        try {
          const cur = entry.subWin.getBounds();
          entry.subWin.setBounds({ ...cur, height: Math.max(60, Math.min(height, 400)) });
        } catch { /* ignore */ }
        return;
      }
    }
    const entry = menuEntryOfSender(e.sender);
    if (!entry) return;
    entry.lastH = height;
    if (entry.open && !entry.host.isDestroyed()) {
      try { entry.view.setBounds(menuBounds(entry.host, entry.lastH)); } catch { /* ignore */ }
    }
  });
  ipcMain.handle('pol:menu-sub', (e, name, itemTop) => {
    internalOnly(e);
    const entry = menuEntryOfSender(e.sender);
    if (!entry) return;
    if (name) openSub(entry, name, itemTop);
    else scheduleSubHide(entry);
  });
  ipcMain.handle('pol:menu-sub-hover', (e, on) => {
    internalOnly(e);
    const entry = menuEntryByAnySender(e.sender);
    if (!entry || !entry.sub) return;
    if (on) clearTimeout(entry.sub.timer);
    else scheduleSubHide(entry, 150);
  });

  // ---- 窗口 ----
  ipcMain.handle('pol:win-incognito', (e) => { internalOnly(e); createMainWindow({ incognito: true }); });
  ipcMain.handle('pol:win-fullscreen', (e) => {
    internalOnly(e);
    const w = BrowserWindow.fromWebContents(e.sender);
    if (w) {
      try { w.setFullScreen(!w.isFullScreen()); } catch { /* ignore */ }
    }
  });
  // 外壳上报自身实际高度（书签栏显示/隐藏时变化），内容区跟随
  ipcMain.handle('pol:ui-height', (e, n) => { recOf(e).tm.setChromeHeight(n); });

  // ---- 历史记录 ----
  ipcMain.handle('pol:history-list', (e, q) => { internalOnly(e); return history.list({ q }); });
  ipcMain.handle('pol:history-remove', (e, id) => { internalOnly(e); history.remove(id); });
  ipcMain.handle('pol:history-clear', (e) => { internalOnly(e); history.clear(); });
  ipcMain.handle('pol:history-closed', (e) => { internalOnly(e); return history.closedList(); });

  // ---- 书签 ----
  ipcMain.handle('pol:bm-list', (e) => { internalOnly(e); return bookmarks.list(); });
  ipcMain.handle('pol:bm-toggle', (e, info) => { internalOnly(e); return bookmarks.toggle(info || {}); });
  ipcMain.handle('pol:bm-remove', (e, id) => { internalOnly(e); bookmarks.remove(id); });
  ipcMain.handle('pol:bm-rename', (e, id, title) => { internalOnly(e); bookmarks.rename(id, title); });

  // ---- 下载 ----
  ipcMain.handle('pol:dl-list', (e) => { internalOnly(e); return dlMgr.list(); });
  ipcMain.handle('pol:dl-act', (e, id, action) => {
    internalOnly(e);
    try { return { ok: true, item: dlMgr.act(id, action) }; }
    catch (err) { return { ok: false, error: err.message }; }
  });

  // ---- 清除浏览数据 ----
  ipcMain.handle('pol:clear-data', async (e, sel) => {
    internalOnly(e);
    const opt = sel || {};
    if (opt.history) history.clear();
    if (opt.downloads) dlMgr.clearRecords();
    const storages = [];
    if (opt.cookies) storages.push('cookies');
    if (opt.siteData) storages.push('indexdb', 'localstorage', 'serviceworkers', 'websql', 'shadercache', 'cachestorage');
    for (const ses of state.sessions) {
      try {
        if (opt.cache) await ses.clearCache();
        if (storages.length) await ses.clearStorageData({ storages });
      } catch { /* ignore */ }
    }
    broadcastToBrowserPages('pol:dl-changed');
    return { ok: true };
  });

  // ---- 任务管理器 ----
  ipcMain.handle('pol:tasks', (e) => {
    internalOnly(e);
    return app.getAppMetrics().map((m) => ({
      pid: m.pid,
      type: m.type,
      memoryMB: +(m.memory.workingSetSize / 1024).toFixed(1),
      cpu: m.cpu ? +(+m.cpu.percent).toFixed(1) : 0,
    }));
  });
  ipcMain.handle('pol:task-kill', (e, pid) => {
    internalOnly(e);
    if (pid === process.pid) throw new Error('不能结束浏览器主进程');
    try { process.kill(pid); return { ok: true }; }
    catch (err) { throw new Error(`结束进程失败: ${err.message}`); }
  });
}

function createMainWindow({ incognito = false } = {}) {
  const ses = incognito ? session.fromPartition('polaris-incognito') : session.defaultSession;
  const win = new BrowserWindow({
    width: 1280,
    height: 840,
    minWidth: 680,
    minHeight: 480,
    show: false,
    title: incognito ? 'Polaris 隐身窗口' : 'Polaris 浏览器',
    backgroundColor: incognito ? '#35363a' : '#ffffff',
    autoHideMenuBar: true,
    // Chrome 式无边框：隐藏原生标题栏，改用 Windows 覆盖层绘制最小化/最大化/关闭按钮，
    // 颜色与标签栏背景一致（隐身窗口为深色配色）
    ...(process.platform === 'win32' ? {
      titleBarStyle: 'hidden',
      titleBarOverlay: incognito
        ? { color: '#35363a', symbolColor: '#e8eaed', height: 40 }
        : { color: '#dee1e6', symbolColor: '#444746', height: 40 },
    } : {}),
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  win.removeMenu();
  win.loadURL(incognito ? 'browser://shell/index.html?incognito=1' : 'browser://shell/index.html')
    .catch(() => {});
  win.once('ready-to-show', () => {
    win.show();
    // 菜单视图（含子菜单视图）后台预热：窗口就绪后异步加载，用户首次点击 ⋮ 时零等待
    setTimeout(() => {
      try { ensureMenu(win); } catch { /* ignore */ }
    }, 400);
  });

  const tm = new TabManager({
    window: win,
    getSettings: () => state.settings,
    session: incognito ? ses : null,
    incognito,
    actions: {
      incognito: () => createMainWindow({ incognito: true }),
      clearData: () => tm.createTab('browser://settings/#clear'),
      toggleBookmarkBar: () => updateSettings({ bookmarkBar: !(state.settings.bookmarkBar !== false) }),
    },
  });
  state.wins.set(win.id, { win, tm, incognito });
  if (!state.win || state.win.isDestroyed()) {
    state.win = win;
    state.tm = tm;
  }
  configureSession(ses); // 隐身会话同样应用 UA/请求头/权限/下载配置

  // 外壳聚焦时的快捷键（页面内快捷键由 TabManager 的 before-input-event 处理）
  win.webContents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    const ctrl = input.control || input.meta;
    const key = (input.key || '').toLowerCase();
    if (ctrl && key === 'l') {
      event.preventDefault();
      win.webContents.send('pol:ui-command', { type: 'focus-address' });
    } else if (ctrl && key === 't') {
      event.preventDefault();
      tm.createTab();
    } else if (ctrl && key === 'w') {
      event.preventDefault();
      if (tm.activeId != null) tm.close(tm.activeId);
    } else if (ctrl && key === 'f') {
      event.preventDefault();
      win.webContents.send('pol:ui-command', { type: 'find' });
    } else if (ctrl && key === 'p') {
      event.preventDefault();
      tm.print();
    } else if (ctrl && key === 'j') {
      event.preventDefault();
      tm.createTab('browser://downloads/');
    } else if (ctrl && key === 'd') {
      event.preventDefault();
      win.webContents.send('pol:ui-command', { type: 'bookmark' });
    } else if (ctrl && input.shift && key === 't') {
      event.preventDefault();
      tm.reopenClosed();
    } else if (ctrl && input.shift && key === 'n') {
      event.preventDefault();
      createMainWindow({ incognito: true });
    } else if (ctrl && input.shift && key === 'delete') {
      event.preventDefault();
      tm.createTab('browser://settings/#clear');
    } else if (ctrl && input.shift && key === 'b') {
      event.preventDefault();
      updateSettings({ bookmarkBar: !(state.settings.bookmarkBar !== false) });
    } else if (input.shift && key === 'escape') {
      event.preventDefault();
      tm.createTab('browser://tasks/');
    } else if (key === 'f12') {
      event.preventDefault();
      tm.devtools();
    } else if (key === 'f11') {
      event.preventDefault();
      try { win.setFullScreen(!win.isFullScreen()); } catch { /* ignore */ }
    }
  });

  win.on('closed', () => {
    state.wins.delete(win.id);
    if (state.win === win) {
      state.win = null;
      state.tm = null;
    }
  });
  // 窗口变化时关闭扩展弹窗（锚点失效）
  win.on('resize', () => extMgr.closePopup());
  win.on('move', () => extMgr.closePopup());
  return win;
}

// 会话统一配置：UA/请求头改写、字体枚举权限恒拒、下载接管与完成通知。
// 默认会话与各隐身会话都走这一入口（隐身会话 = 内存分区，磁盘零残留）。
function configureSession(ses) {
  if (state.sessions.has(ses)) return;
  state.sessions.add(ses);
  if (state.env) {
    ses.setUserAgent(state.env.userAgent, state.env.acceptLanguage);
    headers.install(ses, state.env);
  }
  const fontDenied = (p) => /font/i.test(String(p || ''));
  ses.setPermissionRequestHandler((_wc, permission, callback) => callback(!fontDenied(permission)));
  ses.setPermissionCheckHandler((_wc, permission) => !fontDenied(permission));
  dlMgr.init(ses, (reason, rec) => {
    broadcastToBrowserPages('pol:dl-changed');
    if (reason === 'done' && rec && rec.state === 'completed') {
      try {
        new Notification({ title: '下载完成', body: rec.filename }).show();
      } catch { /* ignore */ }
    }
  });
}

app.on('second-instance', () => {
  if (state.win && !state.win.isDestroyed()) {
    if (state.win.isMinimized()) state.win.restore();
    state.win.focus();
  }
});

app.whenReady().then(async () => {
  handleInternalScheme();
  registerIpc();

  history.init();
  bookmarks.init();

  state.settings = settingsStore.load();
  diag.reset([
    `tz=${state.settings.timezone || 'system'} locale=${state.settings.locale || 'system'}`,
    `lang-switch=${(state.settings.locale && state.settings.locale !== 'system') ? state.settings.locale : 'off'}`,
    `allowlist=${JSON.stringify(state.settings.privacyAllowlist || [])} argv-lang=${process.argv.filter((a) => a.startsWith('--lang')).join(',') || 'none'}`,
  ].join('\n'));
  updateSettings({}, { broadcast: false });
  configureSession(session.defaultSession);

  bookmarks.onChange(() => broadcastToBrowserPages('pol:bm-changed'));

  // 扩展子系统：仅默认会话；恢复已安装扩展；变更时通知所有内部页面（外壳刷新工具栏图标）
  extMgr.init(session.defaultSession);
  extMgr.onChange(() => {
    for (const wc of webContents.getAllWebContents()) {
      if (!wc.isDestroyed() && wc.getURL().startsWith('browser://')) wc.send('pol:ext-changed');
    }
  });
  extMgr.loadAll();

  // 所有页面（含未来新建的）在创建时与每次导航时注入 UA/ClientHints/时区覆盖；
  // frame 级事件（跨进程 iframe 加载/导航）作为时区垫片的兜底下发时机；
  // DevTools 打开期间会让出调试通道（emulation 暂停），关闭后立即恢复覆盖
  app.on('web-contents-created', (_e, wc) => {
    const apply = (urlArg) => {
      // did-start-navigation 事件自带目标 URL（此时 getURL() 仍是旧地址），用于白名单判定
      let url = typeof urlArg === 'string' ? urlArg : '';
      if (!url) {
        try { url = wc.getURL(); } catch { url = ''; }
      }
      if (url.startsWith('devtools://')) return; // DevTools 前端自身不做仿真
      if (state.env) emulation.apply(wc, state.env, url);
    };
    apply();
    wc.on('did-start-navigation', (_e2, url) => apply(url));
    wc.on('dom-ready', () => apply());
    wc.on('devtools-closed', () => setTimeout(apply, 120));

    let sweepTimer = null;
    const bump = () => {
      clearTimeout(sweepTimer);
      sweepTimer = setTimeout(() => {
        if (wc.isDestroyed() || !state.env) return;
        tzshim.sweepWc(wc, state.env.timezone, state.env.allowlist);
        fpshim.sweepWc(wc, state.env.fp, state.env.allowlist);
      }, 250);
    };
    wc.on('did-frame-navigate', bump);
    wc.on('did-frame-finish-load', bump);
    wc.on('dom-ready', bump);
  });

  if (IS_SMOKE) {
    await smoke.run({ updateSettings, createMainWindow, history, bookmarks });
    return;
  }

  const win = createMainWindow();
  const firstRec = state.wins.get(win.id);
  state.tm = firstRec.tm;
  // 初始标签等外壳加载完成再创建（否则 created/activated 事件早于外壳订阅发出；
  // 外壳另有 tabs-snapshot 自愈兜底，此处时序修正为双保险）
  const openHome = () => {
    if (state.tm) state.tm.createTab(state.settings.homepage || 'browser://newtab/');
  };
  if (win.webContents.isLoadingMainFrame()) win.webContents.once('did-finish-load', openHome);
  else openHome();

  if (IS_SHOT) {
    require('./shot').run({ win, tm: state.tm });
    return;
  }

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      const w2 = createMainWindow();
      const tm2 = state.wins.get(w2.id).tm;
      const open2 = () => tm2.createTab(state.settings.homepage || 'browser://newtab/');
      if (w2.webContents.isLoadingMainFrame()) w2.webContents.once('did-finish-load', open2);
      else open2();
    }
  });
});

app.on('window-all-closed', () => {
  app.quit();
});

module.exports = {
  updateSettings,
  createMainWindow,
  getWindowRec: (win) => state.wins.get(win && win.id),
  openMenu,
  closeMenu,
  getMenuWc: (win) => {
    const e = menuPool.get(win && win.id);
    return e && !e.view.webContents.isDestroyed() ? e.view.webContents : null;
  },
  getSubWc: (win) => {
    const e = menuPool.get(win && win.id);
    return e && e.subWin && !e.subWin.isDestroyed() ? e.subWin.webContents : null;
  },
  isMenuOpen: (win) => {
    const e = menuPool.get(win && win.id);
    return !!(e && e.open && !e.view.webContents.isDestroyed());
  },
};
