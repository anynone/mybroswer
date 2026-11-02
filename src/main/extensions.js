'use strict';
// Chrome 扩展支持：
//   - 安装来源：Chrome 应用商店（URL/ID 在线下载 CRX）、本地 .crx/.zip、已解包目录
//   - 底层：session.loadExtension（Chromium 内核的扩展子系统，支持扩展 API 子集）
//   - 持久化：userData/extensions.json 记录安装目录，启动时自动恢复
//   - 工具栏弹窗：browserAction/action 的 default_popup 在锚定的小视图中打开
const fs = require('fs');
const path = require('path');
const { net, WebContentsView } = require('electron');
const AdmZip = require('adm-zip');
const ua = require('./ua');

let ses = null;               // 主 session
let extRoot = '';             // 扩展安装根目录
let stateFile = '';           // 持久化文件
let installed = [];           // [{ path }]
let changeListeners = [];
let popupView = null;
let popupWin = null;

const CHROME_VERSION = ua.chromeFullVersion();

function init(session) {
  ses = session;
  extRoot = path.join(ses.getUserDataPath?.() || '', 'extensions');
  if (!extRoot) {
    const { app } = require('electron');
    extRoot = path.join(app.getPath('userData'), 'extensions');
  }
  fs.mkdirSync(extRoot, { recursive: true });
  stateFile = path.join(path.dirname(extRoot), 'extensions.json');
  try {
    installed = JSON.parse(fs.readFileSync(stateFile, 'utf-8'));
    if (!Array.isArray(installed)) installed = [];
  } catch { installed = []; }
}

function persist() {
  try { fs.writeFileSync(stateFile, JSON.stringify(installed, null, 2)); } catch { /* ignore */ }
}

function notify() {
  for (const cb of changeListeners) {
    try { cb(); } catch { /* ignore */ }
  }
}

function onChange(cb) { changeListeners.push(cb); }

// ---- CRX 解析：剥离 CRX2/CRX3 头部得到 ZIP ----
function crxToZip(buf) {
  const magic = buf.toString('ascii', 0, 4);
  if (magic !== 'Cr24') return null;
  const ver = buf.readUInt32LE(4);
  if (ver === 3) return buf.subarray(12 + buf.readUInt32LE(8));
  if (ver === 2) return buf.subarray(16 + buf.readUInt32LE(8) + buf.readUInt32LE(12));
  throw new Error(`不支持的 CRX 版本: ${ver}`);
}

// ---- 安装（内存中的 crx/zip 数据） ----
async function installFromBuffer(buf, originName) {
  if (!ses) throw new Error('扩展子系统未初始化');
  let zipBuf = null;
  if (buf.length > 4 && buf[0] === 0x50 && buf[1] === 0x4b) zipBuf = buf; // "PK" ZIP
  else zipBuf = crxToZip(buf);
  if (!zipBuf) throw new Error(`无法识别的文件格式（需要 .crx 或 .zip）：${originName || ''}`);

  const dir = fs.mkdtempSync(path.join(extRoot, 'ext-'));
  new AdmZip(Buffer.from(zipBuf)).extractAllTo(dir, true);

  // 某些打包有一层包裹目录，定位 manifest.json 所在的根目录
  let base = dir;
  if (!fs.existsSync(path.join(dir, 'manifest.json'))) {
    let found = null;
    for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
      if (ent.isDirectory() && fs.existsSync(path.join(dir, ent.name, 'manifest.json'))) { found = path.join(dir, ent.name); break; }
    }
    if (!found) throw new Error('压缩包中未找到 manifest.json，不是有效的 Chrome 扩展');
    base = found;
  }

  const ext = await ses.loadExtension(base, { allowFileAccess: true });
  if (!installed.some((x) => x.path === base)) {
    installed.push({ path: base, from: originName || 'local', installedAt: Date.now() });
    persist();
  }
  notify();
  return ext;
}

// ---- 安装（本地 .crx/.zip 文件或已解包目录） ----
async function installFromPath(p) {
  if (!ses) throw new Error('扩展子系统未初始化');
  let st;
  try { st = fs.statSync(p); } catch { throw new Error(`路径不存在: ${p}`); }
  if (st.isDirectory()) {
    const ext = await ses.loadExtension(p, { allowFileAccess: true });
    if (!installed.some((x) => x.path === p)) {
      installed.push({ path: p, from: 'unpacked', installedAt: Date.now() });
      persist();
    }
    notify();
    return ext;
  }
  return installFromBuffer(fs.readFileSync(p), path.basename(p));
}

// ---- 从 Chrome 应用商店安装（输入 URL 或 32 位扩展 ID） ----
function parseStoreId(input) {
  const m = String(input || '').trim().match(/[a-p]{32}/);
  return m ? m[0] : null;
}

async function installFromStore(input) {
  const id = parseStoreId(input);
  if (!id) throw new Error('无法识别扩展 ID：请粘贴 Chrome 应用商店的扩展链接（形如 https://chromewebstore.google.com/detail/…/<ID>）');
  const url = 'https://clients2.google.com/service/update2/crx?response=redirect'
    + '&os=win&arch=x64&os_arch=x64&nacl_arch=x86-64'
    + '&prod=chromiumcrx&prodchannel=stable&lang=zh-CN'
    + `&prodversion=${CHROME_VERSION}&acceptformat=crx2,crx3`
    + `&x=id%3D%22${id}%22%26installsource%3Dondemand%26uc`;
  const res = await net.fetch(url, { redirect: 'follow' });
  if (!res.ok) throw new Error(`商店下载失败（HTTP ${res.status}）。国内网络可能无法访问 Google 服务，可手动下载 .crx 后本地安装`);
  const buf = Buffer.from(await res.arrayBuffer());
  if (buf.length < 1024) throw new Error('商店返回内容异常，下载失败');
  return installFromBuffer(buf, `store:${id}`);
}

// ---- 列表 / 删除 / 启动恢复 ----
function iconDataUrl(ext) {
  const icons = (ext.manifest && ext.manifest.icons) || {};
  const sizes = Object.keys(icons).map(Number).filter((n) => !isNaN(n)).sort((a, b) => a - b);
  const pick = sizes.find((s) => s >= 32) || sizes[sizes.length - 1];
  if (!pick) return null;
  const file = path.join(ext.path, icons[pick]);
  try {
    const b = fs.readFileSync(file);
    const ext2 = path.extname(file).toLowerCase();
    const mime = ext2 === '.svg' ? 'image/svg+xml' : (ext2 === '.jpg' || ext2 === '.jpeg') ? 'image/jpeg' : 'image/png';
    return `data:${mime};base64,${b.toString('base64')}`;
  } catch { return null; }
}

function list() {
  if (!ses) return [];
  const out = [];
  for (const ext of ses.getAllExtensions()) {
    const action = ext.manifest?.action || ext.manifest?.browser_action || {};
    out.push({
      id: ext.id,
      name: ext.manifest?.name || ext.id,
      version: ext.manifest?.version || '',
      description: ext.manifest?.description || '',
      icon: iconDataUrl(ext),
      popup: action.default_popup || '',
      options: ext.manifest?.options_page || (ext.manifest?.options_ui && ext.manifest.options_ui.page) || '',
      path: ext.path,
    });
  }
  return out;
}

function remove(id) {
  if (!ses) return { ok: false, error: '扩展子系统未初始化' };
  let extPath = null;
  try {
    const ext = ses.getAllExtensions().find((e) => e.id === id);
    extPath = ext ? ext.path : null;
    ses.removeExtension(id);
  } catch (err) {
    return { ok: false, error: err.message };
  }
  if (extPath) {
    installed = installed.filter((x) => x.path !== extPath);
    persist();
  }
  notify();
  closePopup();
  return { ok: true };
}

async function loadAll() {
  if (!ses) return;
  for (const item of [...installed]) {
    try {
      if (!fs.existsSync(path.join(item.path, 'manifest.json'))) throw new Error('目录不存在');
      await ses.loadExtension(item.path, { allowFileAccess: true });
    } catch (err) {
      console.warn(`[extensions] 恢复扩展失败 ${item.path}: ${err.message}`);
    }
  }
  notify();
}

// ---- 工具栏弹窗（browserAction / action 的 popup） ----
function closePopup() {
  if (popupView) {
    const v = popupView;
    popupView = null;
    try { if (popupWin && !popupWin.isDestroyed()) popupWin.contentView.removeChildView(v); } catch { /* ignore */ }
    try { v.webContents.destroy(); } catch { /* ignore */ }
    popupWin = null;
  }
}

function openPopup(win, id, rect) {
  if (!ses || !win || win.isDestroyed()) return { ok: false, error: '窗口不可用' };
  const ext = ses.getAllExtensions().find((e) => e.id === id);
  if (!ext) return { ok: false, error: '扩展不存在' };
  const action = ext.manifest?.action || ext.manifest?.browser_action || {};
  const popupPath = action.default_popup;
  if (!popupPath) return { ok: false, error: '该扩展未提供工具栏弹窗' };
  closePopup();

  const width = Math.min(400, Math.max(280, action.default_width || 360));
  const [winW, winH] = win.getContentSize();
  const height = Math.min(winH - 100, Math.max(200, action.default_height || 560));
  const x = Math.max(8, Math.min((rect && rect.x) || 8, winW - width - 8));
  const y = (rect && rect.bottom) || 82;

  popupWin = win;
  popupView = new WebContentsView({
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true },
  });
  win.contentView.addChildView(popupView);
  popupView.setBounds({ x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) });
  popupView.webContents.loadURL(`chrome-extension://${id}/${String(popupPath).replace(/^\/+/, '')}`).catch(() => {
    closePopup();
  });
  popupView.webContents.on('blur', () => closePopup());
  popupView.webContents.on('destroyed', () => { if (popupView) popupView = null; });
  popupView.webContents.focus();
  return { ok: true };
}

module.exports = {
  init, loadAll, onChange,
  list, installFromPath, installFromStore, remove,
  openPopup, closePopup, parseStoreId,
};
