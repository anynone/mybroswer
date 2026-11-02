'use strict';
// 截图模式：electron . --shot <url1> <url2> ...
// 或通过环境变量 SHOT_URLS="browser://newtab/,browser://settings/" 传递（Windows shell 下更可靠）
// 依次打开给定 URL（默认新标签页），对外壳与页面内容分别截图保存，便于无人工验证 UI。
const fs = require('fs');

function run({ win, tm }) {
  const argv = process.argv;
  const idx = argv.indexOf('--shot');
  const fromArgv = argv.slice(idx + 1).filter((a) => !a.startsWith('--'));
  const fromEnv = (process.env.SHOT_URLS || '').split(',').map((s) => s.trim()).filter(Boolean);
  const list = fromEnv.length ? fromEnv : (fromArgv.length ? fromArgv : ['browser://newtab/']);
  console.log('SHOT_TARGETS ' + JSON.stringify(list));

  (async () => {
    await new Promise((r) => setTimeout(r, 1000));
    for (let k = 0; k < list.length; k++) {
      if (win.isDestroyed()) return;
      tm.createTab(list[k], { active: true });
      await new Promise((r) => setTimeout(r, 2600));
      if (win.isDestroyed()) return;
      const tag = String(list[k]).replace(/\W+/g, '-').replace(/^-|-$/g, '').slice(0, 28) || 'page';
      try {
        const shellImg = await win.webContents.capturePage();
        fs.writeFileSync(`shot-${k}-${tag}-shell.png`, shellImg.toPNG());
      } catch (e) {
        console.warn('shell capture failed:', e.message);
      }
      const tab = tm.activeTab;
      if (tab) {
        try {
          const tabImg = await tab.view.webContents.capturePage();
          fs.writeFileSync(`shot-${k}-${tag}-tab.png`, tabImg.toPNG());
        } catch (e) {
          console.warn('tab capture failed:', e.message);
        }
      }
      console.log(`SHOT_OK ${k} ${list[k]}`);
    }
    // 额外：截取真实打开的 ⋮ 菜单浮层（覆盖在页面之上的浮层视图本体）
    try {
      const { openMenu, closeMenu } = require('./main');
      openMenu(win);
      await new Promise((r) => setTimeout(r, 1000));
      // openMenu 后菜单视图获得焦点；capturePage 前避免 blur 关闭——直接从 getAllWebContents 找
      const { webContents } = require('electron');
      const activeWc = tm.activeTab ? tm.activeTab.view.webContents : null;
      const menuWc = webContents.getAllWebContents()
        .find((wc) => !wc.isDestroyed() && wc !== activeWc && wc.getURL().startsWith('browser://menu/'));
      if (menuWc) {
        const img = await menuWc.capturePage();
        fs.writeFileSync('shot-menu-overlay.png', img.toPNG());
        console.log('SHOT_MENU_OK');
      }
      closeMenu();
    } catch (e) {
      console.warn('menu capture failed:', e.message);
    }
    console.log('SHOT_DONE');
    const { app } = require('electron');
    app.quit();
  })();
}

module.exports = { run };
