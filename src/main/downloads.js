'use strict';
// 下载管理：跟踪会话内全部下载项（含隐身会话共享同一列表），
// 提供进度/暂停/继续/取消/重下/打开文件/打开所在文件夹。

const { shell } = require('electron');

const items = new Map();   // id -> 记录（含仍在进行的 item 引用）
const live = new Map();    // id -> DownloadItem（结束后移除）
let seq = 1;
let onChanged = null;
let defaultSession = null;

function snapshot(r) {
  const { item, ...rest } = r;
  return rest;
}

function notify(reason, rec) {
  if (onChanged) {
    try { onChanged(reason, rec); } catch { /* ignore */ }
  }
}

function init(ses, changeCb) {
  if (!ses.__polarisDlHooked) {
    ses.__polarisDlHooked = true;
    ses.on('will-download', (_e, item) => {
      try { item.setSaveDialogOptions({ defaultPath: item.getFilename() }); } catch { /* ignore */ }
      const id = seq++;
      const rec = {
        id,
        url: item.getURL(),
        filename: item.getFilename(),
        path: item.getSavePath(),
        state: item.isPaused() ? 'paused' : 'progressing',
        received: 0,
        total: item.getTotalBytes(),
        startedAt: Date.now(),
        paused: item.isPaused(),
      };
      items.set(id, rec);
      live.set(id, item);
      item.on('updated', () => {
        rec.received = item.getReceivedBytes();
        rec.total = item.getTotalBytes();
        rec.paused = item.isPaused();
        rec.state = item.isPaused() ? 'paused' : 'progressing';
        rec.path = item.getSavePath();
        notify('updated', snapshot(rec));
      });
      item.once('done', (_e2, dlState) => {
        rec.received = item.getReceivedBytes();
        rec.total = item.getTotalBytes();
        rec.state = dlState; // completed | cancelled | interrupted
        rec.paused = false;
        live.delete(id);
        notify('done', snapshot(rec));
      });
      notify('new', snapshot(rec));
    });
  }
  if (changeCb) onChanged = changeCb;
  if (!defaultSession) defaultSession = ses;
}

function list() {
  return [...items.values()].reverse().map(snapshot);
}

function act(id, action) {
  const rec = items.get(id);
  if (!rec) throw new Error('下载记录不存在');
  const item = live.get(id);
  switch (action) {
    case 'pause':
      if (item && !item.isPaused()) item.pause();
      break;
    case 'resume':
      if (item && item.canResume()) item.resume();
      break;
    case 'cancel':
      if (item) item.cancel();
      break;
    case 'open':
      if (rec.path) shell.openPath(rec.path);
      break;
    case 'folder':
      if (rec.path) shell.showItemInFolder(rec.path);
      break;
    case 'retry': {
      if (!rec.url) break;
      // 重新下载交给任一已接管的会话（走原生下载流程，产生新记录）
      const ses = defaultSession || (item && item.getURL && null);
      if (ses) ses.downloadURL(rec.url);
      break;
    }
    default:
      throw new Error(`未知操作: ${action}`);
  }
  return snapshot(rec);
}

// 清除浏览数据用：清空下载记录（不删磁盘文件）
function clearRecords() {
  for (const [id, item] of live) {
    try { item.cancel(); } catch { /* ignore */ }
    live.delete(id);
  }
  items.clear();
  notify('cleared', null);
}

module.exports = { init, list, act, clearRecords };
