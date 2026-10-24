// 掃描表單：選擇位置與掃描條件。

import { formatBytes } from '../format.js';

const { ref, reactive, computed } = Vue;

const RECENT_KEY = 'windowstool.recentRoots';
const OPTIONS_KEY = 'windowstool.scanOptions';
const MAX_RECENT = 8;

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

export function rememberRoot(root) {
  const recent = load(RECENT_KEY, []).filter((r) => r.toLowerCase() !== root.toLowerCase());
  recent.unshift(root);
  save(RECENT_KEY, recent.slice(0, MAX_RECENT));
}

const THRESHOLDS = [
  { value: 100 * 1024, label: '100 KB' },
  { value: 1024 * 1024, label: '1 MB' },
  { value: 10 * 1024 * 1024, label: '10 MB' },
  { value: 100 * 1024 * 1024, label: '100 MB' },
];

export const ScanForm = {
  name: 'ScanForm',
  props: {
    drives: { type: Array, default: () => [] },
    start: { type: Function, required: true },
  },
  setup(props) {
    const saved = load(OPTIONS_KEY, {});
    const recent = ref(load(RECENT_KEY, []));
    const root = ref(recent.value[0] ?? '');
    const options = reactive({
      includeHidden: saved.includeHidden ?? true,
      includeSystem: saved.includeSystem ?? true,
      excludeNames: saved.excludeNames ?? 'System Volume Information',
      keepFileThreshold: saved.keepFileThreshold ?? 1024 * 1024,
    });
    const busy = ref(false);
    const error = ref(null);

    const drives = computed(() => props.drives.map((d) => ({
      ...d,
      used: d.totalBytes - d.freeBytes,
      ratio: d.totalBytes ? (d.totalBytes - d.freeBytes) / d.totalBytes : 0,
    })));

    // 網路位置（UNC 路徑或網路磁碟機）掃描會比較慢
    const isNetwork = computed(() => {
      const value = root.value.trim();
      if (value.startsWith('\\\\')) return true; // \\server\share
      const drive = props.drives.find((d) => value.toUpperCase().startsWith(d.name.toUpperCase()));
      return drive?.driveType === 'Network';
    });

    async function submit() {
      const value = root.value.trim();
      if (!value) {
        error.value = '請輸入或選擇要掃描的位置。';
        return;
      }

      busy.value = true;
      error.value = null;
      save(OPTIONS_KEY, { ...options });
      try {
        await props.start({
          root: value,
          includeHidden: options.includeHidden,
          includeSystem: options.includeSystem,
          excludeNames: options.excludeNames.split(/[,;\n]/).map((s) => s.trim()).filter(Boolean),
          keepFileThreshold: options.keepFileThreshold,
        });
      } catch (err) {
        error.value = err.message;
      } finally {
        busy.value = false;
      }
    }

    function pick(path) {
      root.value = path;
      error.value = null;
    }

    function forget(path) {
      recent.value = recent.value.filter((r) => r !== path);
      save(RECENT_KEY, recent.value);
    }

    return { root, options, busy, error, recent, drives, isNetwork, submit, pick, forget, formatBytes, THRESHOLDS };
  },
  template: `
    <section class="card">
      <h2>掃描位置</h2>
      <form class="scan-form" @submit.prevent="submit">
        <div class="path-row">
          <input class="input path-input" v-model="root" placeholder="例如 C:\\  或  D:\\Projects  或  %USERPROFILE%\\Downloads"
                 spellcheck="false" autocomplete="off" aria-label="要掃描的位置">
          <button class="btn primary" type="submit" :disabled="busy">{{ busy ? '開始中…' : '開始掃描' }}</button>
        </div>
        <p v-if="error" class="message error">{{ error }}</p>
        <p v-else-if="isNetwork" class="message info">這是網路位置，掃描速度會比本機磁碟慢很多。</p>

        <div v-if="drives.length" class="chip-group">
          <span class="chip-label">磁碟</span>
          <button v-for="d in drives" :key="d.name" type="button" class="drive-chip" @click="pick(d.name)"
                  :class="{ active: root === d.name }" :title="'可用 ' + formatBytes(d.freeBytes)">
            <span class="drive-name">{{ d.name }}<span v-if="d.label" class="muted">{{ d.label }}</span></span>
            <span class="meter"><span :style="{ width: (d.ratio * 100).toFixed(1) + '%' }"></span></span>
            <span class="drive-size muted">{{ formatBytes(d.used) }} / {{ formatBytes(d.totalBytes) }}</span>
          </button>
        </div>

        <div v-if="recent.length" class="chip-group">
          <span class="chip-label">最近使用</span>
          <span v-for="r in recent" :key="r" class="recent-chip">
            <button type="button" class="link" @click="pick(r)" :title="r">{{ r }}</button>
            <button type="button" class="icon-btn" @click="forget(r)" :aria-label="'從清單移除 ' + r" title="移除">×</button>
          </span>
        </div>

        <details class="advanced">
          <summary>進階選項</summary>
          <div class="options-grid">
            <label class="check"><input type="checkbox" v-model="options.includeHidden"> 包含隱藏的檔案和資料夾</label>
            <label class="check"><input type="checkbox" v-model="options.includeSystem"> 包含系統檔案</label>
            <label class="field">
              <span>略過這些資料夾名稱（以逗號分隔）</span>
              <input class="input" v-model="options.excludeNames" spellcheck="false">
            </label>
            <label class="field">
              <span>個別列出的檔案大小門檻</span>
              <select class="input" v-model.number="options.keepFileThreshold">
                <option v-for="t in THRESHOLDS" :key="t.value" :value="t.value">{{ t.label }} 以上</option>
              </select>
              <small class="muted">較小的檔案只會列出每個資料夾最大的 3 個，其餘計入「其他小檔案」，以節省記憶體。</small>
            </label>
          </div>
        </details>
      </form>
    </section>
  `,
};
