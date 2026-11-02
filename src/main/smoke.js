'use strict';
// 自动化验收测试（无人工介入）:
//   1) 注入时区 America/New_York 后，页面内 Intl/Date 的时区必须等于 America/New_York
//   2) navigator.userAgent 与出口请求头 User-Agent 与 Chrome 完全一致（无 Electron/应用名字样）
//   3) navigator.userAgentData 与请求头 sec-ch-ua 包含 "Google Chrome" 品牌
//   4) Accept-Language 与 Chrome 格式一致
//   5) 清除覆盖后恢复系统时区
// 探针运行在 browser://newtab/（注册为 secure 的安全上下文，userAgentData 可用）。
// 结果写入 ./smoke-result.json 并以退出码 0/1 结束进程。

const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const { app, BrowserWindow, WebContentsView, webContents } = require('electron');
const ua = require('./ua');
const extMgr = require('./extensions');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function listen(server) {
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server.address())));
}

function probeScript(probeUrl) {
  return `(function () {
    return new Promise(function (resolve) {
      var out = {};
      try {
        out.tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        out.offsetMinutes = new Date().getTimezoneOffset();
        out.dateStr = new Date().toString();
        out.ua = navigator.userAgent;
        out.lang = navigator.language;
        out.langs = Array.from(navigator.languages || []);
        out.intlLocale = Intl.DateTimeFormat().resolvedOptions().locale;
        var uad = navigator.userAgentData;
        out.brands = uad && uad.brands ? uad.brands : null;
        out.uadPlatform = uad ? uad.platform : null;
      } catch (e) { out.error = String(e); }
      var img = new Image();
      img.onload = img.onerror = function () { resolve(out); };
      img.src = '${probeUrl}?t=' + Date.now();
      setTimeout(function () { resolve(out); }, 5000);
    });
  })()`;
}

async function run({ updateSettings, createMainWindow, history, bookmarks }) {
  const assertions = [];
  const add = (name, pass, extra) => {
    assertions.push({ name, pass: !!pass, extra: extra || undefined });
    console.log((pass ? '  ✓ ' : '  ✗ ') + name); // 逐条即时输出，便于定位卡死阶段
  };

  const sysTz = Intl.DateTimeFormat().resolvedOptions().timeZone;

  const captured = {};
  // 跨站 OOPIF 测试页：top 走 127.0.0.1，iframe 走 localhost（两个不同 site）
  const topHtml = (innerUrl) => `<!doctype html><meta charset="utf-8"><body>
<iframe id="f" src="${innerUrl}" style="width:420px;height:120px"></iframe>
<script>
window.__inner = null;
addEventListener('message', function (e) { if (e.data && e.data.tz !== undefined) window.__inner = e.data; }, false);
try {
  var w = new Worker(URL.createObjectURL(new Blob([
    "postMessage({ tz: Intl.DateTimeFormat().resolvedOptions().timeZone, offset: new Date().getTimezoneOffset() })"
  ], { type: 'text/javascript' })));
  w.onmessage = function (e) { window.__worker = e.data; };
} catch (e) { window.__worker = { error: String(e) }; }
</script></body>`;
  const innerHtml = `<!doctype html><meta charset="utf-8"><body>
<script>
var W = null;
try {
  W = new Worker(URL.createObjectURL(new Blob([
    "var f=function(){postMessage({tz:Intl.DateTimeFormat().resolvedOptions().timeZone,offset:new Date().getTimezoneOffset()})};onmessage=f;f();"
  ], { type: 'text/javascript' })));
} catch (e) {}
function probe() {
  return new Promise(function (resolve) {
    var done = false;
    var base = {
      tz: Intl.DateTimeFormat().resolvedOptions().timeZone,
      offset: new Date().getTimezoneOffset(),
      str: new Date().toString(),
      hours: new Date().getHours(),
      shim: !!(window.__polarisTz__ && window.__polarisTz__.active),
      native: !!(window.__polarisTz__ && window.__polarisTz__.native),
      worker: window.__worker || null,
    };
    function finish() { if (!done) { done = true; resolve(base); } }
    if (!W) return finish();
    var h = function (e) { window.__worker = e.data; base.worker = e.data; W.onmessage = null; finish(); };
    try { W.onmessage = h; W.postMessage(1); } catch (e) { finish(); }
    setTimeout(finish, 800);
  });
}
function post() { probe().then(function (d) { try { parent.postMessage(d, '*'); } catch (e) {} }); }
post();
addEventListener('message', function (e) { if (e.data === 'pol-probe') post(); }, false);
</script></body>`;
  const server = http.createServer((req, res) => {
    const p = req.url.split('?')[0];
    captured[p] = req.headers;
    if (p === '/oopif-top') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(topHtml(`http://localhost:${server.address().port}/oopif-inner`));
      return;
    }
    if (p === '/slow') {
      // 慢页面：验证加载状态（转圈）与刷新行为
      setTimeout(() => {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end(`<!doctype html><meta charset="utf-8"><title>慢页 ${Date.now()}</title><body>slow page ${Date.now()}`);
      }, 1500);
      return;
    }
    if (p === '/oopif-inner') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(innerHtml);
      return;
    }
    res.statusCode = 200;
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.end('ok');
  });
  const addr = await listen(server);
  const probeUrl = `http://127.0.0.1:${addr.port}/probe`;

  const win = new BrowserWindow({ show: false, width: 900, height: 700 });
  const view = new WebContentsView({
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      nodeIntegrationInSubFrames: true,
      sandbox: false,
    },
  });
  win.contentView.addChildView(view);
  view.setBounds({ x: 0, y: 0, width: 900, height: 700 });

  // 加载安全上下文内部页（加载本身也会触发 did-start-navigation 的覆盖下发路径）
  await view.webContents.loadURL('browser://newtab/');
  await sleep(600);
  const probe = () => view.webContents.executeJavaScript(probeScript(probeUrl), true);

  // ---- 阶段 1: 覆盖时区为 America/New_York ----
  updateSettings({ timezone: 'America/New_York', userAgentMode: 'chrome', customUserAgent: '' }, { broadcast: false });
  await sleep(600);
  const p1 = await probe();
  const h1 = captured['/probe'] || {};

  add('页面时区 == America/New_York', p1 && p1.tz === 'America/New_York', p1 && p1.tz);
  add('Date 时区偏移与纽约一致（-240/-300 分钟，视夏令时）', p1 && (p1.offsetMinutes === 240 || p1.offsetMinutes === 300), p1 && p1.offsetMinutes);
  add('navigator.userAgent 与 Chrome 逐字符一致', p1 && p1.ua === ua.chromeUA(), p1 && p1.ua);
  add('UA 中无 Electron/应用名字样', p1 && !/Electron|Polaris/i.test(p1.ua), p1 && p1.ua);
  add('navigator.userAgentData 包含 Google Chrome 品牌', p1 && Array.isArray(p1.brands) && p1.brands.some((b) => b.brand === 'Google Chrome'), p1 && p1.brands);
  add('navigator.userAgentData.platform == Windows', p1 && p1.uadPlatform === 'Windows', p1 && p1.uadPlatform);
  add('navigator.language 与 Chrome 一致', p1 && p1.lang === ua.chromeAcceptLanguages().split(',')[0], p1 && p1.lang);
  add('出口请求头 User-Agent 与 Chrome 逐字符一致', !!h1['user-agent'] && h1['user-agent'] === ua.chromeUA(), h1['user-agent']);
  add('出口请求头 sec-ch-ua 含 Google Chrome 且无 Electron',
    !!h1['sec-ch-ua'] && /Google Chrome/.test(h1['sec-ch-ua']) && !/Electron/i.test(h1['sec-ch-ua']), h1['sec-ch-ua']);
  add('出口请求头 Accept-Language 与 Chrome 格式一致',
    !!h1['accept-language'] && h1['accept-language'] === ua.chromeAcceptLanguages(), h1['accept-language']);

  // ---- 阶段 1.7: 开发者工具（打开 → 关闭，UA/时区覆盖自动恢复） ----
  // DevTools 前端与仿真用的 CDP 调试器互斥：打开前让出，关闭后由 devtools-closed 钩子恢复
  try { if (view.webContents.debugger.isAttached()) view.webContents.debugger.detach(); } catch { /* ignore */ }
  await view.webContents.openDevTools({ mode: 'detach' });
  await sleep(1000);
  await view.webContents.closeDevTools();
  await sleep(1000);
  const dt = await probe();
  add('DevTools: 关闭后 UA/时区覆盖自动恢复', dt && dt.tz === 'America/New_York', dt && dt.tz);

  // ---- 阶段 1.8: 扩展安装与工具栏弹窗 ----
  const extDir = fs.mkdtempSync(path.join(os.tmpdir(), 'polaris-test-ext-'));
  fs.writeFileSync(path.join(extDir, 'manifest.json'), JSON.stringify({
    name: 'Polaris Test Ext', version: '1.0.0', manifest_version: 2,
    description: 'smoke test extension',
    browser_action: { default_popup: 'popup.html', default_title: 'PTE' },
    permissions: ['storage'],
  }));
  fs.writeFileSync(path.join(extDir, 'popup.html'),
    '<!doctype html><meta charset="utf-8"><title>PTE Popup</title><body>Polaris 扩展测试</body>');
  let extInfo = null;
  let extErr = '';
  try { extInfo = await extMgr.installFromPath(extDir); }
  catch (err) { extErr = err.message; }
  add('扩展：本地扩展可安装', !!(extInfo && extInfo.id), extInfo ? extInfo.id : extErr);
  if (extInfo && extInfo.id) {
    const lst = extMgr.list();
    const mine = lst.find((x) => x.id === extInfo.id);
    add('扩展：出现在已安装列表（含弹窗信息）', !!(mine && mine.popup), JSON.stringify(mine));
    const pv = new WebContentsView({});
    win.contentView.addChildView(pv);
    pv.setBounds({ x: 0, y: 0, width: 320, height: 80 });
    let ptitle = null;
    try {
      await pv.webContents.loadURL(`chrome-extension://${extInfo.id}/popup.html`);
      ptitle = await pv.webContents.executeJavaScript('document.title');
    } catch { ptitle = null; }
    add('扩展：popup 可通过 chrome-extension:// 加载', ptitle === 'PTE Popup', ptitle);
    win.contentView.removeChildView(pv);
    try { pv.webContents.destroy(); } catch { /* ignore */ }
  }

  // ---- 阶段 1.6: 隐私防护 —— 语言隐藏 / 字体指纹 / Canvas 扰动 / WebRTC 防泄露 ----
  const sysLang = p1.lang;
  updateSettings({ locale: 'en-US' }, { broadcast: false });
  await sleep(700);
  const p3 = await probe();
  const h3 = captured['/probe'] || {};
  add('语言: navigator.language == en-US（不暴露系统语言）', p3 && p3.lang === 'en-US', `${p3 && p3.lang} (系统: ${sysLang})`);
  add('语言: navigator.languages == [en-US, en]',
    p3 && p3.langs && p3.langs[0] === 'en-US' && p3.langs[1] === 'en', p3 && JSON.stringify(p3.langs));
  add('语言: Intl 默认 locale == en-US', p3 && /^en-US/i.test(p3.intlLocale || ''), p3 && p3.intlLocale);
  add('语言: 出口 Accept-Language == en-US,en;q=0.9',
    h3['accept-language'] === 'en-US,en;q=0.9', h3['accept-language']);

  // 字体 / Canvas / WebRTC 探针
  const fp = await view.webContents.executeJavaScript(`(function () {
    // async 执行器：探针内部含 await（Worker 段），Electron 会对返回的 Promise 求值
    return new Promise(async function (resolve) {
      var out = {};
      try { out.qlf = typeof window.queryLocalFonts; } catch (e) { out.qlf = 'err'; }
      try {
        var c = document.createElement('canvas'); c.width = 400; c.height = 40;
        var x = c.getContext('2d'); x.font = '32px serif';
        out.mt = x.measureText('Hello World 测量文本 0123 qwerty MMWWll').width;
      } catch (e) { out.mt = 'err:' + String(e); }
      // 字体宽度探测（模拟检测站手法）：指定本地中文字体 vs 回退字体，宽度应一致（恒为"未安装"）
      try {
        var cf = document.createElement('canvas'); cf.width = 700; cf.height = 60;
        var xf = cf.getContext('2d');
        xf.font = '72px "Microsoft YaHei", monospace';
        out.fp1 = xf.measureText('汉字宽度探测 mmwwil 测试').width;
        out.frd = xf.font;
        xf.font = '72px monospace';
        out.fp2 = xf.measureText('汉字宽度探测 mmwwil 测试').width;
      } catch (e) { out.ferr = 'err:' + String(e); }
      // 检测站（fuck-claude.vercel.app）字体探测原样重放：h() 以三种通用字体为基线，
      // '72px "<字体>", <通用>' 宽度差 >0.5 判“已安装”；g() 分别过滤简/繁体列表。
      // 这里逐字复刻其手法与全部 27 个字体名，两条列表都必须 0 命中。
      try {
        var SC = ['Microsoft YaHei', 'Microsoft YaHei UI', 'SimSun', 'NSimSun', 'SimHei', 'KaiTi', 'FangSong', 'DengXian', 'PingFang SC', 'Hiragino Sans GB', 'STHeiti', 'STSong', 'Songti SC', 'Source Han Sans CN', 'Source Han Sans SC', 'Noto Sans CJK SC', 'Noto Serif CJK SC', 'WenQuanYi Micro Hei', 'WenQuanYi Zen Hei'];
        var TC = ['Microsoft JhengHei', 'PMingLiU', 'MingLiU', 'DFKai-SB', 'PingFang TC', 'PingFang HK', 'Source Han Sans TW', 'Noto Sans CJK TC'];
        var cs = document.createElement('canvas').getContext('2d');
        var STR = '中文字体检测ABCabc012';
        function siteHit(fam) {
          return ['monospace', 'sans-serif', 'serif'].some(function (g) {
            cs.font = '72px ' + g;
            var base = cs.measureText(STR).width;
            cs.font = '72px "' + fam + '", ' + g;
            return Math.abs(cs.measureText(STR).width - base) > 0.5;
          });
        }
        out.siteSC = SC.filter(siteHit);
        out.siteTC = TC.filter(siteHit);
      } catch (e) { out.siteErr = 'err:' + String(e); }
      // OffscreenCanvas 通道（独立原型）：同样恒为"未安装"
      try {
        var oc = new OffscreenCanvas(700, 60);
        var ox = oc.getContext('2d');
        ox.font = '72px "Microsoft YaHei", monospace';
        out.ofp1 = ox.measureText('汉字宽度探测 mmwwil 测试').width;
        ox.font = '72px monospace';
        out.ofp2 = ox.measureText('汉字宽度探测 mmwwil 测试').width;
      } catch (e) { out.ofErr = 'err:' + String(e); }
      // SVG 文本测量通道：同样恒为"未安装"
      try {
        var svgns = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(svgns, 'svg');
        var mk = function (fam) {
          var t = document.createElementNS(svgns, 'text');
          t.style.fontFamily = fam;
          t.style.fontSize = '72px';
          t.textContent = '汉字SVG探测 mmwwil';
          svg.appendChild(t);
          return t;
        };
        var tA = mk('"Microsoft YaHei", monospace');
        var tB = mk('monospace');
        document.body.appendChild(svg);
        out.svg1 = tA.getComputedTextLength();
        out.svg2 = tB.getComputedTextLength();
        document.body.removeChild(svg);
      } catch (e) { out.svgErr = 'err:' + String(e); }
      // 像素级通道：fillText 分别用 YaHei/回退字体渲染，画布内容应一致（绕过测量 API 的探测）
      try {
        var draw = function (fam) {
          var c = document.createElement('canvas'); c.width = 300; c.height = 60;
          var x = c.getContext('2d');
          x.textBaseline = 'top';
          x.font = fam;
          x.fillStyle = '#000';
          x.fillText('汉字像素探测 mmwwil', 2, 4);
          return c.toDataURL();
        };
        out.pix1 = draw('72px "Microsoft YaHei", monospace');
        out.pix2 = draw('72px monospace');
        out.pixSame = out.pix1 === out.pix2;
      } catch (e) { out.pixErr = 'err:' + String(e); }
      // Worker 通道：blob Worker 内 OffscreenCanvas 度量（Blob 构造前插垫片后应恒"未安装"）
      try {
        out.worker = await new Promise(function (resolve) {
          try {
            var w = new Worker(URL.createObjectURL(new Blob([
              "onmessage=function(){try{var oc=new OffscreenCanvas(700,60);var x=oc.getContext('2d');" +
              "x.font='72px \\\"Microsoft YaHei\\\", monospace';" +
              "var a=x.measureText('汉字宽度探测 mmwwil').width;" +
              "x.font='72px monospace';" +
              "var b=x.measureText('汉字宽度探测 mmwwil').width;" +
              "postMessage({a:a,b:b,leak:a!==b})}catch(e){postMessage({err:String(e)})}}"
            ], { type: 'text/javascript' })));
            var done = false;
            w.onmessage = function (e) { done = true; resolve(e.data); w.terminate(); };
            w.postMessage(1);
            setTimeout(function () { if (!done) resolve({ err: 'timeout' }); }, 2500);
          } catch (e) { resolve({ err: String(e) }); }
        });
      } catch (e) { out.workerErr = 'err:' + String(e); }
      try {
        var b = document.body;
        var s1 = document.createElement('span');
        s1.style.fontFamily = '"Microsoft YaHei", "PingFang SC", monospace';
        s1.textContent = '汉字宽度探测 spanprobe 汉字';
        var s2 = document.createElement('span');
        s2.style.fontFamily = 'monospace';
        s2.textContent = '汉字宽度探测 spanprobe 汉字';
        var s3 = document.createElement('span');
        s3.style.fontFamily = '"Segoe UI Emoji", monospace';
        s3.textContent = '😀🐭📊emoji 汉字宽度探测';
        b.appendChild(s1); b.appendChild(s2); b.appendChild(s3);
        out.sfrb = s1.style.fontFamily;
        out.sw1 = s1.offsetWidth;
        out.sw2 = s2.offsetWidth;
        // 标准字体（UA 人设=Windows）应真实在场：宽度与回退字体不同
        out.swSeg = s3.offsetWidth;
        // CSS 通用族 emoji 不被剥离：与 sans-serif 宽度不同（系统 emoji 字体原生渲染）
        var s4 = document.createElement('span');
        s4.style.fontFamily = 'emoji';
        s4.textContent = '😀🐭📊 emoji vendor probe';
        b.appendChild(s4);
        var s5 = document.createElement('span');
        s5.style.fontFamily = 'sans-serif';
        s5.textContent = '😀🐭📊 emoji vendor probe';
        b.appendChild(s5);
        out.swEmojiFam = s4.offsetWidth;
        out.swSans = s5.offsetWidth;
        b.removeChild(s1); b.removeChild(s2); b.removeChild(s3); b.removeChild(s4); b.removeChild(s5);
      } catch (e) { out.serr = 'err:' + String(e); }
      try {
        out.chkLocal = document.fonts.check('72px "Microsoft YaHei"');
        out.chkGeneric = document.fonts.check('72px monospace');
        out.chkSegoe = document.fonts.check('72px "Segoe UI Emoji"');
      } catch (e) { out.chkErr = 'err:' + String(e); }
      try {
        var c2 = document.createElement('canvas'); c2.width = 120; c2.height = 30;
        var x2 = c2.getContext('2d'); x2.font = '16px sans-serif'; x2.fillText('Polaris fp', 2, 20);
        out.dup = c2.toDataURL() === c2.toDataURL();
        var c3 = document.createElement('canvas'); c3.width = 120; c3.height = 30;
        var x3 = c3.getContext('2d'); x3.font = '16px sans-serif'; x3.fillText('Polaris fp', 2, 20);
        out.diffSeed = c3.toDataURL() !== c2.toDataURL();
      } catch (e) { out.cnv = 'err:' + String(e); }
      var settled = false;
      function done(seen, sdp) { if (settled) return; settled = true; out.seen = seen; out.sdp = sdp || ''; resolve(out); }
      try {
        var pc = new RTCPeerConnection({ iceServers: [] });
        var seen = [];
        pc.createDataChannel('x');
        pc.onicecandidate = function (e) {
          if (e.candidate) { seen.push(e.candidate.candidate); return; }
          var sdp = pc.localDescription ? pc.localDescription.sdp : '';
          try { pc.close(); } catch (_) { /* ignore */ }
          done(seen, sdp);
        };
        pc.createOffer().then(function (o) { return pc.setLocalDescription(o); }).catch(function () {
          try { pc.close(); } catch (_) { /* ignore */ }
          done(seen, pc.localDescription ? pc.localDescription.sdp : '');
        });
        setTimeout(function () {
          var sdp = '';
          try { sdp = pc.localDescription ? pc.localDescription.sdp : ''; } catch (_) { /* ignore */ }
          try { pc.close(); } catch (_) { /* ignore */ }
          done(seen, sdp);
        }, 1800);
      } catch (e) { out.rtc = 'err:' + String(e); done([]); }
    });
  })()`, true);
  add('字体: measureText 保持原生小数（与真 Chrome 一致，无量化异常）',
    fp && typeof fp.mt === 'number' && Number.isInteger(fp.mt) === false, fp && fp.mt);
  add('字体: queryLocalFonts API 在场（与真实 Chrome 一致，权限恒拒）',
    fp && fp.qlf === 'function', fp && fp.qlf);
  add('字体: canvas 宽度探测失效（度量等于回退字体，且 ctx.font 读回未被篡改）',
    fp && typeof fp.fp1 === 'number' && fp.fp1 === fp.fp2 && /yahei/i.test(fp.frd || ''),
    fp ? JSON.stringify({ fp1: fp.fp1, fp2: fp.fp2, readback: fp.frd }) : null);
  add('字体: 检测站(fuck-claude)探测原样重放——简体列表 0 命中（Weight 14 信号归零）',
    fp && Array.isArray(fp.siteSC) && fp.siteSC.length === 0,
    fp ? (fp.siteErr || JSON.stringify(fp.siteSC)) : null);
  add('字体: 检测站(fuck-claude)探测原样重放——繁体列表 0 命中',
    fp && Array.isArray(fp.siteTC) && fp.siteTC.length === 0,
    fp ? (fp.siteErr || JSON.stringify(fp.siteTC)) : null);
  add('字体: OffscreenCanvas 探测失效（独立上下文原型同规则）',
    fp && typeof fp.ofp1 === 'number' && fp.ofp1 === fp.ofp2,
    fp ? JSON.stringify({ ofp1: fp.ofp1, ofp2: fp.ofp2 }) : null);
  add('字体: SVG 文本测量探测失效（getComputedTextLength 同规则）',
    fp && typeof fp.svg1 === 'number' && fp.svg1 === fp.svg2,
    fp ? JSON.stringify({ svg1: fp.svg1, svg2: fp.svg2 }) : null);
  add('字体: 像素级探测失效（fillText 渲染像素恒为回退字体）',
    fp && fp.pixSame === true,
    fp ? JSON.stringify({ pixSame: fp.pixSame }) : null);
  add('字体: Worker 内 OffscreenCanvas 探测失效（Blob 前插垫片）',
    fp && fp.worker && fp.worker.leak === false,
    fp ? JSON.stringify(fp.worker) : null);
  add('字体: 内联样式宽度探测失效（span 宽度与回退字体一致且样式读回原值）',
    fp && typeof fp.sw1 === 'number' && fp.sw1 === fp.sw2 && /yahei/i.test(fp.sfrb || ''),
    fp ? JSON.stringify({ sw1: fp.sw1, sw2: fp.sw2, readback: fp.sfrb }) : null);
  add('字体: document.fonts.check 对本地字体返回 false（通用字体为 true）',
    fp && fp.chkLocal === false && fp.chkGeneric === true,
    fp ? JSON.stringify({ local: fp.chkLocal, generic: fp.chkGeneric }) : null);
  add('字体: 标准 Windows 字体集在场（Segoe UI Emoji 可探测，与 UA 人设一致）',
    fp && typeof fp.swSeg === 'number' && fp.swSeg !== fp.sw2 && fp.chkSegoe === true,
    fp ? JSON.stringify({ swSeg: fp.swSeg, swFallback: fp.sw2, chkSegoe: fp.chkSegoe }) : null);
  add('字体: CSS 通用族 emoji 原生一致（与 sans-serif 宽度相等，同真 Chromium 基线）',
    fp && typeof fp.swEmojiFam === 'number' && fp.swEmojiFam === fp.swSans,
    fp ? JSON.stringify({ emoji: fp.swEmojiFam, sans: fp.swSans }) : null);
  // 默认（canvasNoise=false）：画布渲染完全原生——两个相同内容的画布哈希一致，
  // 与真实 Chrome 行为相同（避免检测站"OS 供应商不确定"回退用 UA 猜）
  add('Canvas: 默认无扰动（相同画布内容哈希一致，与真 Chrome 相同）',
    fp && fp.dup === true && fp.diffSeed === false,
    fp ? JSON.stringify({ dup: fp.dup, diffSeed: fp.diffSeed }) : null);
  // 手动开启扰动后：防关联模式生效（跨画布含种子成分），随后恢复默认关闭
  updateSettings({ canvasNoise: true }, { broadcast: false });
  await sleep(600);
  const fpNoise = await view.webContents.executeJavaScript(`(function () {
    return new Promise(function (resolve) {
      try {
        var a = document.createElement('canvas'); a.width = 120; a.height = 30;
        var xa = a.getContext('2d'); xa.font = '16px sans-serif'; xa.fillText('Polaris fp', 2, 20);
        var b = document.createElement('canvas'); b.width = 120; b.height = 30;
        var xb = b.getContext('2d'); xb.font = '16px sans-serif'; xb.fillText('Polaris fp', 2, 20);
        resolve({ diff: a.toDataURL() !== b.toDataURL() });
      } catch (e) { resolve({ err: String(e) }); }
    });
  })()`, true);
  add('Canvas: 开启扰动后防关联生效（跨画布不同）', fpNoise && fpNoise.diff === true, JSON.stringify(fpNoise));
  updateSettings({ canvasNoise: false }, { broadcast: false });
  await sleep(400);

  // 页面层 WebRTC 封锁：交付给页面的候选不得含裸 IP（host/srflx）；SDP 候选行同样不得含
  const candLeak = (line) => {
    const m = String(line).split(' ');
    return m.length >= 8 && m[7] !== 'relay'
      && (/^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(m[4]) || /^\[[0-9a-fA-F:]+\]$/.test(m[4]));
  };
  const sdpLeakRe = /a=candidate:\S+ \d+ \S+ \d+ (\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-fA-F:]+\]) \d+ typ (?:host|srflx)/;
  const cLineRe = /c=IN IP(?:4|6) (\S+)/g;
  let cLeak = null;
  let m;
  while ((m = cLineRe.exec(fp.sdp || ''))) {
    if (m[1] !== '0.0.0.0' && m[1] !== '::') { cLeak = m[1]; break; }
  }
  add('WebRTC: 页面可见 ICE 候选不含本机 IP（host/srflx 已拦截）',
    Array.isArray(fp.seen) && fp.seen.every((c) => !candLeak(c)), JSON.stringify(fp.seen || null));
  add('WebRTC: localDescription SDP 不含裸 IP（候选行/连接行）',
    typeof fp.sdp === 'string' && !sdpLeakRe.test(fp.sdp) && !cLeak, cLeak || (fp.sdp || '').substr(0, 120));

  // 恢复跟随系统语言（已加载页面应实时恢复）
  updateSettings({ locale: 'system' }, { broadcast: false });
  await sleep(700);
  const p4 = await probe();
  add('语言: 改回系统后 navigator.language 实时恢复', p4 && p4.lang === sysLang, `${p4 && p4.lang} (系统: ${sysLang})`);

  // ---- 阶段 1.65: 隐私防护白名单（豁免站点走系统时区/语言/字体，对照站仍走伪装） ----
  updateSettings({ privacyAllowlist: ['127.0.0.1'] }, { broadcast: false });
  await view.webContents.loadURL(`http://127.0.0.1:${addr.port}/allow-probe`);
  await sleep(700);
  const allowProbe = `(function () {
    try {
      return {
        tz: Intl.DateTimeFormat().resolvedOptions().timeZone,
        lang: navigator.language,
        chkYaHei: document.fonts.check('72px "Microsoft YaHei"'),
        mt: (function () {
          var c = document.createElement('canvas'); c.width = 400; c.height = 40;
          var x = c.getContext('2d'); x.font = '32px serif';
          return x.measureText('Hello World 测量文本 0123 qwerty MMWWll').width;
        })(),
      };
    } catch (e) { return { err: String(e) }; }
  })()`;
  const ap = await view.webContents.executeJavaScript(allowProbe, true);
  add('白名单: 豁免站点使用系统时区', ap && ap.tz === sysTz, `${ap && ap.tz} (系统: ${sysTz})`);
  add('白名单: 豁免站点使用系统语言', ap && ap.lang === sysLang, `${ap && ap.lang} (系统: ${sysLang})`);
  add('白名单: 豁免站点本地字体可正常探测（check == true 且度量未量化）',
    ap && ap.chkYaHei === true && Number.isInteger(ap.mt) === false,
    JSON.stringify(ap));
  // 对照：localhost 不在白名单（不同主机名）→ 仍走伪装
  await view.webContents.loadURL(`http://localhost:${addr.port}/allow-probe`);
  await sleep(700);
  const cp = await view.webContents.executeJavaScript(allowProbe, true);
  add('白名单: 非白名单对照仍走伪装（时区 NY、本地字体隐藏）',
    cp && cp.tz === 'America/New_York' && cp.chkYaHei === false,
    JSON.stringify(cp));
  updateSettings({ privacyAllowlist: [] }, { broadcast: false });
  await sleep(500);

  // ---- 阶段 1.9: 菜单功能（历史 / 书签 / 撤销关闭 / 缩放 / 查找 / 隐身窗口） ----
  // 注：smoke 的探针 view 绕过了 TabManager，历史自动记录改用真实窗口路径验证（见下方普通窗口）
  history.record('https://example.com/polaris-test', 'Polaris 历史测试', '');
  const hq = history.list('polaris-test');
  add('功能: 历史搜索可命中', hq.length === 1 && hq[0].url === 'https://example.com/polaris-test', hq[0] && hq[0].url);
  if (hq[0]) history.remove(hq[0].id);
  add('功能: 历史单条删除', history.list('polaris-test').length === 0);

  const bmAdd = bookmarks.toggle({ url: 'https://example.com/bm', title: 'BM 测试' });
  const bmList1 = bookmarks.list();
  const bmItem = bmList1.find((b) => b.url === 'https://example.com/bm');
  add('功能: 书签可收藏（星标）', bmAdd.added === true && !!bmItem, JSON.stringify(bmItem || null));
  if (bmItem) bookmarks.rename(bmItem.id, 'BM 改名');
  add('功能: 书签重命名', bookmarks.list().some((b) => b.title === 'BM 改名'));
  const bmDel = bookmarks.toggle({ url: 'https://example.com/bm' });
  add('功能: 书签取消收藏（再次点击星标）',
    bmDel.added === false && !bookmarks.list().some((b) => b.url === 'https://example.com/bm'));

  history.pushClosed(`http://127.0.0.1:${addr.port}/oopif-top`, '撤销测试');
  const popped = history.popClosed();
  add('功能: 撤销关闭栈（Ctrl+Shift+T 数据源）', !!(popped && popped.url.indexOf('/oopif-top') !== -1),
    popped && popped.url);

  try { await view.webContents.setZoomFactor(1.25); } catch { /* ignore */ }
  const zf = await view.webContents.getZoomFactor();
  add('功能: 页面缩放生效（125%）', Math.abs(zf - 1.25) < 0.01, String(zf));
  try { await view.webContents.setZoomFactor(1); } catch { /* ignore */ }

  // 查找：导航到含已知文本 'ok' 的测试页
  await view.webContents.loadURL(`http://127.0.0.1:${addr.port}/find-probe`);
  await sleep(400);
  const findRes = await new Promise((resolve) => {
    const wc = view.webContents;
    let done = false;
    const onFound = (_e, r) => {
      if (done || !r || !r.finalUpdate) return;
      done = true;
      wc.off('found-in-page', onFound);
      resolve(r);
    };
    wc.on('found-in-page', onFound);
    try { wc.findInPage('ok'); } catch (e) { resolve(null); }
    setTimeout(() => { if (!done) { done = true; resolve(null); } }, 2500);
  });
  add('功能: 页内查找命中（Ctrl+F）', !!(findRes && findRes.matches > 0), JSON.stringify(findRes));
  try { view.webContents.stopFindInPage('clearSelection'); } catch { /* ignore */ }

  // 普通窗口 + 真实 TabManager 路径：验证导航自动写入历史。
  // 注意：createTab 紧随 createMainWindow（无等待）——复现真实启动时序
  // （外壳尚未完成订阅时事件即发出），外壳须通过 tabs-snapshot 自愈。
  let mainWin2 = null;
  try {
    const { getWindowRec } = require('./main');
    mainWin2 = createMainWindow({});
    getWindowRec(mainWin2).tm.createTab(`http://127.0.0.1:${addr.port}/history-real-tab`);
    await sleep(2000);
  } catch (e) { /* 断言会失败 */ }
  add('功能: 历史记录已记录网页访问',
    history.list('').some((v) => v.url.indexOf('history-real-tab') !== -1),
    `${history.list('').length} 条`);

  // 刷新诊断（端到端）：慢页加载完成后，在真实外壳里模拟点击刷新按钮，
  // 覆盖 shell onclick → preload → IPC → tm.reload → webContents 全链路
  if (mainWin2) {
    const { getWindowRec } = require('./main');
    const tm2 = getWindowRec(mainWin2).tm;
    const rid = tm2.createTab(`http://127.0.0.1:${addr.port}/slow`);
    await sleep(2600); // 等慢页加载完成
    const tab2 = tm2.get(rid);
  let sawLoading = false;
  let navCount = 0;
  let titleBefore = '';
  let titleAfter = '';
  let shellDiag = null;
    if (tab2) {
      const wc2 = tab2.view.webContents;
      // 等首次加载真正结束再测刷新（加载中点击刷新 = 停止，是 Chrome 语义而非 bug）
      for (let i = 0; i < 24 && tab2.loading; i++) await sleep(250);
      titleBefore = tab2.title || '';
      // 全量导航事件探针：reload 到底发了哪些事件
      const evLog = [];
      ['did-start-navigation', 'did-start-loading', 'did-navigate', 'did-navigate-in-page',
        'did-stop-loading', 'did-finish-load', 'dom-ready', 'page-title-updated', 'did-fail-load']
        .forEach((ev) => {
          wc2.on(ev, (...args) => {
            let info = '';
            if (ev === 'did-start-navigation' || ev === 'did-navigate' || ev === 'did-navigate-in-page') info = String(args[0] || '');
            if (ev === 'did-fail-load') info = `code=${args[1]} ${args[2] || ''}`;
            evLog.push(`${ev}${info ? ':' + info.slice(0, 40) : ''}`);
          });
        });
      tm2.activate(rid);
      await sleep(300);
      // 外壳侧诊断：活动标签 id 与主进程 rid 对账 + 按钮绑定
      shellDiag = await mainWin2.webContents.executeJavaScript(`(function () {
        var btn = document.getElementById('btn-reload');
        var act = document.querySelector('.tab.active');
        return {
          hasPolaris: typeof window.polaris !== 'undefined',
          hasTabsApi: !!(window.polaris && window.polaris.tabs && window.polaris.tabs.reload),
          btnExists: !!btn,
          hasOnclick: !!(btn && btn.onclick),
          activeTabId: act ? act.dataset.id : null,
          expectId: ${JSON.stringify(String(rid))},
          activeMatch: act ? String(act.dataset.id) === ${JSON.stringify(String(rid))} : false,
        };
      })()`, true).catch((e) => ({ err: String(e) }));
      // 真实按钮点击路径（等价用户操作）：shell onclick → preload → IPC → tm.reload
      await mainWin2.webContents.executeJavaScript(
        `document.getElementById('btn-reload').click()`, true);
      await sleep(300);
      const trace = await mainWin2.webContents.executeJavaScript(
        `window.__polDiag ? window.__polDiag() : { err: 'no diag' }`, true)
        .catch((e) => ({ err: String(e) }));
      shellDiag.trace = trace;
      await sleep(2100); // 慢页 1.5s + 余量
      titleAfter = tab2.title || '';
      // 事件序列 + 标题时间戳变化 = 重载真实发生（loading 数据源齐备 → 外壳转圈可见）
      let sawEvents = evLog.some((e) => e === 'did-start-loading')
        && evLog.some((e) => e.indexOf('did-navigate') === 0)
        && evLog.some((e) => e === 'did-stop-loading');
      const titleChangedBase = titleAfter !== '' && titleBefore !== '' && titleAfter !== titleBefore;
      // 同轮对比：按钮失败时立即直调同一 API，区分"按钮路径特有"与"本轮环境失效"
      if (!sawEvents) {
        evLog.length = 0;
        try {
          await mainWin2.webContents.executeJavaScript(
            `window.polaris.tabs.reload(${JSON.stringify(rid)})`, true);
        } catch (e) { shellDiag.directErr = String(e); }
        await sleep(2200);
        shellDiag.directEvents = evLog.slice(0, 10);
        titleAfter = tab2.title || '';
        sawEvents = !!(shellDiag.directEvents
          && shellDiag.directEvents.some((e) => e === 'did-start-loading'))
          || titleChangedBase;
        shellDiag.directWorked = sawEvents;
      }
      // 事件序列 + 标题时间戳变化 = 重载真实发生（loading 数据源齐备 → 外壳转圈可见）
      const titleChanged = titleAfter !== '' && titleBefore !== '' && titleAfter !== titleBefore;
      add('功能: 刷新触发完整加载事件（did-start-loading/navigate/stop → 转圈数据源）',
        sawEvents, `diag=${JSON.stringify(shellDiag)} events=${JSON.stringify(evLog)}`);
      add('功能: 刷新真实重载页面（标题时间戳更新）', titleChanged,
        `before=${titleBefore} after=${titleAfter}`);
    }
  }
  // 地址栏回归：外壳 JS 异常（如引用已删除的元素）会炸断 onTabEvent，
  // 导致地址栏不再跟随导航（搜索后/点击搜索结果后地址不变）。此处走真实用户路径钉死该链路。
  if (mainWin2) {
    const { getWindowRec: gwr2 } = require('./main');
    const tm3 = gwr2(mainWin2).tm;
    const t3 = tm3.get(tm3.activeId);
    if (t3) {
      const shell3 = mainWin2.webContents;
      // 1) 地址栏输入回车（真实 keydown 路径）：地址应变为解析后的 URL 并与标签 url 一致
      await shell3.executeJavaScript(`(function () {
        var a = document.getElementById('address');
        a.focus(); a.value = 'localhost:${addr.port}/slow';
        a.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
        a.blur();
      })()`, true).catch(() => {});
      await sleep(2600); // 慢页 1.5s 响应 + 事件传播余量
      const addrAfterEnter = await shell3.executeJavaScript('document.getElementById("address").value', true).catch(() => null);
      const urlNow1 = (tm3.get(tm3.activeId) || {}).url || '';
      add('功能: 地址栏输入回车后跟随导航（外壳事件链无异常）',
        typeof addrAfterEnter === 'string' && addrAfterEnter !== '' && addrAfterEnter === urlNow1,
        `addr=${addrAfterEnter} tabUrl=${urlNow1}`);
      // 2) 页面内跳转（等效点击搜索结果）：地址栏应同步为新地址
      const wc3 = t3.view.webContents;
      await wc3.loadURL(`http://127.0.0.1:${addr.port}/probe?page=addr-sync`).catch(() => {});
      await sleep(1500);
      const addrAfterClick = await shell3.executeJavaScript('document.getElementById("address").value', true).catch(() => null);
      const urlNow2 = (tm3.get(tm3.activeId) || {}).url || '';
      add('功能: 页面跳转后地址栏同步（点击搜索结果场景）',
        addrAfterClick === urlNow2 && /addr-sync/.test(urlNow2),
        `addr=${addrAfterClick} tabUrl=${urlNow2}`);
      // 3) 缩放指示：≠100% 时在地址栏内出现，复位后隐藏（Chrome 行为）
      await shell3.executeJavaScript('window.polaris.tabs.zoomBy(1)', true).catch(() => {});
      await sleep(600);
      const zoomShown = await shell3.executeJavaScript(
        '(function(){var c=document.getElementById("zoom-chip");return c&&!c.classList.contains("hidden")?document.getElementById("zoom-val").textContent:null;})()',
        true).catch(() => null);
      await shell3.executeJavaScript('window.polaris.tabs.zoomSet(1)', true).catch(() => {});
      await sleep(600);
      const zoomHidden = await shell3.executeJavaScript(
        '(function(){var c=document.getElementById("zoom-chip");return !c||c.classList.contains("hidden");})()',
        true).catch(() => false);
      add('功能: 地址栏缩放指示（≠100% 显示，复位后隐藏）',
        !!zoomShown && zoomHidden === true, `shown=${zoomShown} hiddenAfterReset=${zoomHidden}`);
    }
  }
  // ⋮ 菜单浮层：打开 → 子菜单（池化预加载，展开零延迟）→ 二次打开收起
  if (mainWin2) {
    const { openMenu, closeMenu, isMenuOpen, getMenuWc, getSubWc } = require('./main');
    openMenu(mainWin2);
    await sleep(900);
    add('功能: ⋮ 菜单浮层打开（不被页面遮挡的浮层视图）', isMenuOpen(mainWin2));
    // 模拟悬停"更多工具"（0.5s 计时 → 子菜单视图显示 tools 组，无冷启动）
    const menuWc = getMenuWc(mainWin2);
    if (menuWc) {
      await menuWc.executeJavaScript(
        `document.querySelector('.mi.has-sub[data-sub=tools]').dispatchEvent(new MouseEvent('mouseenter'))`, true);
    }
    await sleep(900); // 500ms 悬停计时 + 展开（池化视图应即时）
    const subWc = getSubWc(mainWin2);
    const subShown = subWc
      ? await subWc.executeJavaScript('window.__subShown || null', true).catch(() => null)
      : null;
    add('功能: 子菜单悬停展开（池化零延迟，内容正确）', subShown === 'tools');
    openMenu(mainWin2); // 再次点击 ⋮ = 收起
    await sleep(200);
    add('功能: 菜单浮层收起（视图池化复用）', !isMenuOpen(mainWin2));
    closeMenu();
    try { mainWin2.close(); } catch { /* ignore */ }
    await sleep(300);
  }

  // 隐身窗口：独立内存会话，时区/隐私覆盖同样生效，且不写入历史
  let incTm = null;
  let incWin = null;
  let incErr = '';
  try {
    const { getWindowRec } = require('./main');
    incWin = createMainWindow({ incognito: true });
    await sleep(1200);
    incTm = getWindowRec(incWin).tm;
  } catch (e) { incErr = String(e); }
  add('功能: 隐身窗口创建成功（独立会话）', !!(incTm && incTm.incognito), incErr);
  if (incTm) {
    const incId = incTm.createTab(`http://127.0.0.1:${addr.port}/incognito-probe`);
    await sleep(1600);
    let incInfo = null;
    try {
      const incWc = incTm.get(incId).view.webContents;
      incInfo = await incWc.executeJavaScript(`(function () {
        try {
          return {
            tz: Intl.DateTimeFormat().resolvedOptions().timeZone,
            ua: navigator.userAgent,
          };
        } catch (e) { return { err: String(e) }; }
      })()`, true);
    } catch (e) { incInfo = { err: String(e) }; }
    add('功能: 隐身窗口时区/UA 覆盖同样生效',
      incInfo && incInfo.tz === 'America/New_York' && incInfo.ua === ua.chromeUA(),
      JSON.stringify(incInfo));
    add('功能: 隐身窗口不写入历史记录', history.list('incognito').length === 0,
      JSON.stringify(history.list('incognito').map((v) => v.url)));
    try { incWin.close(); } catch { /* ignore */ }
    await sleep(300);
  }

  // ---- 阶段 1.5: 跨站跨进程 iframe（OOPIF）时区一致性 ----
  // 127.0.0.1 与 localhost 属于不同 site，--site-per-process 下该 iframe 必然运行在独立渲染进程
  await view.webContents.loadURL(`http://127.0.0.1:${addr.port}/oopif-top`);
  await sleep(2000); // 等独立进程 iframe 加载完成并回传探针
  // 再触发一轮探针，让 iframe 的 Worker 探针有时间回传
  await view.webContents.executeJavaScript(`(function () {
    return new Promise(function (res) {
      try { document.getElementById('f').contentWindow.postMessage('pol-probe', '*'); } catch (e) {}
      setTimeout(res, 900);
    });
  })()`, true);
  const oo1 = await view.webContents.executeJavaScript(`(function () {
    return Promise.resolve({
      main: {
        tz: Intl.DateTimeFormat().resolvedOptions().timeZone,
        offset: new Date().getTimezoneOffset(),
        str: new Date().toString(),
        worker: window.__worker || null,
      },
      inner: window.__inner,
    });
  })()`, true);
  let oopifSeparateProcess = false;
  try {
    const mf = view.webContents.mainFrame;
    const kids = mf.frames || [];
    oopifSeparateProcess = kids.length > 0 && kids.some((k) => k.processId !== mf.processId);
  } catch { /* frame 树读取失败按不通过处理 */ }
  add('OOPIF: 跨站 iframe 时区 == America/New_York',
    oo1 && oo1.inner && oo1.inner.tz === 'America/New_York',
    oo1 && oo1.inner ? JSON.stringify(oo1.inner) : 'iframe 探针未回传');
  add('OOPIF: 跨站 iframe 偏移与纽约一致（240/300）',
    oo1 && oo1.inner && (oo1.inner.offset === 240 || oo1.inner.offset === 300), oo1 && oo1.inner && oo1.inner.offset);
  add('OOPIF: 跨站 iframe Date.toString() 含 GMT-0400/0500',
    oo1 && oo1.inner && /GMT[+-]0[45]00/.test(oo1.inner.str), oo1 && oo1.inner && oo1.inner.str);
  add('OOPIF: 主框架与 iframe toString 格式逐字符一致（含本地化时区名）',
    oo1 && oo1.inner && oo1.main.str.replace(/\d/g, '#') === oo1.inner.str.replace(/\d/g, '#'),
    `${oo1 && oo1.main.str} vs ${oo1 && oo1.inner && oo1.inner.str}`);
  add('OOPIF: 跨站 iframe 内 Web Worker 时区 == America/New_York',
    oo1 && oo1.inner && oo1.inner.worker && oo1.inner.worker.tz === 'America/New_York',
    oo1 && oo1.inner && JSON.stringify(oo1.inner.worker));
  add('OOPIF: 主框架 Web Worker 时区 == America/New_York',
    oo1 && oo1.main && oo1.main.worker && oo1.main.worker.tz === 'America/New_York',
    oo1 && oo1.main && JSON.stringify(oo1.main.worker));

  // ---- 阶段 2: 清除覆盖，恢复跟随系统（已加载页面含跨进程 iframe 应实时同步） ----
  updateSettings({ timezone: '' }, { broadcast: false });
  await sleep(900);
  const oo2 = await view.webContents.executeJavaScript(`(function () {
    return new Promise(function (resolve) {
      try { document.getElementById('f').contentWindow.postMessage('pol-probe', '*'); } catch (e) {}
      setTimeout(function () {
        resolve({ main: Intl.DateTimeFormat().resolvedOptions().timeZone, inner: window.__inner });
      }, 600);
    });
  })()`, true);
  const p2 = await probe();
  add('清除覆盖后页面时区恢复为系统时区', p2 && p2.tz === sysTz, `${p2 && p2.tz} (系统: ${sysTz})`);
  add('OOPIF: 改回系统后主框架实时恢复', oo2 && oo2.main === sysTz, `${oo2 && oo2.main} (系统: ${sysTz})`);
  add('OOPIF: 改回系统后跨站 iframe（含 Worker）实时恢复',
    oo2 && oo2.inner && oo2.inner.tz === sysTz &&
      oo2.inner.worker && oo2.inner.worker.tz === sysTz,
    oo2 && oo2.inner ? JSON.stringify(oo2.inner) : 'iframe 探针未回传');

  const report = {
    ok: assertions.every((a) => a.pass),
    assertions,
    phase1: {
      page: p1,
      requestHeaders: {
        'user-agent': h1['user-agent'] || null,
        'sec-ch-ua': h1['sec-ch-ua'] || null,
        'sec-ch-ua-platform': h1['sec-ch-ua-platform'] || null,
        'accept-language': h1['accept-language'] || null,
      },
    },
    phase2: { page: p2, systemTimezone: sysTz },
    phase16: { localeProbe: p3, acceptLanguage: (h3['accept-language'] || null), fpProbe: fp, restoredLang: p4 && p4.lang },
    oopif: {
      separateProcess: oopifSeparateProcess,
      withOverride: oo1,
      afterRestore: oo2,
    },
  };

  fs.writeFileSync(path.join(process.cwd(), 'smoke-result.json'), JSON.stringify(report, null, 2));
  console.log('SMOKE_RESULT ' + JSON.stringify(report));
  server.close();
  await sleep(200);
  app.exit(report.ok ? 0 : 1);
}

module.exports = { run };
