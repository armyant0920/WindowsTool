// 顯示格式。

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

export function formatBytes(bytes) {
  if (bytes == null || Number.isNaN(bytes)) return '—';
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  const digits = unit === 0 ? 0 : value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${UNITS[unit]}`;
}

const numberFormat = new Intl.NumberFormat('zh-TW');

export function formatNumber(n) {
  return n == null ? '—' : numberFormat.format(n);
}

const dateFormat = new Intl.DateTimeFormat('zh-TW', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false,
});

/** 早於 1980 年的時間（例如空資料夾沒有檔案時間）顯示為「—」。 */
export function formatDate(iso) {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime()) || date.getFullYear() < 1980) return '—';
  return dateFormat.format(date);
}

export function formatDuration(ms) {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms} 毫秒`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)} 秒`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes} 分 ${Math.round(seconds - minutes * 60)} 秒`;
}

export function formatPercent(part, whole) {
  if (!whole) return '—';
  const p = (part / whole) * 100;
  return p >= 10 ? `${p.toFixed(0)}%` : `${p.toFixed(1)}%`;
}

export const FLAG_LABELS = {
  reparsePoint: { short: '連結', text: '連結（Symlink / Junction）：不會往下掃描，也不計入大小，避免重複計算。' },
  accessDenied: { short: '無法讀取', text: '權限不足或讀取失敗，這個資料夾的大小可能被低估。' },
  cloudPlaceholder: { short: '雲端', text: '雲端預留位置（OneDrive 等）：顯示的是檔案大小，實際佔用的磁碟空間可能更小。' },
  hidden: { short: '隱藏', text: '隱藏項目。' },
  system: { short: '系統', text: '系統項目。' },
};

export function extLabel(ext) {
  if (ext === '*') return '其他';
  if (ext === '') return '（無副檔名）';
  return ext.toLowerCase();
}
