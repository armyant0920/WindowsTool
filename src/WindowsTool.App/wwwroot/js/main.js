// WindowsTool 前端進入點。

import { api, sse, hasToken } from './api.js';
import { ScanForm, rememberRoot } from './components/scan-form.js';
import { ScanProgressView } from './components/scan-progress.js';
import { ResultView } from './components/result-view.js';
import { TreeNode } from './components/tree-node.js';

const { createApp, ref, onMounted } = Vue;

const App = {
  components: { ScanForm, ScanProgressView, ResultView },
  setup() {
    const view = ref('home'); // home | scanning | results
    const system = ref(null);
    const fatal = ref(null);
    const notice = ref(null);
    const scan = ref(null);
    const resultKey = ref(0);
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

    return { view, system, fatal, notice, scan, resultKey, startScan, cancelScan, newScan, rescan };
  },
  template: `
    <header class="topbar">
      <div class="brand">
        <span class="logo" aria-hidden="true"></span>
        <h1>WindowsTool</h1>
        <span v-if="system" class="muted version">v{{ system.version }}</span>
      </div>
      <nav class="tabs" aria-label="功能">
        <button type="button" class="tab active" aria-current="page">磁碟掃描</button>
        <button type="button" class="tab" disabled title="即將推出（Phase 1.5）">快速搜尋</button>
      </nav>
      <span v-if="system" class="muted env">{{ system.system.isElevated ? '系統管理員' : '一般權限' }}</span>
    </header>

    <main class="page">
      <section v-if="fatal" class="card"><p class="message error">{{ fatal }}</p></section>
      <template v-else>
        <p v-if="notice" class="message" :class="notice.kind">{{ notice.text }}</p>
        <scan-form v-if="view === 'home'" :drives="system ? system.system.drives : []" :start="startScan" />
        <scan-progress-view v-else-if="view === 'scanning' && scan" :scan="scan" :cancel="cancelScan" />
        <result-view v-else-if="view === 'results' && scan" :key="resultKey" :scan="scan" @new-scan="newScan" @rescan="rescan" />
      </template>
    </main>
  `,
};

const app = createApp(App);
app.component('tree-node', TreeNode);
app.mount('#app');
