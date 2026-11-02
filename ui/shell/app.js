'use strict';
/* global polaris */
const $ = (s) => document.querySelector(s);

const tabsEl = $('#tabs');
const address = $('#address');
const findbar = $('#findbar');
const findInput = $('#find-input');
const findCount = $('#find-count');
const INCOGNITO = /[?&]incognito=1/.test(location.search);

// Material 矢量图标（与工具栏静态图标同一套）
const svg = (d) => `<svg viewBox="0 0 24 24"><path d="${d}"/></svg>`;
const ICONS = {
  close: svg('M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z'),
  stop: svg('M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z'),
  reload: svg('M17.65 6.35A7.95 7.95 0 0 0 12 4a8 8 0 1 0 7.73 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z'),
  // Chrome 风格加载弧（配合 CSS 旋转动画）
  spinner: '<svg viewBox="0 0 24 24" class="spin"><path d="M12 5a7 7 0 1 0 7 7h-1.6A5.4 5.4 0 1 1 12 6.6V5z"/><path d="M12 1.8v3.4l3-1.7z"/></svg>',
  lock: svg('M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V10a2 2 0 0 0-2-2Zm-6 9a2 2 0 1 1 2-2 2 2 0 0 1-2 2Zm3.1-9H8.9V6a3.1 3.1 0 0 1 6.2 0Z'),
  info: svg('M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm1 15h-2v-6h2Zm0-8h-2V7h2Z'),
};

const tabs = new Map();
let activeId = null;
let settings = null;
let bmUrls = new Set();
// 刷新的乐观加载反馈：点击瞬间立即转圈，且最少持续一段时间（快页面 loading 仅几十毫秒，
// 不保底的话用户根本看不到任何反馈）
let spinFloor = 0;
let spinTimer = null;

function bumpSpin(ms) {
  spinFloor = Date.now() + ms;
  clearTimeout(spinTimer);
  spinTimer = setTimeout(() => renderTabs(), ms);
}

function active() {
  return tabs.get(activeId);
}

function letter(t) {
  const s = (t.title || t.url || '新').trim();
  return s.charAt(0).toUpperCase();
}

function reportHeight() {
  try { polaris.ui.setHeight(document.body.offsetHeight); } catch { /* ignore */ }
}

function renderTabs() {
  tabsEl.innerHTML = '';
  for (const t of tabs.values()) {
    const el = document.createElement('div');
    el.className = 'tab' + (t.id === activeId ? ' active' : '') + (t.loading ? ' loading' : '');
    el.title = t.title || t.url || '';
    el.dataset.id = String(t.id);

    const fav = document.createElement('span');
    fav.className = 'fav';
    const optimisticSpin = t.id === activeId && Date.now() < spinFloor;
    if (t.loading || optimisticSpin) {
      // Chrome 同款：加载中 favicon 位置显示旋转指示
      fav.innerHTML = ICONS.spinner;
      fav.classList.add('spinner');
    } else if (t.favicon) {
      const img = document.createElement('img');
      img.src = t.favicon;
      img.onerror = () => {
        fav.textContent = letter(t);
        fav.classList.add('letter');
      };
      fav.appendChild(img);
    } else {
      fav.textContent = letter(t);
      fav.classList.add('letter');
    }

    const title = document.createElement('span');
    title.className = 'title';
    title.textContent = t.loading ? '加载中…' : (t.title || '新标签页');

    const x = document.createElement('button');
    x.className = 'x';
    x.innerHTML = ICONS.close;
    x.title = '关闭标签页';
    x.onclick = (e) => {
      e.stopPropagation();
      polaris.tabs.close(t.id);
    };

    el.appendChild(fav);
    el.appendChild(title);
    el.appendChild(x);
    el.onclick = () => polaris.tabs.activate(t.id);
    el.onauxclick = (e) => {
      if (e.button === 1) polaris.tabs.close(t.id);
    };
    tabsEl.appendChild(el);
  }
  const a = active();
  $('#btn-back').disabled = !(a && a.canGoBack);
  $('#btn-forward').disabled = !(a && a.canGoForward);
  // 刷新按钮加载态：旋转指示，点击 = 停止加载（Chrome 的 X 语义 + 旋转反馈）；
  // loading 为真或处于乐观反馈保底期内都显示转圈
  const btnReload = $('#btn-reload');
  const loading = !!(a && a.loading) || Date.now() < spinFloor;
  btnReload.classList.toggle('loading', loading);
  btnReload.title = loading ? '停止加载此页 (Esc)' : '刷新 (Ctrl+R)';
  btnReload.innerHTML = loading ? ICONS.spinner : ICONS.reload;
  refreshStar();
  updateZoomVal();
}

function updateAddress() {
  if (document.activeElement === address) return;
  const a = active();
  const url = a ? a.url : '';
  address.value = url.startsWith('browser://newtab') ? '' : url;
  const lock = $('#lock');
  if (/^https:\/\//i.test(url)) lock.innerHTML = ICONS.lock;
  else if (/^http:\/\//i.test(url)) lock.innerHTML = ICONS.info;
  else lock.innerHTML = '';
  refreshStar();
}

function updateTzChip() {
  const text = $('#tz-text');
  const chip = $('#tz-chip');
  if (settings && settings.timezone) {
    text.textContent = settings.timezone;
    chip.classList.add('custom');
  } else {
    const sys = settings && settings.info ? settings.info.systemTimezone : '';
    text.textContent = `跟随系统${sys ? ` (${sys})` : ''}`;
    chip.classList.remove('custom');
  }
}

// ---- 缩放显示：与 Chrome 一致，仅当缩放 ≠ 100% 时在地址栏内出现，点击复位 ----
function updateZoomVal() {
  const a = active();
  const pct = Math.round((a && a.zoom ? a.zoom : 1) * 100);
  const chip = $('#zoom-chip');
  if (!chip) return;
  chip.classList.toggle('hidden', pct === 100);
  const val = $('#zoom-val');
  if (val) val.textContent = `${pct}%`;
}

// ---- 书签（星标 + 书签栏） ----
function refreshStar() {
  const a = active();
  const url = a ? a.url : '';
  const on = !!url && bmUrls.has(url);
  const star = $('#btn-star');
  star.classList.toggle('on', on);
  star.title = on ? '取消收藏此页' : '收藏此页 (Ctrl+D)';
}

function toggleBookmark() {
  const a = active();
  if (!a || !a.url) return;
  polaris.bookmarks.toggle({ url: a.url, title: a.title, favicon: a.favicon })
    .then(() => loadBookmarks())
    .catch(() => {});
}

function renderBmBar(list) {
  const bar = $('#bm-bar');
  bar.innerHTML = '';
  for (const b of list || []) {
    const el = document.createElement('div');
    el.className = 'bm-item';
    el.title = `${b.title || b.url}\n${b.url}`;
    const fav = document.createElement('span');
    fav.className = 'fav';
    if (b.favicon) {
      const img = document.createElement('img');
      img.src = b.favicon;
      img.onerror = () => {
        fav.textContent = (b.title || '?').charAt(0).toUpperCase();
      };
      fav.appendChild(img);
    } else {
      fav.textContent = (b.title || '?').charAt(0).toUpperCase();
    }
    const t = document.createElement('span');
    t.className = 't';
    t.textContent = b.title || b.url;
    el.appendChild(fav);
    el.appendChild(t);
    el.onclick = () => polaris.tabs.navigateCurrent(b.url);
    el.onauxclick = (e) => {
      if (e.button === 1) polaris.tabs.create(b.url);
    };
    bar.appendChild(el);
  }
}

function loadBookmarks() {
  polaris.bookmarks.list()
    .then((list) => {
      bmUrls = new Set((list || []).map((b) => b.url));
      renderBmBar(list);
      refreshStar();
    })
    .catch(() => {});
}

function applyBookmarkBar() {
  const on = !!(settings && settings.bookmarkBar !== false);
  document.body.classList.toggle('with-bm', on);
  $('#bm-bar').classList.toggle('hidden', !on);
  reportHeight();
}

// ---- 页内查找栏 ----
function openFind(prefill) {
  findbar.classList.remove('hidden');
  if (prefill !== undefined) findInput.value = prefill;
  findInput.focus();
  findInput.select();
}

function closeFind() {
  findbar.classList.add('hidden');
  findCount.textContent = '0/0';
  polaris.tabs.stopFind().catch(() => {});
}

let findTimer = null;
function onFindInput() {
  clearTimeout(findTimer);
  findTimer = setTimeout(() => {
    const text = findInput.value;
    if (text) polaris.tabs.find(text).catch(() => {});
    else {
      polaris.tabs.stopFind().catch(() => {});
      findCount.textContent = '0/0';
    }
  }, 120);
}

// ---- 扩展图标 ----
function renderExtensions(exts) {
  const box = $('#ext-icons');
  box.innerHTML = '';
  for (const x of exts || []) {
    const b = document.createElement('button');
    b.className = 'ext-ico';
    b.title = x.name + (x.version ? ` ${x.version}` : '');
    if (x.icon) {
      const img = document.createElement('img');
      img.src = x.icon;
      b.appendChild(img);
    } else {
      b.textContent = (x.name || '?').charAt(0).toUpperCase();
    }
    b.onclick = (e) => {
      e.stopPropagation();
      const r = b.getBoundingClientRect();
      polaris.ext.popup(x.id, { x: r.left, y: r.top, bottom: r.bottom, width: r.width, height: r.height })
        .catch(() => {});
    };
    box.appendChild(b);
  }
}

function bind() {
  $('#btn-newtab').onclick = () => polaris.tabs.create();
  $('#btn-back').onclick = () => polaris.tabs.back(activeId);
  $('#btn-forward').onclick = () => polaris.tabs.forward(activeId);
  $('#btn-reload').onclick = () => {
    const a = active();
    // 诊断打点（仅 browser:// 外壳内可见，用于排查刷新链路）
    window.__reloadTrace = { at: Date.now(), hasActive: !!a, id: a ? a.id : null, loading: !!(a && a.loading), activeId };
    const d = (m) => { try { polaris.diag(m).catch(() => {}); } catch { /* ignore */ } };
    d(`btn-click hasActive=${!!a} id=${a ? a.id : '-'} loading=${!!(a && a.loading)} activeId=${activeId}`);
    if (!a) return;
    if (a.loading) {
      d('branch=stop');
      polaris.tabs.stop(a.id).catch((e) => d(`stop-err ${e.message}`));
    } else {
      d('branch=reload');
      bumpSpin(450);       // 点击瞬间立即转圈（不等 IPC/加载事件往返）
      renderTabs();
      polaris.tabs.reload(a.id)
        .then(() => d('reload-ack'))
        .catch((e) => d(`reload-err ${e.message}`));
    }
  };
  $('#btn-home').onclick = () => polaris.tabs.navigateCurrent('browser://newtab/');
  $('#tz-chip').onclick = () => polaris.tabs.create('browser://settings/');
  $('#btn-ext').onclick = () => polaris.tabs.create('browser://extensions/');
  $('#btn-star').onclick = toggleBookmark;
  $('#zoom-chip').onclick = () => polaris.tabs.zoomSet(1);

  address.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      polaris.tabs.navigateCurrent(address.value);
      address.blur();
    } else if (e.key === 'Escape') {
      updateAddress();
      address.blur();
    }
  });
  address.addEventListener('focus', () => address.select());

  // 查找栏
  findInput.addEventListener('input', onFindInput);
  findInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      const text = findInput.value;
      if (text) polaris.tabs.find(text, { forward: !e.shiftKey, findNext: true }).catch(() => {});
    } else if (e.key === 'Escape') {
      e.preventDefault();
      closeFind();
    }
  });
  $('#find-next').onclick = () => {
    if (findInput.value) polaris.tabs.find(findInput.value, { forward: true, findNext: true }).catch(() => {});
  };
  $('#find-prev').onclick = () => {
    if (findInput.value) polaris.tabs.find(findInput.value, { forward: false, findNext: true }).catch(() => {});
  };
  $('#find-close').onclick = closeFind;

  $('#btn-menu').onclick = (e) => {
    e.stopPropagation();
    polaris.menu.open().catch(() => {});
  };
}

function onTabEvent(evt) {
  const t = tabs.get(evt.id);
  switch (evt.type) {
    case 'created':
      tabs.set(evt.id, { id: evt.id, url: evt.url || '', title: evt.title || '新标签页', favicon: '', loading: false });
      break;
    case 'removed':
      tabs.delete(evt.id);
      break;
    case 'activated':
      activeId = evt.id;
      break;
    case 'state':
      if (t) Object.assign(t, {
        url: evt.url, title: evt.title, loading: evt.loading,
        canGoBack: evt.canGoBack, canGoForward: evt.canGoForward,
        zoom: evt.zoom !== undefined ? evt.zoom : (t.zoom || 1),
      });
      break;
    case 'title':
      if (t) t.title = evt.title;
      break;
    case 'favicon':
      if (t) t.favicon = evt.favicon;
      break;
    case 'found':
      findCount.textContent = `${evt.active || 0}/${evt.total || 0}`;
      return; // 查找结果不重绘标签栏
    default: break;
  }
  renderTabs();
  if (evt.type !== 'favicon' && evt.type !== 'title') updateAddress();
}

async function init() {
  if (INCOGNITO) document.body.classList.add('incognito');
  bind();

  polaris.onTabEvent(onTabEvent);
  // 状态快照：外壳就绪时主动拉取当前标签状态，自愈任何早于订阅的事件丢失
  // （启动时 loadURL(shell) 后主进程立即 createTab，created/activated 可能先于本页订阅发出）
  polaris.tabs.snapshot().then((snap) => {
    if (!snap) return;
    for (const t of snap.tabs || []) {
      tabs.set(t.id, {
        id: t.id, url: t.url || '', title: t.title || '新标签页', favicon: t.favicon || '',
        loading: !!t.loading, canGoBack: !!t.canGoBack, canGoForward: !!t.canGoForward,
        zoom: 1,
      });
    }
    if (snap.activeId != null && !activeId) activeId = snap.activeId;
    renderTabs();
    updateAddress();
  }).catch(() => {});
  polaris.onUiCommand((cmd) => {
    if (cmd.type === 'focus-address') {
      address.focus();
      address.select();
    } else if (cmd.type === 'find') {
      openFind();
    } else if (cmd.type === 'bookmark') {
      toggleBookmark();
    } else if (cmd.type === 'menu-state') {
      // ⋮ 按钮开合态：三个点 ↔ 三叶图标渐变
      $('#btn-menu').classList.toggle('open', !!cmd.arg);
    }
  });
  polaris.onSettingsChanged((s) => {
    settings = s;
    updateTzChip();
    applyBookmarkBar();
  });
  polaris.onExtChanged(renderExtensions);
  polaris.onBmChanged(loadBookmarks);
  polaris.onDlChanged(() => { /* 下载页自行刷新 */ });

  try {
    settings = await polaris.settings.get();
    updateTzChip();
    applyBookmarkBar();
  } catch (err) {
    console.error('读取设置失败:', err);
    applyBookmarkBar();
    reportHeight();
  }

  loadBookmarks();

  try {
    renderExtensions(await polaris.ext.list());
  } catch (err) {
    console.error('读取扩展列表失败:', err);
  }
}

// 内部诊断口（browser:// 外壳内可见；用于自动化排查，不构成页面指纹面）
window.__polDiag = () => ({
  activeId,
  activeIdType: typeof activeId,
  tabs: [...tabs.keys()],
  activeLoading: (tabs.get(activeId) || {}).loading,
  trace: window.__reloadTrace || null,
});

init();
