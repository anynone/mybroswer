'use strict';
// 书签存储（扁平列表，按添加顺序 = 书签栏顺序）。toggle 语义与 Chrome 星标一致：
// 已收藏则取消，未收藏则添加。

const fs = require('fs');
const path = require('path');
const { app } = require('electron');

const state = { items: [], seq: 1 };
let file = null;
let saveTimer = null;
const listeners = new Set();

function init() {
  file = path.join(app.getPath('userData'), 'bookmarks.json');
  try {
    const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (Array.isArray(raw.items)) state.items = raw.items;
    const maxId = state.items.reduce((m, v) => Math.max(m, v.id || 0), 0);
    state.seq = maxId + 1;
  } catch { /* 首次启动 */ }
}

function save() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, JSON.stringify({ items: state.items }), 'utf8');
    } catch { /* ignore */ }
  }, 300);
}

function changed() {
  save();
  for (const cb of listeners) {
    try { cb(); } catch { /* ignore */ }
  }
}

function list() {
  return state.items.slice();
}

function find(url) {
  return state.items.find((b) => b.url === url) || null;
}

// 返回 {added: boolean}：true = 已收藏，false = 已取消收藏
function toggle({ url, title, favicon }) {
  if (!url) return { added: false };
  const existing = find(url);
  if (existing) {
    state.items = state.items.filter((b) => b.id !== existing.id);
    changed();
    return { added: false };
  }
  state.items.unshift({ id: state.seq++, url, title: title || url, favicon: favicon || '', at: Date.now() });
  changed();
  return { added: true };
}

function remove(id) {
  const n = state.items.length;
  state.items = state.items.filter((b) => b.id !== id);
  if (state.items.length !== n) changed();
}

function rename(id, title) {
  const b = state.items.find((x) => x.id === id);
  if (b && title) {
    b.title = String(title);
    changed();
  }
}

function onChange(cb) {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

module.exports = { init, list, find, toggle, remove, rename, onChange };
