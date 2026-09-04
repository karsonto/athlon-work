/** @typedef {import('./timeline-store.js').TimelineItem} TimelineItem */

export function t(key) {
  return (window.__chatI18n && window.__chatI18n[key]) || key;
}

export function cssEscape(value) {
  if (window.CSS && typeof CSS.escape === 'function') return CSS.escape(String(value));
  return String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

export function decodeBase64Utf8(b64) {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new TextDecoder('utf-8').decode(bytes);
}

export function resolveEventMarkdown(event) {
  if (event && event.markdownB64) return decodeBase64Utf8(event.markdownB64);
  if (event && event.markdown) return event.markdown;
  if (event && event.content) return event.content;
  return '';
}

export function resolveEventHtml(event) {
  if (event && event.htmlB64) return decodeBase64Utf8(event.htmlB64);
  return (event && event.html) || '';
}

export function resolveRenderedHtml(event, fallbackText) {
  const html = resolveEventHtml(event);
  if (html) return html;
  return '<pre>' + escapeHtml(resolveEventMarkdown(event) || fallbackText || '') + '</pre>';
}

export function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text == null ? '' : String(text);
  return div.innerHTML;
}

export function post(payload) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(payload);
  }
}

export function formatReasoningSeconds(ms, tFn) {
  return tFn('seconds').replace('{0}', String(Math.max(1, Math.round(ms / 1000))));
}
