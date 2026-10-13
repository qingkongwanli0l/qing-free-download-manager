/* SPDX-License-Identifier: MIT */
(() => {
  "use strict";
  const t = QingI18n.text;
  if (window.__qingDownloads) return;
  window.__qingDownloads = true;
  let enabled = true;
  let showButtons = true;
  let showExpandedStreams = false;
  let buttonSize = "standard", buttonOpacity = 100, hideFullscreen = true;
  let entries = [];
  let frameId = 0;
  let scanTimer;
  let layoutFrame;
  const root = document.createElement("div");
  const shadow = root.attachShadow({ mode: "closed" });
  document.documentElement.append(root);
  const style = document.createElement("style");
  style.textContent = ":host{all:initial}button{font:13px system-ui;cursor:pointer;border:1px solid #9cc5c9;border-radius:5px;padding:6px 10px;background:#fff;color:#10343a}button:hover{background:#e8f7f7}.qing-button{position:fixed;z-index:2147483646;background:#087e8b;color:white;border:0}.qing-button:hover{background:#076a75}.panel{position:fixed;z-index:2147483647;right:16px;top:16px;width:320px;max-height:60vh;overflow:auto;background:white;color:#10343a;border:1px solid #aacbcc;border-radius:8px;padding:12px;box-shadow:0 6px 24px #0003;font:13px system-ui}.panel button{display:block;max-width:100%;margin:6px 0;overflow-wrap:anywhere;text-align:left}.notice{white-space:pre-wrap;margin:8px 0}";
  shadow.append(style);
  const buttons = document.createElement("div");
  shadow.append(buttons);
  const panel = document.createElement("div");
  panel.className = "panel";
  panel.hidden = true;
  shadow.append(panel);

  function send(message) {
    return new Promise((resolve, reject) => chrome.runtime.sendMessage(message, response => {
      const error = chrome.runtime.lastError;
      if (error) reject(new Error(t("扩展已更新或暂不可用，请刷新页面。")));
      else if (response && response.ok === false) reject(new Error(response.error));
      else resolve(response);
    }));
  }
  function closeButton() {
    const button = document.createElement("button");
    button.textContent = t("关闭");
    button.addEventListener("click", () => { panel.hidden = true; });
    panel.append(button);
  }
  function notice(text) {
    panel.replaceChildren();
    const message = document.createElement("p");
    message.className = "notice";
    message.textContent = text;
    panel.append(message);
    closeButton();
    panel.hidden = false;
  }
  function suggestExclusion(suggestion) {
    panel.replaceChildren();
    const label = document.createElement("label");
    label.textContent = t("此文件已取消三次。是否停止自动捕获以下链接？");
    const input = document.createElement("input");
    input.type = "text"; input.value = suggestion.mask; input.style.width = "100%";
    label.append(input); panel.append(label);
    const hint = document.createElement("p");
    hint.textContent = t("可修改规则，星号匹配任意字符。请勿填写密码或登录令牌。");
    panel.append(hint);
    const preference = document.createElement("label");
    preference.style.display = "block";
    const dontSuggest = document.createElement("input"); dontSuggest.type = "checkbox";
    preference.append(dontSuggest, document.createTextNode(t("以后不再建议添加例外（可在桌面设置的提示页恢复）"))); panel.append(preference);
    for (const [accept, text] of [[true, t("添加例外")], [false, t("继续询问")]]) {
      const button = document.createElement("button"); button.textContent = text;
      button.addEventListener("click", async () => {
        try {
          await send({ action: "exclusionDecision", id: suggestion.id, accept, mask: input.value, dontSuggest: dontSuggest.checked });
          notice(accept ? t("自动捕获例外已保存，可在扩展的捕获设置中删除。") : t("未添加例外。"));
        } catch (error) { notice(error.message); }
      });
      panel.append(button);
    }
    closeButton(); panel.hidden = false;
  }
  async function choose(element) {
    try {
      const result = await send({ action: "mediaList" });
      entries = result.entries || [];
      panel.replaceChildren();
      const title = document.createElement("strong");
      title.textContent = t("选择要下载的媒体");
      panel.append(title);
      const exact = element ? entries.filter(item => item.url === element.currentSrc || item.url === element.src) : [];
      const choices = (exact.length ? [...exact, ...entries.filter(item => item.kind === "subtitle" && !exact.includes(item))] : [...entries])
        .sort((a, b) => Number(a.kind === "subtitle") - Number(b.kind === "subtitle"));
      if (!choices.length) {
        notice(t("尚未发现可下载的媒体。请先开始播放，再试一次。"));
        return;
      }
      let subtitles = false;
      for (const item of choices) {
        if (item.kind === "subtitle" && !subtitles) {
          const heading = document.createElement("strong"); heading.textContent = t("此页面发现的字幕"); panel.append(heading); subtitles = true;
        }
        const button = document.createElement("button");
        button.textContent = QingI18n.kind(item.kind) + " · " +
          (item.title || item.fileName || new URL(item.url).pathname.split("/").pop() || hostname(item.url));
        button.addEventListener("click", async () => {
          button.disabled = true;
          try {
            const reply = await send({ action: "add", items: [item] });
            notice(t("已发送 ") + reply.accepted + t(" 项到 Qing。"));
          } catch (error) { notice(error.message); }
        });
        panel.append(button);
        if (showExpandedStreams && item.kind !== "subtitle") {
          const more = document.createElement("button"); more.textContent = t("其他媒体流");
          more.addEventListener("click", async () => {
            more.disabled = true; more.textContent = t("正在查询…");
            try {
              const result = await send({ action: "mediaFormats", url: item.url });
              if (!result.formats.length) { more.textContent = t("未发现其他媒体流"); return; }
              const select = document.createElement("select"); select.setAttribute("aria-label", t("媒体流格式")); select.style.maxWidth = "100%";
              for (const format of result.formats) {
                const option = document.createElement("option"); option.value = String(format.id);
                option.textContent = [format.id, format.resolution, format.extension, format.videoCodec, format.audioCodec, format.language,
                  format.size > 0 ? Math.ceil(format.size / 1024) + " KiB" : ""].filter(Boolean).join(" · ");
                select.append(option);
              }
              const download = document.createElement("button"); download.textContent = t("下载所选媒体流");
              download.addEventListener("click", async () => {
                download.disabled = true;
                try { const result = await send({ action: "addFormat", url: item.url, formatId: select.value }); notice(t("已发送 ") + result.accepted + t(" 项到 Qing。")); }
                catch (error) { notice(error.message); }
              });
              more.replaceWith(select, download);
            } catch (error) { notice(error.message); }
          });
          panel.append(more);
        }
      }
      closeButton();
      panel.hidden = false;
    } catch (error) { notice(error.message); }
  }
  function hostname(url) { return new URL(url).hostname; }
  function layout() {
    cancelAnimationFrame(layoutFrame);
    layoutFrame = requestAnimationFrame(() => {
      buttons.replaceChildren();
      if (!enabled || !showButtons || (hideFullscreen && document.fullscreenElement)) return;
      const parent = document.fullscreenElement || document.documentElement;
      if (root.parentElement !== parent) parent.append(root);
      const players = [...document.querySelectorAll("video,audio")];
      if (!players.length && entries.some(item => item.frameId === frameId)) players.push(null);
      for (const element of players) {
        const rect = element ? element.getBoundingClientRect() : { width: 100, height: 30, left: innerWidth - 116, top: 8, bottom: 38 };
        if (rect.width < 60 || rect.height < 20 || rect.bottom < 0 || rect.top > innerHeight) continue;
        const button = document.createElement("button");
        button.className = "qing-button";
        button.textContent = buttonSize === "mini" ? "↓" : t("Qing 下载");
        button.title = t("使用 Qing 下载媒体");
        button.style.padding = buttonSize === "mini" ? "3px 7px" : buttonSize === "medium" ? "4px 8px" : "6px 10px";
        button.style.fontSize = buttonSize === "standard" ? "13px" : "11px";
        button.style.opacity = buttonOpacity / 100;
        button.style.left = Math.max(0, Math.min(innerWidth - 100, rect.left + 8)) + "px";
        button.style.top = Math.max(0, rect.top + 8) + "px";
        button.addEventListener("click", () => choose(element));
        buttons.append(button);
      }
    });
  }
  async function scan() {
    const items = [];
    for (const element of document.querySelectorAll("video,audio,source,track")) {
      const url = element.currentSrc || element.src;
      if (!/^https?:\/\//i.test(url || "")) continue;
      const kind = element.tagName === "TRACK" ? "subtitle" :
        (element.tagName === "AUDIO" || element.parentElement?.tagName === "AUDIO" ? "audio" : "video");
      const player = /^(VIDEO|AUDIO)$/.test(element.tagName) ? element : element.parentElement;
      items.push({ url, kind, mime: element.type || "", fileName: "", referer: location.href,
        title: element.title || (element.tagName === "TRACK" ? element.label || element.srclang : "") || player?.title || document.title, sourceTitle: document.title,
        thumbnailUrl: player?.poster || "", duration: Number.isFinite(player?.duration) ? player.duration : 0,
        width: player?.videoWidth || 0, height: player?.videoHeight || 0 });
    }
    try {
      const state = await send({ action: "discovered", items });
      frameId = state.frameId;
      configure(state);
      layout();
    } catch {
      enabled = false;
      layout();
    }
  }
  function configure(value) {
    QingI18n.setLanguage(value.language);
    enabled = value.enabled; showButtons = value.showButtons;
    buttonSize = value.buttonSize || "standard"; buttonOpacity = value.buttonOpacity ?? 100;
    hideFullscreen = value.hideFullscreen !== false;
    showExpandedStreams = !!value.showExpandedStreams;
  }
  function scheduleScan() {
    clearTimeout(scanTimer);
    scanTimer = setTimeout(scan, 200);
  }
  function keys(event) {
    const navigationInput = event.isTrusted && (event.type === "pointerdown" || event.type === "keydown" && event.key === "Enter");
    const link = navigationInput && event.target instanceof Element ? event.target.closest("a[href]") : null;
    send({ action: "keys", alt: event.altKey, ctrl: event.ctrlKey, shift: event.shiftKey,
      linkUrl: navigationInput ? link?.href || "" : undefined }).catch(() => {});
  }
  window.addEventListener("keydown", keys, true);
  window.addEventListener("keyup", keys, true);
  window.addEventListener("pointerdown", keys, true);
  window.addEventListener("blur", () => keys({}), true);
  window.addEventListener("scroll", layout, true);
  window.addEventListener("resize", layout);
  document.addEventListener("loadedmetadata", scheduleScan, true);
  document.addEventListener("play", scheduleScan, true);
  document.addEventListener("fullscreenchange", layout);
  new MutationObserver(scheduleScan).observe(document.documentElement, {
    subtree: true, childList: true, attributes: true, attributeFilter: ["src", "poster", "title"]
  });
  chrome.runtime.onMessage.addListener((message, sender, reply) => {
    if (message.action === "language") { QingI18n.setLanguage(message.language); panel.hidden = true; layout(); }
    if (message.action === "snapshot") {
      reply({ entries });
      scheduleScan();
    }
    if (message.action === "notice") notice(message.text);
    if (message.action === "exclusionSuggestion") suggestExclusion(message);
    if (message.action === "scan") scheduleScan();
    if (message.action === "mediaState" || message.action === "settings") {
      if (message.entries) entries = message.entries;
      configure(message);
      layout();
      if (message.action === "settings") scheduleScan();
    }
  });
  scan();
})();
