// WindowsTool 前端進入點。

import { api, sse, hasToken } from './api.js';
import { ScanForm, rememberRoot } from './components/scan-form.js';
import { ScanProgressView } from './components/scan-progress.js';
import { ResultView } from './components/result-view.js';
import { TreeNode } from './components/tree-node.js';
import { SearchView } from './components/search-view.js';
import { RulesView } from './components/rules-view.js';

const { createApp, ref, computed, onMounted } = Vue;

const App = {
  components: { ScanForm, ScanProgressView, ResultView, SearchView, RulesView },
  setup() {
    const TAB_KEY = 'windowstool.tab';
    let initialTab = 'scan';
    try {
      const saved = localStorage.getItem(TAB_KEY);
      if (saved === 'search' || saved === 'rules') initialTab = saved;
    } catch { /* 預設磁碟掃描 */ }
    const tab = ref(initialTab); // scan | search | rules
    const view = ref('home'); // 磁碟掃描的畫面：home | scanning | results
    const system = ref(null);
    const fatal = ref(null);
    const notice = ref(null);
    const scan = ref(null);
    const resultKey = ref(0);
    const rulesVersion = ref(0); // 規則變動時遞增，結果頁會重新載入
    const rulePrefill = ref(null);
    const rulesMounted = ref(initialTab === 'rules'); // 第一次切到規則頁才載入
    let lastRequest = null;
    let follower = null;

    onMounted(async () => {
      if (!hasToken) {
        fatal.value = '找不到連線金鑰：請從 WindowsTool 啟動時自動開啟的網址進入（主控台視窗上也有顯示）。';
        return;
      }
      try {
        system.value = await api('/api/system/info');
      } catch (err) {
        fatal.value = err.message;
      }
    });

    async function startScan(request) {
      notice.value = null;
      const { scanId } = await api('/api/scans', { method: 'POST', body: request });
      lastRequest = request;
      rememberRoot(request.root);
      scan.value = {
        scanId,
        root: request.root,
        status: 'running',
        elapsedMs: 0,
        expectedBytes: null,
        progress: { dirs: 0, files: 0, bytes: 0, denied: 0, currentPath: null },
      };
      view.value = 'scanning';
      follow(scanId);
    }

    async function follow(scanId) {
      follower?.abort();
      const controller = new AbortController();
      follower = controller;
      try {
        for await (const { event, data } of sse(`/api/scans/${scanId}/events`, controller.signal)) {
          scan.value = data;
          if (event === 'done') break;
        }
      } catch (err) {
        if (err.name === 'AbortError') return;
        notice.value = { kind: 'error', text: err.message };
        view.value = 'home';
        return;
      }

      const s = scan.value;
      if (s.status === 'completed') {
        resultKey.value++;
        view.value = 'results';
      } else if (s.status === 'cancelled') {
        notice.value = { kind: 'info', text: '已取消掃描。' };
        view.value = 'home';
      } else {
        notice.value = { kind: 'error', text: `掃描失敗：${s.error ?? '未知的錯誤'}` };
        view.value = 'home';
      }
    }

    async function cancelScan() {
      if (!scan.value) return;
      try {
        await api(`/api/scans/${scan.value.scanId}`, { method: 'DELETE' });
      } catch {
        // 進度串流會回報最後的狀態
      }
    }

    function newScan() {
      view.value = 'home';
    }

    async function rescan() {
      if (!lastRequest) return newScan();
      try {
        await startScan(lastRequest);
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
        view.value = 'home';
      }
    }

    // 給規則頁「試套用」用：目前完成的掃描
    const completedScan = computed(() =>
      view.value === 'results' && scan.value?.status === 'completed' ? scan.value : null);

    function createRuleFrom(item) {
      rulePrefill.value = item;
      switchTab('rules');
    }

    function switchTab(name) {
      tab.value = name;
      if (name === 'rules') rulesMounted.value = true;
      try { localStorage.setItem(TAB_KEY, name); } catch { /* 無法儲存就算了 */ }
    }

    return {
      tab, view, system, fatal, notice, scan, resultKey, rulesVersion, rulePrefill, rulesMounted, completedScan,
      startScan, cancelScan, newScan, rescan, switchTab, createRuleFrom,
    };
  },
  template: `
    <header class="topbar">
      <div class="brand">
        <span class="logo" aria-hidden="true"></span>
        <h1>WindowsTool</h1>
        <span v-if="system" class="muted version">v{{ system.version }}</span>
      </div>
      <nav class="tabs" aria-label="功能">
        <button type="button" class="tab" :class="{ active: tab === 'scan' }" :aria-current="tab === 'scan' ? 'page' : undefined" @click="switchTab('scan')">磁碟掃描</button>
        <button type="button" class="tab" :class="{ active: tab === 'search' }" :aria-current="tab === 'search' ? 'page' : undefined" @click="switchTab('search')">快速搜尋</button>
        <button type="button" class="tab" :class="{ active: tab === 'rules' }" :aria-current="tab === 'rules' ? 'page' : undefined" @click="switchTab('rules')">規則</button>
      </nav>
      <span v-if="system" class="muted env">{{ system.system.isElevated ? '系統管理員' : '一般權限' }}</span>
    </header>

    <main class="page">
      <section v-if="fatal" class="card"><p class="message error">{{ fatal }}</p></section>
      <template v-else>
        <!-- 用 v-show 切換，兩邊的狀態（掃描結果、搜尋結果）都會保留 -->
        <div v-show="tab === 'scan'">
          <p v-if="notice" class="message" :class="notice.kind">{{ notice.text }}</p>
          <scan-form v-if="view === 'home'" :drives="system ? system.system.drives : []" :start="startScan" />
          <scan-progress-view v-else-if="view === 'scanning' && scan" :scan="scan" :cancel="cancelScan" />
          <result-view v-else-if="view === 'results' && scan" :key="resultKey" :scan="scan" :rules-version="rulesVersion"
                       @new-scan="newScan" @rescan="rescan" @create-rule="createRuleFrom" />
        </div>
        <div v-show="tab === 'search'">
          <search-view :drives="system ? system.system.drives : []" />
        </div>
        <div v-if="rulesMounted" v-show="tab === 'rules'">
          <rules-view :scan-id="completedScan ? completedScan.scanId : null" :scan-root="completedScan ? completedScan.root : null"
                      :prefill="rulePrefill" @changed="rulesVersion++" @prefill-used="rulePrefill = null" />
        </div>
      </template>
    </main>
  `,
};

const app = createApp(App);
app.component('tree-node', TreeNode);
app.mount('#app');
