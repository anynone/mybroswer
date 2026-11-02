'use strict';

const ENGINES = {
  bing: 'https://www.bing.com/search?q=',
  baidu: 'https://www.baidu.com/s?wd=',
  google: 'https://www.google.com/search?q=',
};

// 地址栏输入解析：URL 直接访问，其余按搜索引擎搜索
function resolveInput(input, engine = 'bing') {
  const s = String(input || '').trim();
  if (!s) return null;
  if (/^(https?|file|browser|about|data|view-source|chrome-extension):/i.test(s)) return s;
  if (/^localhost(:\d+)?([/?#].*)?$/i.test(s)) return `http://${s}`;
  if (/^[\w-]+(\.[\w-]+)+(:\d+)?([/?#].*)?$/.test(s)) return `https://${s}`;
  const base = ENGINES[engine] || ENGINES.bing;
  return base + encodeURIComponent(s);
}

module.exports = { resolveInput, ENGINES };
