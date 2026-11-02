'use strict';
const { contextBridge, ipcRenderer, webFrame } = require('electron');

// 时区一致性 + 指纹防护：每个 frame（含跨站跨进程 iframe）在 document_start 注入。
//   - 时区：与配置目标不一致时（CDP 覆盖触达不到的 frame）注入 Date/Intl 时区垫片
//   - 指纹：语言 getter / 字体测量量化 / Canvas 种子化扰动（脚本内部自行判断启用项）
// 对外部页面不暴露任何接口，仅内部改写页面行为。
try {
  const info = ipcRenderer.sendSync('pol:frame-tz');
  if (info && info.tz) {
    const nativeTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (nativeTz !== info.tz) {
      const { buildShim } = require('../main/tz-shim');
      webFrame.executeJavaScript(buildShim(info.tz), false).catch(() => {});
    }
  }
  const fp = info && info.fp;
  if (fp && (fp.locale || fp.fontGuard !== false || fp.canvasNoise !== false)) {
    const { buildScript } = require('../main/fp-shim');
    webFrame.executeJavaScript(buildScript(fp), false).catch(() => {});
  }
} catch { /* 垫片失败不影响页面加载 */ }

// 只有内部页面（browser:// 协议）才暴露能力；
// 外部网页（http/https 等）不会注入任何自定义对象，避免形成浏览器指纹。
if (typeof location !== 'undefined' && location.protocol === 'browser:') {
  const invoke = (channel, ...args) => ipcRenderer.invoke(channel, ...args);
  const subscribe = (channel, cb) => {
    const listener = (_e, data) => cb(data);
    ipcRenderer.on(channel, listener);
    return () => ipcRenderer.removeListener(channel, listener);
  };

  contextBridge.exposeInMainWorld('polaris', {
    tabs: {
      create: (url) => invoke('pol:tab-create', url),
      close: (id) => invoke('pol:tab-close', id),
      activate: (id) => invoke('pol:tab-activate', id),
      navigate: (id, input) => invoke('pol:tab-navigate', id, input),
      navigateCurrent: (input) => invoke('pol:tab-navigate-current', input),
      back: (id) => invoke('pol:tab-back', id),
      forward: (id) => invoke('pol:tab-forward', id),
      reload: (id) => invoke('pol:tab-reload', id),
      stop: (id) => invoke('pol:tab-stop', id),
      devtools: () => invoke('pol:tab-devtools'),
      snapshot: () => invoke('pol:tabs-snapshot'),
      find: (text, opts) => invoke('pol:tab-find', text, opts),
      stopFind: () => invoke('pol:tab-find-stop'),
      print: () => invoke('pol:tab-print'),
      zoomBy: (dir) => invoke('pol:tab-zoom', dir),
      zoomSet: (factor) => invoke('pol:tab-zoom-set', factor),
      zoomGet: () => invoke('pol:tab-zoom-get'),
      reopen: () => invoke('pol:tab-reopen'),
    },
    settings: {
      get: () => invoke('pol:settings-get'),
      set: (patch) => invoke('pol:settings-set', patch),
      timezones: () => invoke('pol:timezones'),
    },
    win: {
      incognito: () => invoke('pol:win-incognito'),
      fullscreen: () => invoke('pol:win-fullscreen'),
    },
    menu: {
      open: () => invoke('pol:menu-open'),
      act: (a) => invoke('pol:menu-act', a),
      size: (h) => invoke('pol:menu-size', h),
      sub: (name, itemTop) => invoke('pol:menu-sub', name, itemTop),
      subHover: (on) => invoke('pol:menu-sub-hover', on),
      onSub: (cb) => subscribe('pol:sub-show', cb),
    },
    diag: (m) => invoke('pol:diag', m),
    ui: {
      setHeight: (n) => invoke('pol:ui-height', n),
    },
    history: {
      list: (q) => invoke('pol:history-list', q),
      remove: (id) => invoke('pol:history-remove', id),
      clear: () => invoke('pol:history-clear'),
      closed: () => invoke('pol:history-closed'),
    },
    bookmarks: {
      list: () => invoke('pol:bm-list'),
      toggle: (info) => invoke('pol:bm-toggle', info),
      remove: (id) => invoke('pol:bm-remove', id),
      rename: (id, title) => invoke('pol:bm-rename', id, title),
    },
    downloads: {
      list: () => invoke('pol:dl-list'),
      act: (id, action) => invoke('pol:dl-act', id, action),
    },
    clearData: (sel) => invoke('pol:clear-data', sel),
    tasks: {
      list: () => invoke('pol:tasks'),
      kill: (pid) => invoke('pol:task-kill', pid),
    },
    info: () => invoke('pol:info'),
    quit: () => invoke('pol:app-quit'),
    ext: {
      list: () => invoke('pol:ext-list'),
      installStore: (input) => invoke('pol:ext-install-store', input),
      installFile: () => invoke('pol:ext-install-file'),
      installDir: () => invoke('pol:ext-install-dir'),
      remove: (id) => invoke('pol:ext-remove', id),
      popup: (id, rect) => invoke('pol:ext-popup', id, rect),
    },
    onTabEvent: (cb) => subscribe('pol:tab-event', cb),
    onUiCommand: (cb) => subscribe('pol:ui-command', cb),
    onSettingsChanged: (cb) => subscribe('pol:settings-changed', cb),
    onExtChanged: (cb) => subscribe('pol:ext-changed', cb),
    onBmChanged: (cb) => subscribe('pol:bm-changed', cb),
    onDlChanged: (cb) => subscribe('pol:dl-changed', cb),
  });
}
