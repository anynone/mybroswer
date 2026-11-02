'use strict';
// 浏览历史与"最近关闭的标签页"存储（JSON 持久化，防抖落盘）。
// 隐身窗口的访问一律不记录（由 TabManager 侧过滤）。

const fs = require('fs');
const path = require('path');
const { app } = require('electron');

const MAX_VISITS = 5000;
const MAX_CLOSED = 25;

const state = { visits: [], closed: [], seq: 1 };
let file = null;
let saveTimer = null;
const listeners = new Set();

function init() {
  file = path.join(app.getPath('userData'), 'history.json');
  try {
    const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (Array.isArray(raw.visits)) state.visits = raw.visits;
    if (Array.isArray(raw.closed)) state.closed = raw.closed;
    const maxId = state.visits.reduce((m, v) => Math.max(m, v.id || 0), 0);
    state.seq = maxId + 1;
  } catch { /* 首次启动或文件损坏时从空开始 */ }
}

function save() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, JSON.stringify({ visits: state.visits, closed: state.closed }), 'utf8');
    } catch { /* ignore */ }
  }, 500);
}

function changed() {
  save();
  for (const cb of listeners) {
    try { cb(); } catch { /* ignore */ }
  }
}

// 与 Chrome 一致：仅记录 http(s)/file 页面，内部页（browser://）不进历史
function recordable(url) {
  return /^(https?|file):/i.test(url || '');
}

function record(url, title, favicon) {
  if (!recordable(url)) return;
  const now = Date.now();
  const last = state.visits[0];
  if (last && last.url === url && now - last.at < 10 * 60 * 1000) {
    // 短时间重复访问同一页：仅刷新标题/图标与时间，避免刷屏
    last.title = title || last.title;
    last.favicon = favicon || last.favicon;
    last.at = now;
    changed();
    return;
  }
  state.visits.unshift({ id: state.seq++, url, title: title || url, favicon: favicon || '', at: now });
  if (state.visits.length > MAX_VISITS) state.visits.length = MAX_VISITS;
  changed();
}

function list(opts) {
  // 兼容 list('关键词') 与 list({ q, limit, offset }) 两种调用
  const o = typeof opts === 'string' ? { q: opts } : (opts || {});
  const { q = '', limit = 300, offset = 0 } = o;
  const query = String(q || '').toLowerCase();
  const all = query
    ? state.visits.filter((v) =>
        (v.title || '').toLowerCase().includes(query) || (v.url || '').toLowerCase().includes(query))
    : state.visits;
  return all.slice(offset, offset + limit);
}

function remove(id) {
  const n = state.visits.length;
  state.visits = state.visits.filter((v) => v.id !== id);
  if (state.visits.length !== n) changed();
}

function clear() {
  state.visits = [];
  changed();
}

// ---- 最近关闭（Ctrl+Shift+T 的撤销栈 + 历史页展示） ----
function pushClosed(url, title) {
  if (!recordable(url)) return;
  state.closed = state.closed.filter((c) => c.url !== url);
  state.closed.unshift({ url, title: title || url, at: Date.now() });
  if (state.closed.length > MAX_CLOSED) state.closed.length = MAX_CLOSED;
  changed();
}

function popClosed() {
  const it = state.closed.shift();
  if (it) changed();
  return it || null;
}

function closedList() {
  return state.closed.slice();
}

function onChange(cb) {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

module.exports = { init, record, list, remove, clear, pushClosed, popClosed, closedList, onChange, recordable };
