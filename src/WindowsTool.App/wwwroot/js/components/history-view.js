// 紀錄與隔離區：隔離區的項目可以一鍵還原或清空；操作紀錄可以查詢。

import { api } from '../api.js';
import { formatBytes, formatNumber, formatDate } from '../format.js';
import { ACTION_TEXT } from './plan-view.js';

const { ref, reactive, computed, onMounted } = Vue;

const CONFIRM_TEXT = '永久刪除';
const LOG_ACTIONS = { ...ACTION_TEXT, restore: '從隔離區還原', purge: '清空隔離區' };
const OUTCOME_TEXT = { ok: '完成', failed: '失敗', skipped: '略過' };
const PURGE_OPTIONS = [
  { value: 30, label: '30 天前' },
  { value: 7, label: '7 天前' },
  { value: 0, label: '全部' },
];

export const HistoryView = {
  name: 'HistoryView',
  props: {
    refreshKey: { type: Number, default: 0 },
  },
  setup(props) {
    const quarantine = ref(null);
    const logs = ref(null);
    const error = ref(null);
    const notice = ref(null);
    const busy = ref(false);
    const restoring = ref(null);
    const purge = reactive({ days: 30, confirmation: '', open: false });
    const logFilter = ref('all');

    const shownLogs = computed(() => {
      const entries = logs.value?.entries ?? [];
      return logFilter.value === 'all' ? entries : entries.filter((e) => e.result === logFilter.value);
    });

    async function load() {
      error.value = null;
      try {
        const [q, l] = await Promise.all([api('/api/quarantine'), api('/api/logs?limit=500')]);
        quarantine.value = q;
        logs.value = l;
      } catch (err) {
        error.value = err.message;
      }
    }

    async function restore(entry) {
      restoring.value = entry.entryId;
      notice.value = null;
      try {
        await api(`/api/quarantine/${encodeURIComponent(entry.entryId)}/restore`, { method: 'POST' });
        notice.value = { kind: 'info', text: `已還原：${entry.originalPath}` };
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      } finally {
        restoring.value = null;
        await load();
      }
    }

    async function doPurge() {
      busy.value = true;
      notice.value = null;
      try {
        const result = await api('/api/quarantine/purge', {
          method: 'POST',
          body: { olderThanDays: purge.days, confirmation: purge.confirmation.trim() },
        });
        notice.value = {
          kind: result.failures.length ? 'warn' : 'info',
          text: `已永久刪除 ${formatNumber(result.purged)} 項（${formatBytes(result.bytes)}）。`
            + (result.failures.length ? ` ${result.failures.length} 項失敗：${result.failures.join('；')}` : ''),
        };
        Object.assign(purge, { confirmation: '', open: false });
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      } finally {
        busy.value = false;
        await load();
      }
    }

    function undoText(entry) {
      const u = entry.undo;
      if (!u) return '';
      if (u.type === 'recycleBin') return '可以從資源回收筒還原';
      if (u.type === 'quarantine') return entry.action === 'restore' ? '' : '可以在上方的隔離區還原';
      if (u.type === 'move') return `搬到 ${u.from}`;
      return '';
    }

    onMounted(load);
    // 執行計畫完成後重新整理
    Vue.watch(() => props.refreshKey, load);

    return {
      quarantine, logs, error, notice, busy, restoring, purge, logFilter, shownLogs,
      PURGE_OPTIONS, LOG_ACTIONS, OUTCOME_TEXT, CONFIRM_TEXT,
      load, restore, doPurge, undoText, formatBytes, formatNumber, formatDate,
    };
  },
  template: `
    <section class="card">
      <div class="summary-head">
        <div class="summary-title">
          <h2>隔離區</h2>
          <p class="muted" style="margin:0" v-if="quarantine">
            {{ formatNumber(quarantine.entries.length) }} 項，共 {{ formatBytes(quarantine.totalBytes) }}。
            隔離的項目沒有被刪除，只是搬到 <span class="mono">{{ quarantine.root }}</span>（其他磁碟的項目在該磁碟的 <span class="mono">WindowsTool.quarantine</span>）。
          </p>
        </div>
        <div class="summary-actions">
          <button class="btn" type="button" @click="load">重新整理</button>
          <button v-if="quarantine && quarantine.entries.length" class="btn" type="button" @click="purge.open = !purge.open">清空…</button>
        </div>
      </div>
      <p v-if="error" class="message error">{{ error }}</p>
      <p v-if="notice" class="message" :class="notice.kind">{{ notice.text }}</p>

      <div v-if="purge.open" class="message error confirm-box">
        <strong>清空會永久刪除隔離區中的項目，無法還原。</strong>
        <div class="range-row" style="margin-top:8px">
          <select class="input" v-model.number="purge.days" aria-label="清空範圍">
            <option v-for="o in PURGE_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}隔離的項目</option>
          </select>
          <input class="input" v-model="purge.confirmation" :placeholder="'輸入「' + CONFIRM_TEXT + '」確認'" autocomplete="off" aria-label="確認文字">
          <button class="btn danger" type="button" :disabled="busy || purge.confirmation.trim() !== CONFIRM_TEXT" @click="doPurge">永久刪除</button>
          <button class="link" type="button" @click="purge.open = false">取消</button>
        </div>
      </div>

      <div class="table-wrap" v-if="quarantine && quarantine.entries.length">
        <table class="items">
          <thead><tr><th>原本的位置</th><th class="num">大小</th><th class="hide-sm">隔離時間</th><th></th></tr></thead>
          <tbody>
            <tr v-for="e in quarantine.entries" :key="e.entryId">
              <td class="mono plan-path"><span aria-hidden="true">{{ e.kind === 'dir' ? '📁' : '📄' }}</span> {{ e.originalPath }}</td>
              <td class="num">{{ formatBytes(e.size) }}</td>
              <td class="hide-sm nowrap">{{ formatDate(e.quarantinedUtc) }}</td>
              <td class="nowrap"><button class="btn small" type="button" :disabled="restoring === e.entryId" @click="restore(e)">{{ restoring === e.entryId ? '還原中…' : '還原' }}</button></td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-else-if="quarantine" class="muted">隔離區是空的。在掃描結果勾選項目、建立執行計畫時選「移到隔離區」，項目就會出現在這裡。</p>
    </section>

    <section class="card">
      <div class="summary-head">
        <div class="summary-title">
          <h2>操作紀錄</h2>
          <p class="muted" style="margin:0" v-if="logs">每一次執行（包含被擋下、略過、失敗的項目）都會記錄在 <span class="mono">{{ logs.directory }}</span>。</p>
        </div>
        <div class="segmented" role="group" aria-label="篩選結果">
          <button type="button" :class="{ active: logFilter === 'all' }" @click="logFilter = 'all'">全部</button>
          <button v-for="(label, key) in OUTCOME_TEXT" :key="key" type="button" :class="{ active: logFilter === key }" @click="logFilter = key">{{ label }}</button>
        </div>
      </div>
      <div class="table-wrap" v-if="shownLogs.length">
        <table class="items">
          <thead><tr><th>時間</th><th>動作</th><th>結果</th><th>項目</th><th class="num hide-sm">大小</th><th class="hide-sm">說明</th></tr></thead>
          <tbody>
            <tr v-for="(e, n) in shownLogs" :key="n">
              <td class="nowrap">{{ formatDate(e.ts) }}</td>
              <td class="nowrap">{{ e.action === 'command' && e.commandId ? e.commandId : LOG_ACTIONS[e.action] }}</td>
              <td><span class="state-badge" :class="'outcome-' + e.result">{{ OUTCOME_TEXT[e.result] }}</span></td>
              <td class="mono plan-path">{{ e.path }}</td>
              <td class="num hide-sm">{{ e.bytes ? formatBytes(e.bytes) : '' }}</td>
              <td class="hide-sm plan-reason">{{ e.error || undoText(e) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-else-if="logs" class="muted">沒有紀錄。</p>
    </section>
  `,
};
