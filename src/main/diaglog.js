'use strict';
// 轻量诊断日志：写入 userData/polaris-diag.log（启动时重置），用于排查真实环境问题。
// 只记录关键导航/刷新/停止节点，量级极小。

const fs = require('fs');
const path = require('path');
const { app } = require('electron');

let file = null;

function log(msg) {
  try {
    if (!file) file = path.join(app.getPath('userData'), 'polaris-diag.log');
    fs.appendFileSync(file, `${new Date().toISOString().slice(11, 23)} ${msg}\n`);
  } catch { /* ignore */ }
}

// 启动时重置日志并写头部摘要
function reset(header) {
  try {
    file = path.join(app.getPath('userData'), 'polaris-diag.log');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, `===== ${new Date().toISOString()} =====\n${header || ''}\n`);
  } catch { /* ignore */ }
}

module.exports = { log, reset };
