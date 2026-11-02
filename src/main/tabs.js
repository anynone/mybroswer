'use strict';
const path = require('path');
const { Menu, clipboard, WebContentsView } = require('electron');
const { resolveInput } = require('./navigate');
const history = require('./history');
const diag = require('./diaglog');

// 外壳基础高度（标签栏 40px + 工具栏 44px）；书签栏显示时由外壳通过
// setChromeHeight 上报实际高度（基础 + 30）
const CHROME_HEIGHT = 84;

// Chrome 的缩放档位
const ZOOM_STEPS = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

let seq = 0;

function navApi(wc) {
  if (wc.navigationHistory) return wc.navigationHistory;
  return {
    canGoBack: () => wc.canGoBack(),
    canGoForward: () => wc.canGoForward(),
  };
}

class TabManager {
  constructor({ window, getSettings, session = null, incognito = false, actions = {} }) {
    this.win = window;
    this.getSettings = getSettings;
    this.session = session;         // null = 默认会话；隐身窗口传独立内存会话
    this.incognito = incognito;
    this.actions = actions;         // { incognito(), clearData(), toggleBookmarkBar() }
    this.tabs = new Map();
    this.activeId = null;
    this.chromeHeight = CHROME_HEIGHT;
    this.findText = '';
    window.on('resize', () => this.layout());
  }

  send(evt) {
    const w = this.win;
    if (w && !w.isDestroyed()) w.webContents.send('pol:tab-event', evt);
  }

  ui(type, arg) {
    const w = this.win;
    if (w && !w.isDestroyed()) w.webContents.send('pol:ui-command', { type, arg });
  }

  setChromeHeight(n) {
    const h = Math.max(CHROME_HEIGHT, Math.min(200, Number(n) || CHROME_HEIGHT));
    if (h === this.chromeHeight) return;
    this.chromeHeight = h;
    this.layout();
  }

  get(id) {
    return this.tabs.get(id);
  }

  get activeTab() {
    return this.tabs.get(this.activeId) || null;
  }

  createTab(url = 'browser://newtab/', { active = true } = {}) {
    if (!this.win || this.win.isDestroyed()) return null;
    const id = ++seq;
    const view = new WebContentsView({
      webPreferences: {
        preload: path.join(__dirname, '..', 'preload', 'index.js'),
        contextIsolation: true,
        nodeIntegration: false,
        nodeIntegrationInSubFrames: true, // preload 进入所有 iframe（含跨进程），用于时区垫片
        sandbox: false,
        ...(this.session ? { session: this.session } : {}),
      },
    });
    const tab = { id, view, url: '', title: '新标签页', favicon: '', loading: false };
    this.tabs.set(id, tab);
    this.win.contentView.addChildView(view);
    this.bind(tab);
    this.send({ type: 'created', id, url: '', title: tab.title });
    view.webContents.loadURL(url).catch(() => { /* 已由 did-fail-load 处理 */ });
    if (active) this.activate(id);
    else this.layout();
    return id;
  }

  bind(tab) {
    const wc = tab.view.webContents;

    // target=_blank / Ctrl+点击 / 中键点击 → 新标签页打开（含扩展自身的选项页）
    wc.setWindowOpenHandler(({ url, disposition }) => {
      if (/^(https?|browser|file|chrome-extension):/i.test(url)) {
        this.createTab(url, { active: disposition !== 'background-tab' });
      }
      return { action: 'deny' };
    });

    wc.on('did-start-loading', () => {
      tab.loading = true;
      diag.log(`loading+ id=${tab.id} ${tab.url.slice(0, 60)}`);
      this.emitState(tab);
    });
    wc.on('did-stop-loading', () => {
      tab.loading = false;
      diag.log(`loading- id=${tab.id}`);
      this.emitState(tab);
    });
    wc.on('did-navigate', (_e, url) => {
      tab.url = url;
      this.emitState(tab);
      if (!this.incognito) history.record(url, tab.title, tab.favicon);
    });
    wc.on('did-navigate-in-page', (_e, url) => {
      tab.url = url;
      this.emitState(tab);
      if (!this.incognito) history.record(url, tab.title, tab.favicon);
    });
    wc.on('page-title-updated', (_e, title) => {
      tab.title = title || tab.url || '新标签页';
      this.send({ type: 'title', id: tab.id, title: tab.title });
      this.syncTitle();
      if (!this.incognito) history.record(tab.url, tab.title, tab.favicon);
    });
    wc.on('page-favicon-updated', (_e, icons) => {
      tab.favicon = icons && icons.length ? icons[icons.length - 1] : '';
      this.send({ type: 'favicon', id: tab.id, favicon: tab.favicon });
    });
    wc.on('did-fail-load', (_e, code, desc, url, isMain) => {
      if (!isMain || code === -3) return; // -3 = 用户取消
      const q = new URLSearchParams({ code: String(code), desc: desc || '', url: url || tab.url || '' });
      wc.loadURL(`browser://error/?${q.toString()}`).catch(() => {});
    });
    wc.on('render-process-gone', () => {
      if (wc.isDestroyed()) return;
      const q = new URLSearchParams({ code: 'crash', desc: '页面渲染进程异常退出', url: tab.url || '' });
      wc.loadURL(`browser://error/?${q.toString()}`).catch(() => {});
    });
    wc.on('context-menu', (_e, params) => this.contextMenu(tab, params));
    wc.on('before-input-event', (event, input) => this.shortcuts(tab, event, input));
    // 页内查找结果回传外壳查找栏（序号/总数）
    wc.on('found-in-page', (_e, result) => {
      this.send({
        type: 'found',
        id: tab.id,
        active: result ? result.activeMatchOrdinal : 0,
        total: result ? result.matches : 0,
      });
    });
  }

  // 打开 DevTools 前让出 CDP 调试通道（UA/时区覆盖用的 debugger 与 DevTools 前端互斥），
  // DevTools 关闭后由主进程 devtools-closed 钩子自动恢复覆盖
  yieldDebugger(wc) {
    try { if (wc.debugger.isAttached()) wc.debugger.detach(); } catch { /* ignore */ }
  }

  openDevToolsFor(tab) {
    const wc = tab.view.webContents;
    if (wc.isDestroyed()) return;
    this.yieldDebugger(wc);
    try { wc.openDevTools({ mode: 'detach' }); } catch { /* ignore */ }
  }

  shortcuts(tab, event, input) {
    if (input.type !== 'keyDown') return;
    const wc = tab.view.webContents;
    const ctrl = input.control || input.meta;
    const key = (input.key || '').toLowerCase();
    if (ctrl && key === 'l') {
      event.preventDefault();
      this.ui('focus-address');
    } else if (ctrl && key === 't') {
      event.preventDefault();
      this.createTab();
    } else if (ctrl && key === 'w') {
      event.preventDefault();
      this.close(tab.id);
    } else if (ctrl && key === 'r') {
      event.preventDefault();
      if (input.shift) {
        // Ctrl+Shift+R 强制刷新（绕过缓存）
        diag.log(`hard-reload(kbd) id=${tab.id}`);
        try { tab.view.webContents.reloadIgnoringCache(); } catch { /* ignore */ }
      } else {
        this.reload(tab.id);
      }
    } else if (key === 'f5') {
      event.preventDefault();
      if (input.shift) {
        // Shift+F5 强制刷新（Chrome 同款）
        diag.log(`hard-reload(f5) id=${tab.id}`);
        try { tab.view.webContents.reloadIgnoringCache(); } catch { /* ignore */ }
      } else {
        this.reload(tab.id);
      }
    } else if (ctrl && input.shift && key === 'i') {
      event.preventDefault();
      this.openDevToolsFor(tab);
    } else if (key === 'f12') {
      event.preventDefault();
      this.openDevToolsFor(tab);
    } else if (ctrl && key === 'f') {
      event.preventDefault();
      this.ui('find');
    } else if (ctrl && key === 'p') {
      event.preventDefault();
      this.print();
    } else if (ctrl && key === 'j') {
      event.preventDefault();
      this.createTab('browser://downloads/');
    } else if (ctrl && key === 'd') {
      event.preventDefault();
      this.ui('bookmark');
    } else if (ctrl && input.shift && key === 't') {
      event.preventDefault();
      this.reopenClosed();
    } else if (ctrl && input.shift && key === 'n') {
      event.preventDefault();
      if (this.actions.incognito) this.actions.incognito();
    } else if (ctrl && input.shift && key === 'delete') {
      event.preventDefault();
      this.createTab('browser://settings/#clear');
    } else if (ctrl && input.shift && key === 'b') {
      event.preventDefault();
      if (this.actions.toggleBookmarkBar) this.actions.toggleBookmarkBar();
    } else if (input.shift && key === 'escape') {
      event.preventDefault();
      this.createTab('browser://tasks/');
    } else if (key === 'f11') {
      event.preventDefault();
      try { this.win.setFullScreen(!this.win.isFullScreen()); } catch { /* ignore */ }
    } else if (key === 'escape' && tab.loading) {
      // Chrome 行为：加载中 Esc = 停止加载（非加载态的 Esc 交给页面处理）
      event.preventDefault();
      this.stop(tab.id);
    } else if (ctrl && (key === '=' || key === '+')) {
      event.preventDefault();
      this.zoomBy(1);
    } else if (ctrl && key === '-') {
      event.preventDefault();
      this.zoomBy(-1);
    } else if (ctrl && key === '0') {
      event.preventDefault();
      this.setZoom(1);
    }
  }

  // ---- 页内查找 ----
  find(text, { forward = true, findNext = false } = {}) {
    const t = this.activeTab;
    if (!t) return;
    if (!text) { this.stopFind(); return; }
    this.findText = text;
    t.view.webContents.findInPage(text, { forward, findNext, matchCase: false });
  }

  stopFind() {
    this.findText = '';
    const t = this.activeTab;
    if (t && !t.view.webContents.isDestroyed()) t.view.webContents.stopFindInPage('clearSelection');
    this.send({ type: 'found', id: this.activeId, active: 0, total: 0 });
  }

  // ---- 打印 ----
  print() {
    const t = this.activeTab;
    if (t && !t.view.webContents.isDestroyed()) {
      try { t.view.webContents.print(); } catch { /* ignore */ }
    }
  }

  // ---- 缩放 ----
  getZoom() {
    const t = this.activeTab;
    try { return t ? t.view.webContents.getZoomFactor() : 1; } catch { return 1; }
  }

  setZoom(factor) {
    const t = this.activeTab;
    if (!t) return;
    try { t.view.webContents.setZoomFactor(factor); } catch { /* ignore */ }
    this.emitState(t);
  }

  // dir: 1 放大 / -1 缩小，沿 Chrome 档位步进
  zoomBy(dir) {
    const cur = this.getZoom();
    const next = dir > 0
      ? ZOOM_STEPS.find((z) => z > cur + 0.001) || ZOOM_STEPS[ZOOM_STEPS.length - 1]
      : [...ZOOM_STEPS].reverse().find((z) => z < cur - 0.001) || ZOOM_STEPS[0];
    this.setZoom(next);
  }

  // ---- 撤销关闭（Ctrl+Shift+T） ----
  reopenClosed() {
    const it = history.popClosed();
    if (it) this.createTab(it.url);
    return !!it;
  }

  contextMenu(tab, params) {
    const nav = navApi(tab.view.webContents);
    const tpl = [];
    if (params.linkURL) {
      tpl.push(
        { label: '在新标签页中打开链接', click: () => this.createTab(params.linkURL, { active: false }) },
        { label: '复制链接地址', click: () => clipboard.writeText(params.linkURL) },
        { type: 'separator' },
      );
    }
    if (params.selectionText && params.selectionText.trim()) {
      tpl.push({ label: '复制', role: 'copy' }, { type: 'separator' });
    }
    tpl.push(
      { label: '后退', enabled: nav.canGoBack(), click: () => this.back(tab.id) },
      { label: '前进', enabled: nav.canGoForward(), click: () => this.forward(tab.id) },
      { type: 'separator' },
      { label: '重新加载', click: () => this.reload(tab.id) },
    );
    // DevTools 需要独占调试通道，先让出仿真用的 debugger
    tpl.push({ label: '检查元素', click: () => {
      this.yieldDebugger(tab.view.webContents);
      try { tab.view.webContents.inspectElement(params.x, params.y); } catch { /* ignore */ }
    } });
    Menu.buildFromTemplate(tpl).popup({ window: this.win });
  }

  emitState(tab) {
    const nav = navApi(tab.view.webContents);
    let zoom = 1;
    try { zoom = tab.view.webContents.getZoomFactor(); } catch { /* ignore */ }
    this.send({
      type: 'state',
      id: tab.id,
      url: tab.url,
      title: tab.title,
      loading: tab.loading,
      canGoBack: !!nav.canGoBack(),
      canGoForward: !!nav.canGoForward(),
      zoom,
    });
    this.syncTitle();
  }

  syncTitle() {
    const t = this.activeTab;
    if (t && this.win && !this.win.isDestroyed()) {
      this.win.setTitle(`${t.title || '新标签页'} - Polaris 浏览器`);
    }
  }

  // 外壳初始化/重载后主动拉取：完整标签状态快照（防事件早于订阅丢失）
  snapshot() {
    return {
      activeId: this.activeId,
      tabs: [...this.tabs.values()].map((t) => ({
        id: t.id, url: t.url, title: t.title, favicon: t.favicon,
        loading: t.loading,
        canGoBack: (() => { try { return navApi(t.view.webContents).canGoBack(); } catch { return false; } })(),
        canGoForward: (() => { try { return navApi(t.view.webContents).canGoForward(); } catch { return false; } })(),
      })),
    };
  }

  activate(id) {
    const t = this.tabs.get(id);
    if (!t) return;
    this.activeId = id;
    for (const other of this.tabs.values()) other.view.setVisible(false);
    t.view.setVisible(true);
    this.layout();
    this.send({ type: 'activated', id });
    const wc = t.view.webContents;
    if (!wc.isDestroyed()) wc.focus();
    this.syncTitle();
  }

  close(id) {
    const t = this.tabs.get(id);
    if (!t) return;
    this.tabs.delete(id);
    if (!this.incognito && t.url) history.pushClosed(t.url, t.title);
    try { this.win.contentView.removeChildView(t.view); } catch { /* ignore */ }
    try { t.view.webContents.destroy(); } catch { /* ignore */ }
    this.send({ type: 'removed', id });
    if (this.activeId === id) {
      const rest = [...this.tabs.keys()];
      if (rest.length) this.activate(rest[rest.length - 1]);
      else if (this.win && !this.win.isDestroyed()) this.win.close();
    }
  }

  navigate(id, input) {
    const t = this.tabs.get(id);
    if (!t) return;
    const url = resolveInput(input, (this.getSettings() || {}).searchEngine);
    if (!url) return;
    t.view.webContents.loadURL(url).catch(() => {});
  }

  navigateCurrent(input) {
    if (this.activeId != null) this.navigate(this.activeId, input);
  }

  back(id) {
    const t = this.tabs.get(id || this.activeId);
    if (t) t.view.webContents.goBack();
  }

  forward(id) {
    const t = this.tabs.get(id || this.activeId);
    if (t) t.view.webContents.goForward();
  }

  reload(id) {
    const t = this.tabs.get(id || this.activeId);
    diag.log(`reload(id=${id}) tab=${!!t} ${t ? t.url.slice(0, 60) : ''}`);
    if (t) t.view.webContents.reload();
  }

  stop(id) {
    const t = this.tabs.get(id || this.activeId);
    if (t) t.view.webContents.stop();
  }

  devtools() {
    const t = this.activeTab;
    if (t) this.openDevToolsFor(t);
  }

  layout() {
    if (!this.win || this.win.isDestroyed()) return;
    const [w, h] = this.win.getContentSize();
    const rect = { x: 0, y: this.chromeHeight, width: Math.max(0, w), height: Math.max(0, h - this.chromeHeight) };
    for (const t of this.tabs.values()) t.view.setBounds(rect);
  }
}

module.exports = TabManager;
