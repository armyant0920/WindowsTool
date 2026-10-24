'use strict';

// ---- Session token -------------------------------------------------------
// 啟動時網址為 http://127.0.0.1:<port>/#token=<token>。
// 讀出後放進 sessionStorage（只限這個分頁，重新整理仍可用），並從網址列移除。
const TOKEN_KEY = 'windowstool.token';

function readToken() {
  const hash = new URLSearchParams(location.hash.slice(1));
  const fromHash = hash.get('token');
  if (fromHash) {
    try { sessionStorage.setItem(TOKEN_KEY, fromHash); } catch { /* 無法儲存時只放在記憶體 */ }
    history.replaceState(null, '', location.pathname + location.search);
    return fromHash;
  }
  try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; }
}

const token = readToken();

async function api(path, init = {}) {
  const headers = new Headers(init.headers);
  headers.set('Authorization', `Bearer ${token}`);
  const response = await fetch(path, { ...init, headers });
  if (response.status === 401) {
    throw new Error('連線已失效：請使用 WindowsTool 最新一次啟動時開啟的分頁。');
  }
  if (!response.ok) {
    throw new Error(`伺服器回應錯誤（HTTP ${response.status}）`);
  }
  return response.json();
}

// ---- UI ----------------------------------------------------------------------
const $ = (id) => document.getElementById(id);

function formatBytes(bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function setStatus(text, kind) {
  const el = $('status');
  el.textContent = text;
  el.className = `status ${kind}`;
}

function renderDrives(drives) {
  const tbody = $('drives');
  tbody.replaceChildren();
  for (const d of drives) {
    const used = d.totalBytes - d.freeBytes;
    const ratio = d.totalBytes > 0 ? used / d.totalBytes : 0;

    const tr = document.createElement('tr');
    const cells = [
      d.label ? `${d.name}（${d.label}）` : d.name,
      d.driveType,
      d.format ?? '',
      `${formatBytes(used)} / ${formatBytes(d.totalBytes)}`,
    ];
    cells.forEach((text, i) => {
      const td = document.createElement('td');
      td.textContent = text;
      if (i === 3) td.className = 'num';
      tr.append(td);
    });

    const barCell = document.createElement('td');
    const bar = document.createElement('div');
    bar.className = 'bar';
    bar.title = `${(ratio * 100).toFixed(1)}%`;
    const fill = document.createElement('span');
    fill.style.width = `${(ratio * 100).toFixed(1)}%`;
    bar.append(fill);
    barCell.append(bar);
    tr.append(barCell);

    tbody.append(tr);
  }
}

async function main() {
  if (!token) {
    setStatus('找不到連線金鑰：請從 WindowsTool 啟動時自動開啟的網址進入（主控台視窗上也有顯示）。', 'error');
    return;
  }

  try {
    const info = await api('/api/system/info');
    $('version').textContent = `v${info.version}`;
    $('os').textContent = info.system.osDescription;
    $('arch').textContent = info.system.architecture;
    $('framework').textContent = info.system.framework;
    $('elevated').textContent = info.system.isElevated ? '系統管理員' : '一般使用者';
    renderDrives(info.system.drives);
    $('system').hidden = false;
    setStatus('已連線到本機服務', 'ok');
  } catch (err) {
    setStatus(err.message, 'error');
  }
}

main();
