// 快速搜尋：在指定位置下依條件找檔案，結果邊找邊顯示（PLAN 9A）。

import { api, sse, download } from '../api.js';
import { formatBytes, formatNumber, formatDate, formatDuration, FLAG_LABELS } from '../format.js';

const { ref, reactive, computed, shallowRef, watch, onBeforeUnmount } = Vue;

const STATE_KEY = 'windowstool.search';
const RECENT_PATTERNS_KEY = 'windowstool.recentPatterns';
const PAGE = 300;

const UNITS = [
  { value: 1024, label: 'KB' },
  { value: 1024 * 1024, label: 'MB' },
  { value: 1024 * 1024 * 1024, label: 'GB' },
];

const LIMITS = [1000, 10000, 50000, 100000];
const KIND_ICONS = { dir: '📁', file: '📄', link: '🔗' };

function load(key, fallback) {
  try {
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) : fallback;
  } catch {
    return fallback;
  }
}

function save(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* 無法儲存就算了 */ }
}

/** yyyy-mm-dd（本地時間）→ 當天開始或結束的 ISO 時間。 */
function dayBoundary(value, endOfDay) {
  if (!value) return null;
  const [y, m, d] = value.split('-').map(Number);
  const date = endOfDay ? new Date(y, m - 1, d, 23, 59, 59, 999) : new Date(y, m - 1, d);
  return date.toISOString();
}

function toBytes(amount, unit) {
  if (amount === '' || amount === null || amount === undefined) return null;
  const n = Number(amount);
  return Number.isFinite(n) && n >= 0 ? Math.round(n * unit) : null;
}

export const SearchView = {
  name: 'SearchView',
  props: {
    drives: { type: Array, default: () => [] },
  },
  setup(props) {
    const saved = load(STATE_KEY, {});
    const form = reactive({
      roots: saved.roots ?? [],
      rootInput: '',
      pattern: '',
      mode: saved.mode ?? 'auto',
      matchFullPath: saved.matchFullPath ?? false,
      kind: saved.kind ?? 'any',
      ext: saved.ext ?? '',
      minSize: '', minUnit: 1024 * 1024,
      maxSize: '', maxUnit: 1024 * 1024,
      after: '', before: '',
      includeHidden: saved.includeHidden ?? true,
      includeSystem: saved.includeSystem ?? false,
      maxDepth: '',
      excludeNames: saved.excludeNames ?? '$Recycle.Bin, System Volume Information',
      maxResults: saved.maxResults ?? 10000,
    });
    const recentPatterns = ref(load(RECENT_PATTERNS_KEY, []));

    // 沒有記錄時，預設搜尋第一個磁碟
    watch(() => props.drives, (drives) => {
      if (form.roots.length === 0 && drives.length > 0) form.roots = [drives[0].name];
    }, { immediate: true });

    const formError = ref(null);
    const notice = ref(null);
    const status = ref(null);         // 最後一次收到的 SearchEventDto（不含 hits）
    const hits = shallowRef([]);      // 結果很多，用 shallowRef 避免每筆都變成 reactive
    const searchId = ref(null);
    const starting = ref(false);
    const showDenied = ref(false);
    const selected = ref(null);
    const visible = ref(PAGE);
    const filterText = ref('');
    const sort = reactive({ by: null, desc: false }); // null = 依找到的順序
    let follower = null;

    const running = computed(() => status.value?.status === 'running');
    const hasExtraFilters = computed(() =>
      form.kind !== 'any' || form.ext.trim() || form.minSize !== '' || form.maxSize !== '' || form.after || form.before
      || form.maxDepth !== '' || !form.includeHidden || form.includeSystem);

    // ---- 位置 ----
    function addRoot(path) {
      const value = (path ?? form.rootInput).trim();
      if (!value) return;
      if (!form.roots.some((r) => r.toLowerCase() === value.toLowerCase())) form.roots = [...form.roots, value];
      form.rootInput = '';
    }

    function removeRoot(path) {
      form.roots = form.roots.filter((r) => r !== path);
    }

    function toggleDrive(name) {
      if (form.roots.includes(name)) removeRoot(name);
      else addRoot(name);
    }

    function allDrives() {
      form.roots = props.drives.map((d) => d.name);
    }

    // ---- 搜尋 ----
    function buildRequest() {
      return {
        roots: form.roots,
        pattern: form.pattern.trim() || null,
        mode: form.mode,
        matchFullPath: form.matchFullPath,
        kind: form.kind,
        extensions: form.ext.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean),
        minSize: toBytes(form.minSize, form.minUnit),
        maxSize: toBytes(form.maxSize, form.maxUnit),
        modifiedAfter: dayBoundary(form.after, false),
        modifiedBefore: dayBoundary(form.before, true),
        includeHidden: form.includeHidden,
        includeSystem: form.includeSystem,
        maxDepth: form.maxDepth === '' ? null : Number(form.maxDepth),
        excludeNames: form.excludeNames.split(/[,;\n]/).map((s) => s.trim()).filter(Boolean),
        maxResults: form.maxResults,
      };
    }

    function remember() {
      save(STATE_KEY, {
        roots: form.roots, mode: form.mode, matchFullPath: form.matchFullPath, kind: form.kind, ext: form.ext,
        includeHidden: form.includeHidden, includeSystem: form.includeSystem, excludeNames: form.excludeNames,
        maxResults: form.maxResults,
      });
      const p = form.pattern.trim();
      if (p) {
        recentPatterns.value = [p, ...recentPatterns.value.filter((x) => x !== p)].slice(0, 15);
        save(RECENT_PATTERNS_KEY, recentPatterns.value);
      }
    }

    async function submit() {
      if (form.rootInput.trim()) addRoot();
      if (form.roots.length === 0) {
        formError.value = '請至少選擇一個搜尋位置。';
        return;
      }

      formError.value = null;
      notice.value = null;
      starting.value = true;
      follower?.abort();
      try {
        const { searchId: id } = await api('/api/search', { method: 'POST', body: buildRequest() });
        remember();
        searchId.value = id;
        hits.value = [];
        selected.value = null;
        visible.value = PAGE;
        filterText.value = '';
        sort.by = null;
        status.value = { status: 'running', progress: { dirs: 0, matches: 0, denied: 0, currentPath: null }, elapsedMs: 0, roots: form.roots };
        follow(id);
      } catch (err) {
        formError.value = err.message;
      } finally {
        starting.value = false;
      }
    }

    async function follow(id) {
      const controller = new AbortController();
      follower = controller;
      try {
        for await (const { event, data } of sse(`/api/search/${id}/events`, controller.signal)) {
          if (data.hits.length > 0) hits.value = hits.value.concat(data.hits);
          status.value = { ...data, hits: undefined };
          if (event === 'done') break;
        }
      } catch (err) {
        if (err.name !== 'AbortError') notice.value = { kind: 'error', text: err.message };
      }
    }

    async function stop() {
      if (!searchId.value) return;
      try {
        await api(`/api/search/${searchId.value}/cancel`, { method: 'POST' });
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      }
    }

    onBeforeUnmount(() => follower?.abort());

    // ---- 結果 ----
    const filtered = computed(() => {
      const q = filterText.value.trim().toLowerCase();
      let list = q ? hits.value.filter((h) => h.path.toLowerCase().includes(q)) : hits.value;
      if (sort.by) {
        const dir = sort.desc ? -1 : 1;
        const key = sort.by;
        const collator = new Intl.Collator('zh-TW', { numeric: true, sensitivity: 'base' });
        list = [...list].sort((a, b) => {
          let r;
          if (key === 'size') r = a.size - b.size;
          else if (key === 'modified') r = a.lastWriteUtc < b.lastWriteUtc ? -1 : a.lastWriteUtc > b.lastWriteUtc ? 1 : 0;
          else if (key === 'directory') r = collator.compare(a.directory, b.directory);
          else r = collator.compare(a.name, b.name);
          return r * dir || a.index - b.index;
        });
      }
      return list;
    });

    const shown = computed(() => filtered.value.slice(0, visible.value));

    function sortBy(column) {
      if (sort.by === column) {
        if (!sort.desc) sort.desc = true;
        else { sort.by = null; sort.desc = false; } // 第三次點擊回到找到的順序
      } else {
        sort.by = column;
        sort.desc = column === 'size' || column === 'modified';
      }
    }

    function sortMark(column) {
      if (sort.by !== column) return '';
      return sort.desc ? '▼' : '▲';
    }

    async function shell(action, hit) {
      notice.value = null;
      try {
        await api(`/api/shell/${action}`, { method: 'POST', body: { searchId: searchId.value, hitIndex: hit.index } });
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      }
    }

    const copiedIndex = ref(null);
    async function copyPath(hit) {
      try {
        await navigator.clipboard.writeText(hit.path);
        copiedIndex.value = hit.index;
        setTimeout(() => { if (copiedIndex.value === hit.index) copiedIndex.value = null; }, 1500);
      } catch {
        notice.value = { kind: 'error', text: '無法複製到剪貼簿。' };
      }
    }

    async function exportCsv() {
      try {
        await download(`/api/search/${searchId.value}/export`);
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      }
    }

    function isDriveSelected(name) {
      return form.roots.includes(name);
    }

    return {
      form, recentPatterns, formError, notice, status, hits, filtered, shown, visible, filterText, sort, selected,
      running, starting, showDenied, hasExtraFilters, copiedIndex, UNITS, LIMITS, KIND_ICONS, FLAG_LABELS, PAGE,
      addRoot, removeRoot, toggleDrive, allDrives, isDriveSelected, submit, stop, sortBy, sortMark, shell, copyPath, exportCsv,
      formatBytes, formatNumber, formatDate, formatDuration,
    };
  },
  template: `
    <section class="card">
      <form class="search-form" @submit.prevent="submit">
        <div class="path-row">
          <input class="input search-input" v-model="form.pattern" list="recent-patterns" autofocus
                 placeholder="檔名包含…（空白分隔多個關鍵字）或萬用字元，例如 *.pdf;*.docx" spellcheck="false" aria-label="搜尋條件">
          <datalist id="recent-patterns"><option v-for="p in recentPatterns" :key="p" :value="p"></option></datalist>
          <select class="input" v-model="form.mode" aria-label="比對方式">
            <option value="auto">一般 / 萬用字元</option>
            <option value="regex">正規表示式</option>
          </select>
          <button v-if="!running" class="btn primary" type="submit" :disabled="starting">{{ starting ? '開始中…' : '搜尋' }}</button>
          <button v-else class="btn" type="button" @click="stop">停止</button>
        </div>
        <p v-if="formError" class="message error">{{ formError }}</p>

        <div class="chip-group">
          <span class="chip-label">位置</span>
          <span v-for="r in form.roots" :key="r" class="recent-chip">
            <span class="mono root-name" :title="r">{{ r }}</span>
            <button type="button" class="icon-btn" @click="removeRoot(r)" :aria-label="'移除 ' + r" title="移除">×</button>
          </span>
          <input class="input root-input" v-model="form.rootInput" placeholder="加入資料夾路徑後按 Enter"
                 spellcheck="false" @keydown.enter.prevent="addRoot()" aria-label="加入搜尋位置">
        </div>
        <div v-if="drives.length" class="chip-group">
          <span class="chip-label">磁碟</span>
          <button v-for="d in drives" :key="d.name" type="button" class="toggle-chip" :class="{ active: isDriveSelected(d.name) }"
                  @click="toggleDrive(d.name)">{{ d.name }}<span v-if="d.label" class="muted"> {{ d.label }}</span></button>
          <button type="button" class="link" @click="allDrives">全部磁碟</button>
        </div>

        <details class="advanced" :open="hasExtraFilters">
          <summary>更多條件<span v-if="hasExtraFilters" class="badge">已設定</span></summary>
          <div class="options-grid">
            <label class="field">
              <span>類型</span>
              <select class="input" v-model="form.kind">
                <option value="any">檔案和資料夾</option>
                <option value="file">只找檔案</option>
                <option value="dir">只找資料夾</option>
              </select>
            </label>
            <label class="field">
              <span>副檔名（以空白或逗號分隔）</span>
              <input class="input" v-model="form.ext" placeholder="例如 mp4 mkv" spellcheck="false">
            </label>
            <div class="field">
              <span>大小</span>
              <div class="range-row">
                <input class="input num-input" type="number" min="0" step="any" v-model="form.minSize" placeholder="最小" aria-label="最小大小">
                <select class="input" v-model.number="form.minUnit" aria-label="最小大小單位"><option v-for="u in UNITS" :key="u.value" :value="u.value">{{ u.label }}</option></select>
                <span class="muted">～</span>
                <input class="input num-input" type="number" min="0" step="any" v-model="form.maxSize" placeholder="最大" aria-label="最大大小">
                <select class="input" v-model.number="form.maxUnit" aria-label="最大大小單位"><option v-for="u in UNITS" :key="u.value" :value="u.value">{{ u.label }}</option></select>
              </div>
            </div>
            <div class="field">
              <span>修改日期</span>
              <div class="range-row">
                <input class="input" type="date" v-model="form.after" aria-label="修改日期起">
                <span class="muted">～</span>
                <input class="input" type="date" v-model="form.before" aria-label="修改日期迄">
              </div>
            </div>
            <label class="field">
              <span>深度上限（空白 = 不限）</span>
              <input class="input num-input" type="number" min="1" v-model="form.maxDepth" placeholder="不限">
            </label>
            <label class="field">
              <span>結果上限</span>
              <select class="input" v-model.number="form.maxResults">
                <option v-for="l in LIMITS" :key="l" :value="l">{{ formatNumber(l) }} 筆</option>
              </select>
            </label>
            <label class="field">
              <span>略過這些資料夾名稱（以逗號分隔）</span>
              <input class="input" v-model="form.excludeNames" spellcheck="false">
            </label>
            <div class="field">
              <span>其他</span>
              <label class="check"><input type="checkbox" v-model="form.matchFullPath"> 比對完整路徑（而不只是名稱）</label>
              <label class="check"><input type="checkbox" v-model="form.includeHidden"> 包含隱藏的檔案和資料夾</label>
              <label class="check"><input type="checkbox" v-model="form.includeSystem"> 包含系統檔案</label>
            </div>
          </div>
        </details>
      </form>
    </section>

    <p v-if="notice" class="message" :class="notice.kind">{{ notice.text }}</p>

    <section v-if="status" class="card">
      <div class="search-status">
        <div>
          <strong v-if="running">搜尋中… 已找到 {{ formatNumber(status.progress.matches) }} 筆</strong>
          <strong v-else-if="status.status === 'completed'">找到 {{ formatNumber(status.progress.matches) }} 筆</strong>
          <strong v-else-if="status.status === 'cancelled'">已停止，目前找到 {{ formatNumber(status.progress.matches) }} 筆</strong>
          <strong v-else class="error-text">搜尋失敗：{{ status.error }}</strong>
          <span class="muted">　已搜尋 {{ formatNumber(status.progress.dirs) }} 個資料夾 · {{ formatDuration(status.elapsedMs) }}</span>
        </div>
        <div class="search-actions">
          <input class="input" v-model="filterText" placeholder="在結果中篩選…" aria-label="在結果中篩選" spellcheck="false">
          <button class="btn" type="button" @click="exportCsv" :disabled="hits.length === 0">匯出 CSV</button>
        </div>
      </div>
      <p v-if="running" class="mono current-path muted" :title="status.progress.currentPath">{{ status.progress.currentPath || '準備中…' }}</p>
      <div v-if="status.truncated" class="message warn">結果超過上限（{{ formatNumber(status.maxResults) }} 筆），搜尋已提前停止。請縮小條件，或在「更多條件」調高結果上限。</div>
      <div v-if="status.progress.denied > 0 && !running" class="message warn">
        {{ formatNumber(status.progress.denied) }} 個資料夾無法讀取，裡面的檔案沒有被搜尋到。
        <button class="link" type="button" @click="showDenied = !showDenied">{{ showDenied ? '隱藏清單' : '查看清單' }}</button>
        <ul v-if="showDenied && status.deniedPaths" class="error-list mono">
          <li v-for="p in status.deniedPaths" :key="p">{{ p }}</li>
        </ul>
      </div>

      <div class="table-wrap">
        <table class="items hits">
          <thead>
            <tr>
              <th><button type="button" class="th-btn" @click="sortBy('name')">名稱 {{ sortMark('name') }}</button></th>
              <th class="hide-sm"><button type="button" class="th-btn" @click="sortBy('directory')">所在資料夾 {{ sortMark('directory') }}</button></th>
              <th class="num"><button type="button" class="th-btn" @click="sortBy('size')">大小 {{ sortMark('size') }}</button></th>
              <th class="hide-sm"><button type="button" class="th-btn" @click="sortBy('modified')">修改時間 {{ sortMark('modified') }}</button></th>
              <th class="actions-col"><span class="sr-only">動作</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in shown" :key="h.index" :class="{ selected: selected === h.index }"
                @click="selected = h.index" @dblclick="shell('reveal', h)" tabindex="0" @keydown.enter="shell('reveal', h)">
              <td class="name-cell">
                <span class="kind-icon" aria-hidden="true">{{ KIND_ICONS[h.kind] }}</span>
                <span class="name-text"><span :title="h.path">{{ h.name }}</span><small class="muted rel-path show-sm">{{ h.directory }}</small></span>
                <span v-for="f in h.flags" :key="f" class="badge" :class="'badge-' + f" :title="FLAG_LABELS[f]?.text">{{ FLAG_LABELS[f]?.short ?? f }}</span>
              </td>
              <td class="hide-sm dir-cell mono" :title="h.directory">{{ h.directory }}</td>
              <td class="num">{{ h.kind === 'file' ? formatBytes(h.size) : '' }}</td>
              <td class="hide-sm nowrap">{{ formatDate(h.lastWriteUtc) }}</td>
              <td class="actions-col nowrap">
                <button type="button" class="link" @click.stop="shell('reveal', h)" title="在檔案總管中顯示">顯示</button>
                <button type="button" class="link" @click.stop="shell('open', h)" title="用預設程式開啟">開啟</button>
                <button type="button" class="link" @click.stop="copyPath(h)" title="複製完整路徑">{{ copiedIndex === h.index ? '已複製' : '複製' }}</button>
              </td>
            </tr>
            <tr v-if="shown.length === 0 && !running">
              <td colspan="5" class="empty muted">{{ hits.length ? '沒有符合篩選的結果。' : '沒有找到符合條件的項目。' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="table-foot">
        <span class="muted">
          {{ filterText ? '篩選後 ' + formatNumber(filtered.length) + ' 筆，' : '' }}顯示 {{ formatNumber(shown.length) }} / {{ formatNumber(filtered.length) }} 筆
        </span>
        <button v-if="shown.length < filtered.length" class="btn" type="button" @click="visible += PAGE">顯示更多</button>
      </div>
      <p class="hint muted">按兩下（或 Enter）在檔案總管中顯示。點欄位標題排序，再點一次反向，第三次回到找到的順序。</p>
    </section>
  `,
};
