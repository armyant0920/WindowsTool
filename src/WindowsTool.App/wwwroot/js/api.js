// 與本機服務溝通：session token、JSON API、SSE。

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

export const hasToken = Boolean(token);

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

const EXPIRED = '連線已失效：請使用 WindowsTool 最新一次啟動時開啟的分頁。';

function headers(extra) {
  const h = new Headers(extra);
  h.set('Authorization', `Bearer ${token}`);
  return h;
}

/** 呼叫 JSON API。錯誤一律轉成 ApiError，訊息可以直接顯示給使用者。 */
export async function api(path, { method = 'GET', body, signal } = {}) {
  const init = { method, headers: headers(), signal };
  if (body !== undefined) {
    init.headers.set('Content-Type', 'application/json');
    init.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(path, init);
  } catch (err) {
    if (err.name === 'AbortError') throw err;
    throw new ApiError('無法連線到 WindowsTool：程式可能已經關閉。', 0);
  }

  if (response.status === 401) throw new ApiError(EXPIRED, 401);
  if (response.status === 204) return null;

  const data = await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiError(data?.error ?? `伺服器回應錯誤（HTTP ${response.status}）`, response.status);
  }
  return data;
}

/**
 * 讀取 Server-Sent Events。用 fetch 串流而不是 EventSource，
 * 因為 EventSource 無法帶 Authorization header。
 * 每個事件回傳 { event, data }，data 已經 JSON.parse。
 */
export async function* sse(path, signal) {
  let response;
  try {
    response = await fetch(path, { headers: headers({ Accept: 'text/event-stream' }), signal });
  } catch (err) {
    if (err.name === 'AbortError') throw err;
    throw new ApiError('無法連線到 WindowsTool：程式可能已經關閉。', 0);
  }

  if (response.status === 401) throw new ApiError(EXPIRED, 401);
  if (!response.ok || !response.body) throw new ApiError(`無法取得進度（HTTP ${response.status}）`, response.status);

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += value.replace(/\r\n/g, '\n');

      let end;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);

        let event = 'message';
        const data = [];
        for (const line of block.split('\n')) {
          if (line.startsWith('event:')) event = line.slice(6).trim();
          else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
        }
        if (data.length > 0) yield { event, data: JSON.parse(data.join('\n')) };
      }
    }
  } finally {
    reader.releaseLock();
  }
}

/** 下載檔案（例如 CSV）：用 fetch 帶 token 取得內容，再交給瀏覽器存檔。 */
export async function download(path) {
  const response = await fetch(path, { headers: headers() });
  if (response.status === 401) throw new ApiError(EXPIRED, 401);
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new ApiError(data?.error ?? `下載失敗（HTTP ${response.status}）`, response.status);
  }

  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*=UTF-8''([^;]+)/i.exec(disposition) ?? /filename="?([^";]+)"?/i.exec(disposition);
  const fileName = match ? decodeURIComponent(match[1]) : 'download';

  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
