/* SPDX-License-Identifier: MIT */
"use strict";
const t = QingI18n.text;
QingI18n.localize();
const $ = id => document.getElementById(id);
let tabId;
let entries = [];
let builtInConsent = false;
let expandedStreams = false;
chrome.permissions.getAll(value => { builtInConsent = !!value.data_collection; });
function checkSiteAccess() {
  chrome.permissions.contains({ origins: ["<all_urls>"] }, granted => {
    const error = chrome.runtime.lastError;
    $("site-access").hidden = granted && !error;
    $("site-access-explanation").hidden = false;
    $("grant-site-access").hidden = false;
    $("site-access-status").textContent = error ? t("无法检查网站访问权限：") + error.message : "";
  });
}
$("grant-site-access").addEventListener("click", () => {
  $("grant-site-access").disabled = true;
  chrome.permissions.request({ origins: ["<all_urls>"] }, granted => {
    const error = chrome.runtime.lastError;
    $("grant-site-access").disabled = false;
    if (error) $("site-access-status").textContent = t("网站访问授权失败：") + error.message;
    else if (!granted) $("site-access-status").textContent = t("尚未授予网站访问权限。你可以稍后再试；页面媒体发现暂不可用。");
    else {
      $("site-access-explanation").hidden = true;
      $("grant-site-access").hidden = true;
      $("site-access-status").textContent = t("已允许网站访问。请刷新已经打开的网页，再使用媒体发现和下载接管。");
    }
  });
});
checkSiteAccess();
function send(message) {
  return new Promise((resolve, reject) => chrome.runtime.sendMessage({ ...message, tabId }, response => {
    const error = chrome.runtime.lastError;
    if (error) reject(new Error(t("扩展暂不可用，请重新打开此面板。")));
    else if (!response || response.ok === false) reject(new Error(response?.error || t("操作未完成。")));
    else resolve(response);
  }));
}
function status(text, error = false) {
  $("status").hidden = false;
  $("status").textContent = text;
  $("status").className = error ? "error" : "";
}
function render() {
  $("media").replaceChildren();
  $("empty").hidden = entries.length > 0;
  $("count").textContent = entries.length + t(" 项");
  const ordered = entries.map((item, index) => ({ item, index })).sort((a, b) => Number(a.item.kind === "subtitle") - Number(b.item.kind === "subtitle"));
  let subtitles = false;
  for (const { item, index } of ordered) {
    if (item.kind === "subtitle" && !subtitles) {
      const title = document.createElement("li"); title.textContent = t("此页面发现的字幕"); $("media").append(title); subtitles = true;
    }
    const row = document.createElement("li");
    const label = document.createElement("label");
    const check = document.createElement("input");
    check.type = "checkbox";
    check.value = index;
    check.addEventListener("change", () => {
      $("send-selected").disabled = !$("media").querySelector("input:checked");
    });
    const text = document.createElement("span");
    const url = new URL(item.url);
    text.textContent = item.title || item.fileName || url.pathname.split("/").pop() || url.hostname;
    const detail = document.createElement("small");
    detail.textContent = QingI18n.kind(item.kind) + " · " + url.hostname +
      (["video", "audio"].includes(item.kind) ? " · " + (Number.isFinite(item.totalBytes) ? Math.ceil(item.totalBytes / 1024) + " KiB" : t("大小未知")) : "");
    text.append(detail);
    label.append(check, text);
    row.append(label);
    if (expandedStreams && item.kind !== "subtitle") {
      const button = document.createElement("button"); button.textContent = t("其他媒体流");
      button.addEventListener("click", async () => {
        button.disabled = true; button.textContent = t("正在查询…");
        try {
          const result = await send({ action: "mediaFormats", url: item.url });
          if (!result.formats.length) { button.textContent = t("未发现其他媒体流"); return; }
          const select = document.createElement("select"); select.setAttribute("aria-label", t("媒体流格式"));
          for (const format of result.formats) {
            const option = document.createElement("option"); option.value = String(format.id);
            option.textContent = [format.id, format.resolution, format.extension, format.videoCodec, format.audioCodec, format.language,
              format.size > 0 ? Math.ceil(format.size / 1024) + " KiB" : ""].filter(Boolean).join(" · ");
            select.append(option);
          }
          const download = document.createElement("button"); download.textContent = t("下载所选媒体流");
          download.addEventListener("click", event => action({ action: "addFormat", url: item.url, formatId: select.value }, event.target));
          button.replaceWith(select, download);
        } catch (error) { status(error.message, true); button.disabled = false; button.textContent = t("其他媒体流"); }
      });
      row.append(button);
    }
    $("media").append(row);
  }
  $("send-selected").disabled = true;
}
async function refresh() {
  $("connection").textContent = t("正在连接桌面端…");
  try {
    const state = await send({ action: "state" });
    QingI18n.setLanguage(state.language); QingI18n.localize();
    const connected = state.connection.ok;
    $("connection").textContent = connected ? t("桌面端已连接 · ") + (state.connection.version || "") : state.connection.error;
    if (connected && state.connection.config?.captureEnabled === false)
      $("connection").textContent += t(" · 桌面端已关闭自动接管");
    $("connection").className = connected ? "" : "error";
    $("unconfirmed").replaceChildren();
    $("unconfirmed").hidden = !state.unconfirmed?.length;
    if (state.unconfirmed?.length) {
      const hint = document.createElement("p");
      hint.textContent = t("如 Qing 仍显示添加或重复文件确认窗口，请先取消该窗口，再继续浏览器下载。");
      $("unconfirmed").append(hint);
    }
    for (const download of state.unconfirmed || []) {
      const name = document.createElement("p");
      name.textContent = download.fileName || t("下载 ") + download.id;
      $("unconfirmed").append(name);
      for (const [decision, text] of [["resume", t("已核对，继续浏览器下载")], ["cancel", t("Qing 已接收，取消浏览器下载")]]) {
        const button = document.createElement("button");
        button.textContent = text;
        button.addEventListener("click", async () => {
          button.disabled = true;
          try { await send({ action: "resolveCapture", id: download.id, decision }); await refresh(); }
          catch (error) { status(error.message, true); button.disabled = false; }
        });
        $("unconfirmed").append(button);
      }
    }
    $("exclusionSuggestions").replaceChildren();
    $("exclusionSuggestions").hidden = !state.exclusionSuggestions?.length;
    for (const suggestion of state.exclusionSuggestions || []) {
      const label = document.createElement("label");
      label.className = "field";
      label.textContent = t("此文件已取消三次。是否停止自动捕获以下链接？");
      const input = document.createElement("input");
      input.type = "text"; input.value = suggestion.mask;
      label.append(input); $("exclusionSuggestions").append(label);
      const hint = document.createElement("p");
      hint.className = "hint"; hint.textContent = t("可修改规则，星号匹配任意字符。请勿填写密码或登录令牌。");
      $("exclusionSuggestions").append(hint);
      const preference = document.createElement("label");
      preference.style.display = "block"; preference.style.marginBottom = "8px";
      const dontSuggest = document.createElement("input"); dontSuggest.type = "checkbox";
      preference.append(dontSuggest, document.createTextNode(t("以后不再建议添加例外（可在桌面设置的提示页恢复）"))); $("exclusionSuggestions").append(preference);
      for (const [accept, text] of [[true, t("添加例外")], [false, t("继续询问")]]) {
        const button = document.createElement("button"); button.textContent = text;
        button.addEventListener("click", async () => {
          try { await send({ action: "exclusionDecision", id: suggestion.id, accept, mask: input.value, dontSuggest: dontSuggest.checked }); await refresh(); }
          catch (error) { status(error.message, true); }
        });
        $("exclusionSuggestions").append(button);
      }
    }
    for (const key of ["captureEnabled", "mediaEnabled", "showButtons", "useCredentials", "hideFullscreen", "suppressBlankTab", "forbidBrowserReturn", "captureSubtitles", "offerSubtitles", "showExpandedStreams"]) $(key).checked = state.settings[key];
    $("media-size").textContent = t("桌面端媒体大小下限：") + (state.mediaMinimumSizeKb || 0) + t(" KiB。未知大小的媒体仍会显示，流媒体清单不按清单自身大小过滤。更改桌面设置后请刷新此面板。");
    for (const key of ["mediaMode", "buttonSize", "buttonOpacity"]) $(key).value = state.settings[key];
    $("open-box").hidden = state.settings.mediaMode !== "box";
    $("bypassKey").value = state.settings.bypassKey;
    $("excludedHosts").value = state.settings.excludedHosts.join("\n");
    $("mediaExcludedHosts").value = state.settings.mediaExcludedHosts.join("\n");
    $("excludedExtensions").value = state.settings.excludedExtensions.join(", ");
    $("excludedUrls").value = state.settings.excludedUrls.join("\n");
    entries = state.entries;
    expandedStreams = state.settings.showExpandedStreams;
    render();
    $("status").hidden = true;
    if (state.notice && state.notice !== state.connection.error) status(state.notice);
  } catch (error) { status(error.message, true); }
}
async function action(message, button) {
  button.disabled = true;
  try {
    const result = await send(message);
    status(t("已发送 ") + result.accepted + t(" 项到 Qing。"));
  } catch (error) { status(error.message, true); }
  finally { button.disabled = false; }
}
$("refresh").addEventListener("click", () => { checkSiteAccess(); refresh(); });
$("open-box").addEventListener("click", async () => {
  try { await send({ action: "openBox" }); status(t("媒体窗口已打开。")); }
  catch (error) { status(error.message, true); }
});
$("send-selected").addEventListener("click", event => {
  const items = [...$("media").querySelectorAll("input:checked")].map(input => entries[Number(input.value)]);
  action({ action: "add", items }, event.target);
});
$("selected-links").addEventListener("click", event => action({ action: "collect", selected: true }, event.target));
$("all-links").addEventListener("click", event => action({ action: "collect", selected: false }, event.target));
$("settings").addEventListener("submit", async event => {
  event.preventDefault();
  const value = {};
  for (const key of ["captureEnabled", "mediaEnabled", "showButtons", "useCredentials", "hideFullscreen", "suppressBlankTab", "forbidBrowserReturn", "captureSubtitles", "offerSubtitles", "showExpandedStreams"]) value[key] = $(key).checked;
  for (const key of ["mediaMode", "buttonSize", "buttonOpacity"]) value[key] = $(key).value;
  value.bypassKey = $("bypassKey").value;
  for (const key of ["excludedHosts", "mediaExcludedHosts"]) {
    value[key] = $(key).value.split(/[\s,]+/).map(text => text.toLowerCase().trim()).filter(Boolean);
    if (value[key].some(host => !/^(\*\.)?[a-z0-9.-]+$/.test(host))) {
      status(t("网站规则请填写域名，不要包含协议或路径。"), true);
      return;
    }
  }
  value.excludedExtensions = $("excludedExtensions").value.split(/[\s,]+/).map(text => text.toLowerCase().replace(/^\./, "")).filter(Boolean);
  value.excludedUrls = $("excludedUrls").value.split(/\r?\n/).map(text => text.trim()).filter(Boolean);
  if (value.excludedUrls.some(mask => !/^(https?|ftps?):\/\/[^\s]+$/i.test(mask))) {
    status(t("链接规则请填写完整链接；星号可匹配任意字符。"), true); return;
  }
  if (value.useCredentials && builtInConsent) {
    const granted = await new Promise(resolve => chrome.permissions.request({ data_collection: ["authenticationInfo"] }, resolve));
    if (!granted) { status(t("未允许使用认证信息，设置未保存。"), true); return; }
  }
  try { await send({ action: "settings", value }); await refresh(); status(t("设置已保存。")); }
  catch (error) { status(error.message, true); }
});
chrome.tabs.query({ active: true, currentWindow: true }, tabs => {
  tabId = tabs[0]?.id;
  refresh();
});


