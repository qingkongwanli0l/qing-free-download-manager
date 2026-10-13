/* SPDX-License-Identifier: MIT */
"use strict";
if (typeof importScripts === "function") importScripts("i18n.js");
const t = QingI18n.text;

const api = chrome;
const HOST = "com.qing.download_manager";
const defaults = { captureEnabled: false, mediaEnabled: true, useCredentials: false,
  suppressBlankTab: false, forbidBrowserReturn: false,
  captureSubtitles: true, offerSubtitles: true, showExpandedStreams: false,
  showButtons: true, mediaMode: "button", buttonSize: "standard", buttonOpacity: 100, hideFullscreen: true,
  excludedHosts: [], mediaExcludedHosts: [], excludedExtensions: [], excludedUrls: [], bypassKey: "alt" };
let settings = { ...defaults };
let hostConfig = {};
let lastNotice = "";
const requests = new Map();
const media = new Map();
const mediaFormats = new Map();
const bypass = new Map();
const pending = new Map();
const activeCaptures = new Set();
const newTabs = new Map();
const linkClicks = new Map();
const refusals = new Map();
const exclusionSuggestions = new Map();
function unconfirmedNotice() { return t("Qing 的接收结果尚未确认，请核对任务后决定是否继续浏览器下载。"); }
let pendingWrite = Promise.resolve();
let boxSession = "", boxOpen = false;
const boxTimers = new Map();
let boxWrite = Promise.resolve();

function call(object, method, ...args) {
  return new Promise((resolve, reject) => {
    object[method](...args, result => {
      const error = api.runtime.lastError;
      if (error) reject(new Error(error.message));
      else resolve(result);
    });
  });
}

function native(action, fields = {}) {
  const id = crypto.randomUUID ? crypto.randomUUID() : Date.now() + "-" + Math.random();
  return new Promise((resolve, reject) => {
    const timeout = action === "add" ? 600000 : action === "mediaFormats" ? 125000 : 15000;
    let port, done = false, sent = false;
    const finish = (error, reply) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      if (port) port.disconnect();
      if (error) reject(error);
      else resolve(reply);
    };
    const failure = (text, uncertain = action === "add" && sent) => Object.assign(new Error(uncertain ? unconfirmedNotice() : text), { uncertain });
    const timer = setTimeout(() => finish(failure(t("桌面端未及时响应，请检查 Qing 是否正在运行。"))), timeout);
    try {
      port = api.runtime.connectNative(HOST);
      port.onDisconnect.addListener(() => {
        const error = api.runtime.lastError;
        finish(failure(error ? t("无法连接 Qing 桌面端，请安装或修复浏览器集成。") : t("与 Qing 桌面端的连接已中断。")));
      });
      port.onMessage.addListener(reply => {
        if (!reply || reply.id !== id || typeof reply.ok !== "boolean")
          return finish(failure(t("桌面端返回了无法识别的响应。")));
        if (!reply.ok) return finish(failure(reply.error || t("桌面端未接受下载。"), reply.uncertain === true));
        finish(null, reply);
      });
      sent = true;
      port.postMessage({ id, action, ...fields });
    } catch {
      finish(failure(t("无法连接 Qing 桌面端，请安装或修复浏览器集成。")));
    }
  });
}

function savePending() {
  const value = [...pending].map(([id, accepted]) => ({ id, accepted }));
  pendingWrite = pendingWrite.catch(() => {}).then(() => call(api.storage.local, "set", { pendingDownloads: value }));
  return pendingWrite;
}

async function initialize() {
  const data = await call(api.storage.local, "get", ["settings", "pendingDownloads", "boxSession", "boxOpen", "refusalCounts", "mediaMinimumSizeKb", "language"]);
  QingI18n.setLanguage(data.language);
  settings = { ...defaults, ...data.settings };
  hostConfig.mediaMinimumSizeKb = Number(data.mediaMinimumSizeKb) || 0;
  for (const [key, count] of data.refusalCounts || []) refusals.set(key, count);
  if (data.settings && !Object.hasOwn(data.settings, "mediaExcludedHosts")) settings.mediaExcludedHosts = [...settings.excludedHosts];
  boxSession = data.boxSession || (crypto.randomUUID ? crypto.randomUUID() : Date.now() + "-" + Math.random());
  boxOpen = data.boxOpen === true && settings.mediaMode === "box";
  await call(api.storage.local, "set", { boxSession });
  for (const entry of data.pendingDownloads || []) {
    try {
      const [download] = await call(api.downloads, "search", { id: entry.id });
      if (download && download.state === "in_progress") {
        if (entry.accepted === null && download.paused) {
          pending.set(entry.id, null);
          notice(unconfirmedNotice());
        }
        else if (entry.accepted === true) await call(api.downloads, "cancel", entry.id);
        else if (download.paused) await call(api.downloads, "resume", entry.id);
      }
    } catch (error) {
      pending.set(entry.id, entry.accepted);
      notice(t("有下载需要恢复，请打开浏览器下载列表：") + error.message);
    }
  }
  await savePending();
}
const ready = initialize();

function notice(text, tabId, isError = true) {
  lastNotice = text;
  api.action.setBadgeBackgroundColor({ color: isError ? "#b45309" : "#087e8b" });
  api.action.setBadgeText({ text: isError ? "!" : "" });
  if (tabId >= 0) api.tabs.sendMessage(tabId, { action: "notice", text }, () => void api.runtime.lastError);
}

function supported(url) {
  return /^(https?|ftps?):\/\//i.test(url || "") || /^magnet:\?/i.test(url || "");
}
function hostname(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch { return ""; }
}
function hostExcluded(url, referer = "", kind = "excludedHosts") {
  const itemHosts = [hostname(url), hostname(referer)];
  const hosts = [...settings[kind], ...(hostConfig[kind] || [])];
  return hosts.some(value => {
    const domain = value.toLowerCase().replace(/^\*\./, "").trim();
    return domain && itemHosts.some(host => host === domain || host.endsWith("." + domain));
  });
}
function mediaExcluded(item) { return hostExcluded(item.url, item.referer, "mediaExcludedHosts"); }
function visibleMedia(tabId) {
  const minimum = Math.max(0, Number(hostConfig.mediaMinimumSizeKb) || 0) * 1024;
  return [...(media.get(tabId) || new Map()).values()].filter(item => {
    if (item.kind === "subtitle") return settings.mediaMode === "box" ? settings.captureSubtitles : settings.offerSubtitles;
    return !["video", "audio"].includes(item.kind) || !Number.isFinite(item.totalBytes) || item.totalBytes >= minimum;
  });
}
function mediaOptions(url = "") {
  return { enabled: enabled("mediaEnabled") && !hostExcluded(url, "", "mediaExcludedHosts"),
    showButtons: settings.showButtons && settings.mediaMode === "button", buttonSize: settings.buttonSize,
    buttonOpacity: settings.buttonOpacity, hideFullscreen: settings.hideFullscreen, showExpandedStreams: settings.showExpandedStreams,
    language: QingI18n.language() };
}
function excluded(item) {
  if (hostExcluded(item.url, item.referer)) return true;
  if (settings.excludedUrls.some(mask => new RegExp("^" + mask.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\\\*/g, ".*") + "$").test(item.url))) return true;
  const name = item.fileName || new URL(item.url).pathname.split("/").pop();
  const extension = (name.split(".").pop() || "").toLowerCase();
  return [...settings.excludedExtensions, ...(hostConfig.excludedExtensions || [])]
    .some(value => value.toLowerCase().replace(/^\./, "") === extension);
}
async function refusalKey(url) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(url));
  return Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
}
async function saveRefusals() {
  await call(api.storage.local, "set", { refusalCounts: [...refusals] }).catch(error => notice(t("未能保存取消计数：") + error.message));
}
async function refused(url, tabId) {
  if (hostConfig.suggestBrowserExclusions === false) return null;
  const key = await refusalKey(url);
  const count = Math.min(3, (refusals.get(key) || 0) + 1);
  refusals.set(key, count);
  await saveRefusals();
  if (hostConfig.suggestBrowserExclusions === false || count < 3 || [...exclusionSuggestions.values()].some(value => value.url === url)) return null;
  const address = new URL(url);
  address.username = ""; address.password = ""; address.hash = "";
  const query = !!address.search;
  address.search = "";
  const suggestion = { id: crypto.randomUUID ? crypto.randomUUID() : Date.now() + "-" + Math.random(),
    url, tabId, mask: address.href + (query ? "*" : "") };
  exclusionSuggestions.set(suggestion.id, suggestion);
  return suggestion;
}
function captureType(item) {
  const name = item.fileName || new URL(item.url).pathname.split("/").pop();
  const extension = /\.([^.]+)$/.exec(name);
  return !!extension && (hostConfig.extensions || [])
    .some(value => value.toLowerCase().replace(/^\./, "") === extension[1].toLowerCase());
}
function isBypassed(tabId) {
  const state = bypass.get(tabId);
  return !!state && Date.now() - state.time < 2000 && state[settings.bypassKey];
}
function enabled(name) { return settings[name] && hostConfig[name] !== false; }
function mediaKind(url, mime = "") {
  const path = url.split(/[?#]/)[0].toLowerCase();
  if (/mpegurl|dash\+xml/i.test(mime) || /\.(m3u8|mpd)$/.test(path)) return "stream";
  if (/^video\//i.test(mime) || /\.(mp4|webm|mkv|mov|m4v|avi|ts)$/.test(path)) return "video";
  if (/^audio\//i.test(mime) || /\.(mp3|m4a|aac|flac|ogg|wav|opus)$/.test(path)) return "audio";
  if (/vtt|subrip/i.test(mime) || /\.(vtt|srt|ass|ssa)$/.test(path)) return "subtitle";
  return "";
}
function cleanHeaders(headers) {
  const result = {};
  for (const [name, value] of Object.entries(headers || {})) {
    if (/^(host|connection|content-length|accept-encoding|range|if-range|if-none-match|if-modified-since)$/i.test(name) || /^sec-/i.test(name)) continue;
    if (!settings.useCredentials && /^(cookie|authorization|proxy-authorization)$/i.test(name)) continue;
    result[name] = value;
  }
  return result;
}
function header(headers, name) {
  const entry = Object.entries(headers || {}).find(([key]) => key.toLowerCase() === name);
  return entry ? entry[1] : "";
}
function requestFor(url, referer = "") {
  return [...requests.values()].reverse().find(record => record.url === url &&
    (!referer || !header(record.headers, "referer") || header(record.headers, "referer") === referer));
}
function formBody(record) {
  if (record.method !== "POST") return undefined;
  if (!record.formData) throw new Error(t("此 POST 下载无法完整传递，请使用浏览器下载。"));
  const type = header(record.headers, "content-type");
  if (/application\/x-www-form-urlencoded/i.test(type)) {
    const data = new URLSearchParams();
    for (const [name, values] of Object.entries(record.formData))
      for (const value of values) data.append(name, value);
    return data.toString();
  }
  if (/multipart\/form-data/i.test(type)) {
    const boundary = "QingForm" + Date.now().toString(16);
    for (const key of Object.keys(record.headers))
      if (key.toLowerCase() === "content-type") record.headers[key] = "multipart/form-data; boundary=" + boundary;
    let body = "";
    for (const [name, values] of Object.entries(record.formData))
      for (const value of values)
        body += "--" + boundary + '\r\nContent-Disposition: form-data; name="' +
          name.replace(/[\r\n"]/g, "") + '"\r\n\r\n' + value + "\r\n";
    return body + "--" + boundary + "--\r\n";
  }
  throw new Error(t("此 POST 类型暂不支持接管，请使用浏览器下载。"));
}
async function downloadItem(input, record, tabId) {
  const item = { url: input.url, fileName: input.fileName || "", referer: input.referer || "",
    sourcePage: input.sourcePage || record?.sourcePage || "",
    linkText: input.linkText || "", method: "GET", headers: {}, mime: input.mime || "", kind: input.kind === "stream" ? "media" : "file" };
  if (record) {
    item.method = record.method;
    item.body = formBody(record);
    item.headers = cleanHeaders(record.headers);
    item.referer = item.referer || header(record.headers, "referer");
    item.mime = item.mime || record.mime || "";
  }
  if (!header(item.headers, "user-agent")) item.headers["User-Agent"] = navigator.userAgent;
  if (mediaKind(item.url, item.mime) === "stream") item.kind = "media";
  if (item.referer) item.headers.Referer = item.referer;
  if (settings.useCredentials && /^https?:/i.test(item.url) && !header(item.headers, "cookie")) {
    const stores = await call(api.cookies, "getAllCookieStores");
    const store = stores.find(entry => entry.tabIds.includes(record ? record.tabId : tabId));
    const cookies = store ? await call(api.cookies, "getAll", { url: item.url, storeId: store.id }) : [];
    if (cookies.length) {
      for (const name of Object.keys(item.headers)) if (name.toLowerCase() === "cookie") delete item.headers[name];
      item.headers.Cookie = cookies.map(cookie => cookie.name + "=" + cookie.value).join("; ");
    }
  }
  return item;
}
async function hello() {
  const reply = await native("hello");
  hostConfig = reply.config || {};
  if (hostConfig.suggestBrowserExclusions === false) exclusionSuggestions.clear();
  const previousLanguage = QingI18n.language();
  QingI18n.setLanguage(hostConfig.language);
  if (previousLanguage !== QingI18n.language()) {
    lastNotice = "";
    await createMenus();
    const tabs = await call(api.tabs, "query", {});
    for (const tab of tabs) api.tabs.sendMessage(tab.id, { action: "language", language: QingI18n.language() }, () => void api.runtime.lastError);
  }
  await call(api.storage.local, "set", { language: QingI18n.language() });
  await call(api.storage.local, "set", { mediaMinimumSizeKb: Math.max(0, Number(hostConfig.mediaMinimumSizeKb) || 0) });
  for (const [id, records] of media) {
    for (const [url, item] of records) if (!enabled("mediaEnabled") || mediaExcluded(item)) records.delete(url);
    publishMedia(id);
  }
  return reply;
}
async function submit(inputs, tabId, showDialog = true) {
  await ready;
  await hello();
  const unique = [...new Map(inputs.filter(item => supported(item.url)).map(item => [item.url, item])).values()];
  if (!unique.length) throw new Error(t("没有可下载的链接。"));
  const items = await Promise.all(unique.map(item => downloadItem(item, requestFor(item.url, item.referer), tabId)));
  const reply = await native("add", { items, showDialog });
  if (!Number.isInteger(reply.accepted) || reply.accepted < 0 || reply.accepted > items.length)
    throw new Error(unconfirmedNotice());
  if (reply.accepted !== items.length)
    throw new Error(t("桌面端仅接收了 ") + reply.accepted + " / " + items.length + t(" 项，请在桌面端检查。"));
  notice(t("已发送 ") + reply.accepted + t(" 项到 Qing。"), tabId, false);
  return reply;
}

async function capture(download) {
  await ready;
  const url = download.finalUrl || download.url;
  if (!supported(url) || !enabled("captureEnabled") || download.byExtensionId === api.runtime.id ||
      download.paused || download.state !== "in_progress") return;
  const record = requestFor(url, download.referrer) || requestFor(download.url, download.referrer);
  if (!record) { notice(t(" 此下载仍由浏览器处理。").trim(), -1, false); return; }
  const tabId = record.tabId;
  const input = { url, fileName: (download.filename || "").split(/[\\/]/).pop(),
    referer: download.referrer || "", mime: download.mime || "", kind: "file" };
  if (excluded(input) || isBypassed(tabId) || record.bypass) return;
  let paused = false;
  let accepted = false;
  let noReturn = false;
  let suggestion;
  try {
    await hello();
    if (!enabled("captureEnabled") || excluded(input) || !captureType(input)) return;
    const item = await downloadItem(input, record, tabId);
    const [current] = await call(api.downloads, "search", { id: download.id });
    if (!current || current.state !== "in_progress" || current.paused) return;
    pending.set(download.id, false);
    await savePending();
    await call(api.downloads, "pause", download.id);
    paused = true;
    activeCaptures.add(download.id);
    pending.set(download.id, null);
    await savePending();
    const reply = await native("add", { items: [item], showDialog: false });
    if (reply.accepted === 0) suggestion = await refused(url, tabId);
    if (reply.accepted === 0 && settings.forbidBrowserReturn) {
      noReturn = true;
      pending.set(download.id, true);
      await savePending();
      await call(api.downloads, "cancel", download.id);
      paused = false; pending.delete(download.id); await savePending();
      notice(t("Qing 已取消添加；按设置，浏览器下载也已取消。"), tabId, false);
      return;
    }
    if (reply.accepted !== 1) throw Object.assign(new Error(t("桌面端未接受下载。")), { uncertain: reply.accepted !== 0 });
    accepted = true;
    pending.set(download.id, true);
    await savePending();
    await call(api.downloads, "cancel", download.id);
    paused = false;
    await call(api.downloads, "erase", { id: download.id });
    pending.delete(download.id);
    await savePending();
    refusals.delete(await refusalKey(url));
    await saveRefusals();
    for (const [id, value] of exclusionSuggestions) if (value.url === url) exclusionSuggestions.delete(id);
    notice(t("下载已交给 Qing。"), tabId, false);
    if (settings.suppressBlankTab) closeDownloadTab(record, download.url).catch(error => notice(t("下载已交给 Qing；未能清理空白页：") + error.message));
  } catch (error) {
    if (paused && noReturn) {
      notice(t("Qing 已取消添加；浏览器下载仍暂停，请在浏览器下载列表取消此下载。"), tabId);
      return;
    }
    if (paused && !accepted && error.uncertain) {
      notice(unconfirmedNotice(), tabId);
      return;
    }
    if (paused && !accepted) {
      try {
        await call(api.downloads, "resume", download.id);
        paused = false;
      } catch (resumeError) {
        notice(error.message + t(" 请在浏览器下载列表继续此下载：") + resumeError.message, tabId);
        return;
      }
    }
    if (!paused) { pending.delete(download.id); await savePending(); }
    notice(error.message + (accepted ? t(" Qing 已接收，请检查两边的下载列表。") : t(" 此下载仍由浏览器处理。")), tabId);
  } finally {
    activeCaptures.delete(download.id);
    if (suggestion && hostConfig.suggestBrowserExclusions !== false && exclusionSuggestions.has(suggestion.id)) {
      lastNotice = t("此文件已连续取消三次，可在扩展面板中添加自动捕获例外。");
      api.action.setBadgeText({ text: "!" });
      if (tabId >= 0) api.tabs.sendMessage(tabId, { action: "exclusionSuggestion", id: suggestion.id, mask: suggestion.mask }, () => void api.runtime.lastError);
    }
  }
}
api.downloads.onCreated.addListener(download => capture(download).catch(error => notice(error.message)));

api.tabs.onCreated.addListener(tab => {
  for (const [id, entry] of newTabs) if (Date.now() - entry.time > 600000) newTabs.delete(id);
  if (!tab.url || tab.url === "about:blank")
    newTabs.set(tab.id, { source: -1, window: tab.windowId, time: Date.now(), requestId: "", url: "" });
});
api.webNavigation.onCreatedNavigationTarget.addListener(details => {
  const entry = newTabs.get(details.tabId);
  if (entry) {
    entry.source = details.sourceTabId; entry.url = details.url;
    if (entry.url === "about:blank") {
      const click = linkClicks.get(details.sourceTabId);
      entry.url = click && click.frameId === details.sourceFrameId && Date.now() - click.time < 2000 ? click.url : "";
    }
    linkClicks.delete(details.sourceTabId);
  }
});
async function closeDownloadTab(record, originalUrl) {
  if (!record || record.type !== "main_frame") return;
  const entry = newTabs.get(record.tabId);
  if (!entry || entry.source < 0 || entry.url !== originalUrl || entry.requestId !== record.requestId || record.time - entry.time > 10000) return;
  const tab = await call(api.tabs, "get", record.tabId);
  if (newTabs.get(record.tabId) !== entry || (tab.openerTabId >= 0 && tab.openerTabId !== entry.source) || tab.windowId !== entry.window ||
      (tab.url && tab.url !== "about:blank") || tab.pendingUrl && tab.pendingUrl !== record.url) return;
  const frames = await call(api.scripting, "executeScript", { target: { tabId: record.tabId },
    func: () => location.href === "about:blank" && !!document.body && document.body.childElementCount === 0 && !document.body.textContent.trim() });
  if (!frames[0]?.result || newTabs.get(record.tabId) !== entry) return;
  newTabs.delete(record.tabId);
  await call(api.tabs, "remove", record.tabId);
}

function publishMedia(tabId) {
  const entries = visibleMedia(tabId);
  api.action.setBadgeText({ tabId, text: entries.length ? String(entries.length) : "" });
  api.action.setBadgeBackgroundColor({ tabId, color: "#087e8b" });
  api.tabs.get(tabId, tab => {
    if (api.runtime.lastError || !tab) return;
    api.tabs.sendMessage(tabId, { action: "mediaState", entries, ...mediaOptions(tab.url) }, () => void api.runtime.lastError);
  });
  if (boxOpen && settings.mediaMode === "box") {
    clearTimeout(boxTimers.get(tabId));
    boxTimers.set(tabId, setTimeout(() => { boxTimers.delete(tabId); updateBox(tabId, "update").catch(error => notice(error.message)); }, 350));
  }
}
function addMedia(tabId, input, frameId = 0) {
  if (tabId < 0 || !enabled("mediaEnabled") || !supported(input.url) || mediaExcluded(input)) return;
  if (!media.has(tabId)) media.set(tabId, new Map());
  const items = media.get(tabId);
  if (items.size >= 200 && !items.has(input.url)) return;
  const merged = { ...items.get(input.url), ...input, frameId };
  merged.sourcePage = input.sourcePage || items.get(input.url)?.sourcePage || "";
  merged.mime = input.mime || items.get(input.url)?.mime || "";
  merged.kind = mediaKind(input.url, merged.mime) || input.kind;
  if (JSON.stringify(items.get(input.url)) === JSON.stringify(merged)) return;
  items.set(input.url, merged);
  publishMedia(tabId);
}
function updateBox(tabId, command) {
  boxWrite = boxWrite.catch(() => {}).then(async () => {
    if (command === "update" && (!boxOpen || settings.mediaMode !== "box")) return { ok: true, visible: false };
    const entries = [];
    if (command === "show" || command === "update") {
      for (const input of visibleMedia(tabId)) {
        if (!enabled("mediaEnabled") || mediaExcluded(input)) continue;
        const item = await downloadItem(input, requestFor(input.url, input.referer), tabId);
        entries.push({ ...item, title: input.title || input.fileName || "", thumbnailUrl: input.thumbnailUrl || "",
          duration: input.duration || 0, width: input.width || 0, height: input.height || 0, sourceTitle: input.sourceTitle || hostname(input.referer || input.url) });
      }
    }
    const reply = await native("mediaBox", { command, sourceKey: boxSession + ":" + tabId, entries });
    boxOpen = reply.visible === true && settings.mediaMode === "box";
    await call(api.storage.local, "set", { boxOpen });
    return reply;
  }).catch(async error => {
    boxOpen = false; await call(api.storage.local, "set", { boxOpen }); throw error;
  });
  return boxWrite;
}
function pruneRequests() {
  for (const [id, record] of requests) {
    if (Date.now() - record.time > 120000 || requests.size > 512) requests.delete(id);
  }
}
api.webRequest.onBeforeRequest.addListener(details => {
  if (details.tabId < 0 || !["GET", "POST"].includes(details.method)) return;
  pruneRequests();
  requests.set(details.requestId, { url: details.url, method: details.method,
    type: details.type, requestId: details.requestId,
    tabId: details.tabId, frameId: details.frameId, sourcePage: details.documentUrl || "", headers: {}, time: Date.now(),
    formData: details.requestBody && details.requestBody.formData, bypass: isBypassed(details.tabId) });
  if (details.type === "main_frame" && newTabs.has(details.tabId)) {
    const entry = newTabs.get(details.tabId);
    if (entry.requestId && entry.requestId !== details.requestId) newTabs.delete(details.tabId);
    else entry.requestId = details.requestId;
  }
}, { urls: ["http://*/*", "https://*/*"] }, ["requestBody"]);

function beforeHeaders(details) {
  const record = requests.get(details.requestId);
  if (!record) return;
  record.url = details.url;
  record.headers = cleanHeaders(Object.fromEntries((details.requestHeaders || [])
    .filter(item => typeof item.value === "string").map(item => [item.name, item.value])));
}
const requestFilter = { urls: ["http://*/*", "https://*/*"] };
try {
  api.webRequest.onBeforeSendHeaders.addListener(beforeHeaders, requestFilter, ["requestHeaders", "extraHeaders"]);
} catch {
  api.webRequest.onBeforeSendHeaders.addListener(beforeHeaders, requestFilter, ["requestHeaders"]);
}
api.webRequest.onHeadersReceived.addListener(details => {
  const record = requests.get(details.requestId);
  if (!record || details.statusCode < 200 || details.statusCode >= 300) return;
  record.mime = (details.responseHeaders || []).find(item => item.name.toLowerCase() === "content-type")?.value || "";
  const responseHeader = name => (details.responseHeaders || []).find(item => item.name.toLowerCase() === name)?.value;
  const range = /^bytes\s+\d+-\d+\/(\d+)$/i.exec(responseHeader("content-range") || "");
  const length = details.statusCode === 206 ? range?.[1] : responseHeader("content-length");
  const totalBytes = /^\d+$/.test(length || "") ? Number(length) : null;
  const kind = mediaKind(details.url, record.mime);
  if (kind && !record.bypass) addMedia(details.tabId, { url: details.url, kind,
    mime: record.mime, totalBytes, sourcePage: record.sourcePage, referer: header(record.headers, "referer"), fileName: "" }, details.frameId);
}, requestFilter, ["responseHeaders"]);

function clearTab(tabId, frameId) {
  mediaFormats.delete(tabId);
  if (!frameId && boxOpen) updateBox(tabId, "clear").catch(error => notice(error.message));
  if (!frameId) { media.delete(tabId); bypass.delete(tabId); linkClicks.delete(tabId); }
  else for (const [url, item] of media.get(tabId) || []) {
    if (item.frameId === frameId) media.get(tabId).delete(url);
  }
  for (const [id, record] of requests) {
    if (record.tabId === tabId && (!frameId || record.frameId === frameId)) requests.delete(id);
  }
}
api.tabs.onRemoved.addListener(tabId => { newTabs.delete(tabId); ready.then(() => clearTab(tabId)); });
api.webNavigation.onCommitted.addListener(details => {
  if (!details.frameId && (details.url !== "about:blank" || newTabs.get(details.tabId)?.requestId)) newTabs.delete(details.tabId);
  ready.then(() => { clearTab(details.tabId, details.frameId); publishMedia(details.tabId); });
});
api.webNavigation.onHistoryStateUpdated.addListener(details => {
  ready.then(() => {
    clearTab(details.tabId, details.frameId); publishMedia(details.tabId);
    api.tabs.sendMessage(details.tabId, { action: "scan" }, () => void api.runtime.lastError);
  });
});

function collectLinks(selected) {
  const selection = window.getSelection();
  const links = new Map();
  const add = (url, fileName = "", linkText = "") => {
    if ((/^(https?|ftps?):\/\//i.test(url || "") || /^magnet:\?/i.test(url || "")) && (!links.has(url) || linkText))
      links.set(url, { url, fileName, linkText, referer: location.href, sourcePage: location.href });
  };
  for (const element of document.querySelectorAll("a[href],area[href],img[src],video[src],audio[src],source[src],track[src]")) {
    if (!selected || (selection && selection.containsNode(element, true)))
      add(element.href || element.src, element.download || "", (element.textContent || element.title || "").trim());
  }
  const text = selected ? String(selection || "") : document.body.innerText;
  for (const match of text.matchAll(/(?:https?|ftps?):\/\/[^\s<>"']+|magnet:\?[^\s<>"']+/g)) add(match[0]);
  return [...links.values()];
}
async function collect(tabId, selected) {
  const frames = await call(api.scripting, "executeScript", {
    target: { tabId, allFrames: true }, func: collectLinks, args: [selected]
  });
  return frames.flatMap(frame => frame.result || []);
}
async function createMenus() {
  await call(api.contextMenus, "removeAll");
  api.contextMenus.create({ id: "qing", title: t("Qing 下载"), contexts: ["all"] });
  api.contextMenus.create({ id: "qing-link", parentId: "qing", title: t("下载此链接"), contexts: ["link"] });
  api.contextMenus.create({ id: "qing-selection", parentId: "qing", title: t("下载选中内容中的链接"), contexts: ["selection"] });
  api.contextMenus.create({ id: "qing-all", parentId: "qing", title: t("下载页面中的全部链接"), contexts: ["all"] });
}
api.runtime.onInstalled.addListener(() => ready.then(createMenus).catch(error => notice(error.message)));
api.contextMenus.onClicked.addListener((info, tab) => {
  const items = info.menuItemId === "qing-link"
    ? Promise.resolve([{ url: info.linkUrl, referer: info.frameUrl || info.pageUrl, sourcePage: info.frameUrl || info.pageUrl }])
    : collect(tab.id, info.menuItemId === "qing-selection");
  items.then(result => submit(result, tab.id)).catch(error => notice(error.message, tab.id));
});

async function message(request, sender) {
  await ready;
  const fromPage = sender.tab && !(sender.url || "").startsWith(api.runtime.getURL(""));
  const tabId = fromPage ? sender.tab.id : request.tabId;
  if (fromPage) {
    if (request.action === "keys") {
      bypass.set(tabId, { alt: !!request.alt, ctrl: !!request.ctrl, shift: !!request.shift, time: Date.now() });
      if (supported(request.linkUrl)) linkClicks.set(tabId, { url: request.linkUrl, frameId: sender.frameId || 0, time: Date.now() });
      else if (typeof request.linkUrl === "string") linkClicks.delete(tabId);
      return { ok: true };
    }
    if (request.action === "discovered") {
      for (const item of request.items || []) addMedia(tabId, { ...item,
        sourcePage: sender.url || "", referer: item.referer || sender.url || sender.tab.url }, item.frameId ?? sender.frameId ?? 0);
      return { ok: true, frameId: sender.frameId || 0, ...mediaOptions(sender.url || sender.tab.url) };
    }
    if (!["mediaList", "add", "exclusionDecision", "mediaFormats", "addFormat"].includes(request.action)) throw new Error(t("不支持此页面请求。"));
  }
  if (request.action === "state") {
    try {
      const snapshot = await call(api.tabs, "sendMessage", tabId, { action: "snapshot" });
      for (const item of snapshot.entries || []) addMedia(tabId, item, item.frameId || 0);
    } catch { /* Some browser pages do not allow content scripts. */ }
    let connection;
    const unconfirmed = [];
    for (const [id, accepted] of pending) {
      if (accepted !== null || activeCaptures.has(id)) continue;
      const [download] = await call(api.downloads, "search", { id });
      if (download && download.state === "in_progress" && download.paused)
        unconfirmed.push({ id, fileName: (download.filename || "").split(/[\\/]/).pop() });
    }
    if (unconfirmed.length) connection = { ok: false, error: unconfirmedNotice() };
    else try { connection = await hello(); } catch (error) { connection = { ok: false, error: error.message }; }
    return { settings, connection, notice: lastNotice, boxOpen, language: QingI18n.language(), mediaMinimumSizeKb: Math.max(0, Number(hostConfig.mediaMinimumSizeKb) || 0),
      exclusionSuggestions: [...exclusionSuggestions.values()].map(value => ({ id: value.id, mask: value.mask })),
      unconfirmed, entries: visibleMedia(tabId) };
  }
  if (request.action === "exclusionDecision") {
    const suggestion = exclusionSuggestions.get(request.id);
    if (!suggestion || (fromPage && suggestion.tabId !== tabId)) throw new Error(t("此建议已失效，请刷新面板。"));
    const mask = String(request.mask || "").trim();
    if (request.accept && !/^(https?|ftps?):\/\/[^\s]+$/i.test(mask)) throw new Error(t("请填写完整链接规则；星号可匹配任意字符。"));
    if (request.dontSuggest) {
      await native("disableExclusionSuggestions");
      hostConfig.suggestBrowserExclusions = false;
      exclusionSuggestions.clear();
    }
    if (request.accept) {
      if (!settings.excludedUrls.includes(mask)) settings.excludedUrls.push(mask);
      await call(api.storage.local, "set", { settings });
    }
    exclusionSuggestions.delete(request.id);
    refusals.delete(await refusalKey(suggestion.url));
    await saveRefusals();
    notice(request.accept ? t("自动捕获例外已保存，可在捕获设置中删除。") : t("未添加例外。"), tabId, false);
    return { ok: true };
  }
  if (request.action === "resolveCapture") {
    if (!pending.has(request.id) || pending.get(request.id) !== null || activeCaptures.has(request.id) || !["resume", "cancel"].includes(request.decision))
      throw new Error(t("下载状态已变化，请刷新后重试。"));
    await call(api.downloads, request.decision, request.id);
    pending.delete(request.id);
    await savePending();
    lastNotice = "";
    return { ok: true };
  }
  if (request.action === "settings") {
    const value = request.value;
    const previousMode = settings.mediaMode;
    settings = { captureEnabled: !!value.captureEnabled, mediaEnabled: !!value.mediaEnabled,
      suppressBlankTab: !!value.suppressBlankTab, forbidBrowserReturn: !!value.forbidBrowserReturn,
      captureSubtitles: value.captureSubtitles !== false, offerSubtitles: value.offerSubtitles !== false, showExpandedStreams: !!value.showExpandedStreams,
      useCredentials: !!value.useCredentials, showButtons: !!value.showButtons,
      mediaMode: value.mediaMode === "box" ? "box" : "button",
      buttonSize: ["standard", "medium", "mini"].includes(value.buttonSize) ? value.buttonSize : "standard",
      buttonOpacity: Math.max(0, Math.min(100, Number(value.buttonOpacity ?? 100))), hideFullscreen: !!value.hideFullscreen,
      bypassKey: ["alt", "ctrl", "shift"].includes(value.bypassKey) ? value.bypassKey : "alt",
      excludedHosts: value.excludedHosts, mediaExcludedHosts: value.mediaExcludedHosts || [], excludedExtensions: value.excludedExtensions,
      excludedUrls: value.excludedUrls || [] };
    if (!settings.useCredentials)
      for (const record of requests.values()) record.headers = cleanHeaders(record.headers);
    await call(api.storage.local, "set", { settings });
    for (const [id, records] of media) {
      for (const [url, item] of records) if (!enabled("mediaEnabled") || mediaExcluded(item)) records.delete(url);
      publishMedia(id);
    }
    const tabs = await call(api.tabs, "query", {});
    for (const tab of tabs)
      api.tabs.sendMessage(tab.id, { action: "settings", ...mediaOptions(tab.url) }, () => void api.runtime.lastError);
    if (settings.mediaMode !== previousMode) {
      if (settings.mediaMode === "box") await updateBox(tabId, "show");
      else if (boxOpen) await updateBox(tabId, "close");
    }
    return { ok: true };
  }
  if (request.action === "openBox") {
    if (settings.mediaMode !== "box") throw new Error(t("请先保存媒体窗口模式。"));
    await hello(); return updateBox(tabId, "show");
  }
  if (request.action === "mediaList")
    return { ok: true, entries: visibleMedia(tabId),
      ...mediaOptions(sender.url) };
  if (request.action === "mediaFormats" || request.action === "addFormat") {
    if (!settings.showExpandedStreams) throw new Error(t("请先在捕获设置中启用其他媒体流选项。"));
    const input = visibleMedia(tabId).find(item => item.url === request.url && item.kind !== "subtitle");
    if (!input) throw new Error(t("媒体记录已失效，请刷新页面。"));
    await hello();
    const item = await downloadItem(input, requestFor(input.url, input.referer), tabId);
    if (item.method !== "GET") throw new Error(t("此媒体由 POST 请求提供，无法查询其他媒体流；可使用原下载入口。"));
    item.kind = "media";
    if (request.action === "mediaFormats") {
      const reply = await native("mediaFormats", { item });
      if (!Array.isArray(reply.formats)) throw new Error(t("桌面端未返回媒体流列表。"));
      reply.formats = reply.formats.filter(format => !(format.size > 0) || format.size >= Math.max(0, Number(hostConfig.mediaMinimumSizeKb) || 0) * 1024);
      if (!media.get(tabId)?.has(input.url)) throw new Error(t("页面已变化，请重新查询媒体流。"));
      if (!mediaFormats.has(tabId)) mediaFormats.set(tabId, new Map());
      mediaFormats.get(tabId).set(input.url, reply.formats.map(format => String(format.id)));
      return reply;
    }
    if (!mediaFormats.get(tabId)?.get(input.url)?.includes(request.formatId)) throw new Error(t("媒体流列表已失效，请重新查询。"));
    item.mediaFormat = request.formatId;
    const reply = await native("add", { items: [item], showDialog: true });
    if (reply.accepted !== 1) throw new Error(reply.accepted === 0 ? t("未添加下载。") : unconfirmedNotice());
    return reply;
  }
  if (request.action === "add") {
    if (fromPage) {
      const known = media.get(tabId) || new Map();
      if (!(request.items || []).every(item => known.has(item.url)))
        throw new Error(t("媒体记录已失效，请刷新页面后再试。"));
    }
    return submit(request.items, tabId, true);
  }
  if (request.action === "collect") return submit(await collect(tabId, request.selected), tabId, true);
  throw new Error(t("不支持的操作。"));
}
api.runtime.onMessage.addListener((request, sender, reply) => {
  if (sender.id !== api.runtime.id) return false;
  message(request, sender).then(reply, error => reply({ ok: false, error: error.message }));
  return true;
});
api.permissions.onRemoved.addListener(removed => {
  if ((removed.data_collection || []).includes("authenticationInfo")) {
    settings.useCredentials = false;
    for (const record of requests.values()) record.headers = cleanHeaders(record.headers);
    call(api.storage.local, "set", { settings }).catch(error => notice(error.message));
  }
});


