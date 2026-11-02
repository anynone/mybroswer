'use strict';
const { secChUaHeader, secChUaFullListHeader } = require('./ua');

// 网络层兜底：即使某条请求早于 CDP 覆盖生效，出口请求头也与真实 Chrome 一致。
// 注意：只"改写已存在的头"，不新增头，保持与 Chrome 完全相同的头部出现语义（避免指纹差异）。
function install(sess, env) {
  if (!env || env.uaCustom) {
    // 自定义 UA 模式交由用户全权负责
    sess.webRequest.onBeforeSendHeaders(null);
    return;
  }
  const brands = secChUaHeader(env.metadata);
  const fullList = secChUaFullListHeader(env.metadata);
  const platform = `"${env.metadata.platform}"`;
  const acceptLanguage = env.acceptLanguages || null;

  sess.webRequest.onBeforeSendHeaders({ urls: ['http://*/*', 'https://*/*'] }, (details, callback) => {
    const src = details.requestHeaders;
    const out = {};
    let hasAcceptLanguage = false;
    for (const key of Object.keys(src)) {
      const lk = key.toLowerCase();
      if (lk === 'user-agent') out[key] = env.userAgent;
      else if (lk === 'sec-ch-ua') out[key] = brands;
      else if (lk === 'sec-ch-ua-full-version-list') out[key] = fullList;
      else if (lk === 'sec-ch-ua-platform') out[key] = platform;
      else if (lk === 'accept-language' && acceptLanguage) {
        hasAcceptLanguage = true;
        out[key] = acceptLanguage;
      } else out[key] = src[key];
    }
    if (!hasAcceptLanguage && acceptLanguage) out['Accept-Language'] = acceptLanguage;
    callback({ requestHeaders: out });
  });
}

module.exports = { install };
