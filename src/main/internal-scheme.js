'use strict';
const fs = require('fs');
const path = require('path');
const { protocol, net } = require('electron');
const { pathToFileURL } = require('url');

// 内部页面使用 browser:// 协议（standard + secure），preload 仅在该协议下暴露能力
//   browser://shell/index.html     浏览器外壳（工具栏/标签栏）
//   browser://newtab/              新标签页
//   browser://settings/            设置页
//   browser://version/             版本信息
//   browser://error/?code=&desc=&url=  错误页
const UI_ROOT = path.join(__dirname, '..', '..', 'ui');

function registerInternalScheme() {
  protocol.registerSchemesAsPrivileged([
    { scheme: 'browser', privileges: { standard: true, secure: true, supportFetchAPI: true } },
  ]);
}

function handleInternalScheme() {
  protocol.handle('browser', (request) => {
    let url;
    try {
      url = new URL(request.url);
    } catch {
      return new Response('Bad Request', { status: 400 });
    }
    const host = url.hostname;
    let dir;
    let rel;

    if (host === 'shell') {
      dir = path.join(UI_ROOT, 'shell');
      rel = decodeURIComponent(url.pathname) || '/index.html';
      if (rel === '/') rel = '/index.html';
    } else {
      dir = path.join(UI_ROOT, 'pages');
      rel = decodeURIComponent(url.pathname);
      if (!rel || rel === '/') rel = `/${host}.html`;
    }

    const full = path.normalize(path.join(dir, rel));
    if (!full.startsWith(dir)) return new Response('Forbidden', { status: 403 });
    if (!fs.existsSync(full) || !fs.statSync(full).isFile()) {
      return new Response('Not Found', { status: 404 });
    }
    return net.fetch(pathToFileURL(full).toString());
  });
}

module.exports = { registerInternalScheme, handleInternalScheme };
