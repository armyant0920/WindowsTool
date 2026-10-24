// 執行計畫：選擇動作 → 預覽（dry-run，不動檔案）→ 確認執行 → 結果。

import { api } from '../api.js';
import { formatBytes, formatNumber, formatDuration } from '../format.js';

const { ref, reactive, computed, onBeforeUnmount } = Vue;

const CONFIRM_TEXT = '永久刪除';
const PAGE = 200;

export const ACTION_TEXT = {
  recycle: '移到資源回收筒',
  quarantine: '移到隔離區',
  move: '搬移',
  delete: '永久刪除',
  command: '執行清理命令',
};

const STATE_TEXT = { ready: '可以執行', warning: '需要注意', blocked: '擋下', skipped: '略過' };
const OUTCOME_TEXT = { ok: '完成', failed: '失敗', skipped: '略過' };

const MODES = [
  { value: 'suggested', label: '依規則建議', help: '清理命令、隔離照規則；其他（包含未分類）一律移到資源回收筒。' },
  { value: 'recycle', label: '全部移到資源回收筒', help: '可以從 Windows 的資源回收筒還原。' },
  { value: 'quarantine', label: '全部移到隔離區', help: '在「紀錄與隔離區」頁面一鍵還原；不需要額外的磁碟空間。' },
  { value: 'move', label: '搬移到資料夾', help: '目前資料夾只能搬到同一個磁碟。' },
  { value: 'delete', label: '永久刪除', help: '無法還原，執行前要輸入確認文字。' },
];

export const PlanView = {
  name: 'PlanView',
  props: {
    scanId: { type: String, required: true },
    selection: { type: Object, required: true }, // { count, size }：建立計畫時使用掃描結果頁上的勾選
  },
  emits: ['close', 'executed', 'rescan'],
  setup(props, { emit }) {
    const step = ref('choose'); // choose | preview | running | done
    const mode = ref('suggested');
    const destination = ref('');
    const plan = ref(null);
    const execution = ref(null);
    const error = ref(null);
    const busy = ref(false);
    const confirmation = ref('');
    const filter = ref('all');
    const visible = ref(PAGE);
    const commands = reactive({});
    let poller = null;

    const items = computed(() => plan.value?.items ?? []);
    const filtered = computed(() => {
      const list = filter.value === 'all' ? items.value : items.value.filter((i) => i.state === filter.value);
      // 擋下、需要注意的排前面
      const order = { blocked: 0, warning: 1, ready: 2, skipped: 3 };
      return [...list].sort((a, b) => order[a.state] - order[b.state] || b.size - a.size);
    });
    const shown = computed(() => filtered.value.slice(0, visible.value));
    const runnable = computed(() => (plan.value ? plan.value.summary.ready + plan.value.summary.warning : 0));
    const canExecute = computed(() =>
      runnable.value > 0 && (!plan.value.summary.requiresConfirmation || confirmation.value.trim() === CONFIRM_TEXT));
    const results = computed(() => execution.value?.results ?? []);
    const failures = computed(() => results.value.filter((r) => r.outcome === 'failed'));
    const progressPercent = computed(() => {
      const e = execution.value;
      return e && e.total ? Math.round((e.done / e.total) * 100) : 0;
    });

    async function preview() {
      error.value = null;
      if (mode.value === 'move' && !destination.value.trim()) {
        error.value = '請輸入要搬到哪個資料夾。';
        return;
      }
      busy.value = true;
      try {
        const body = {
          scanId: props.scanId,
          useSelection: true,
          action: mode.value,
          destination: mode.value === 'move' ? destination.value.trim() : null,
        };
        const result = await api('/api/plans', { method: 'POST', body });
        plan.value = result.plan;
        filter.value = 'all';
        visible.value = PAGE;
        confirmation.value = '';
        step.value = 'preview';
        if (result.plan.summary.commands.length) await loadCommands();
      } catch (err) {
        error.value = err.message;
      } finally {
        busy.value = false;
      }
    }

    async function loadCommands() {
      try {
        const rules = await api('/api/rules?source=user');
        for (const c of rules.commands) commands[c.id] = c;
      } catch {
        // 只是顯示用
      }
    }

    async function execute() {
      if (!canExecute.value) return;
      error.value = null;
      busy.value = true;
      try {
        const result = await api(`/api/plans/${plan.value.planId}/execute`, {
          method: 'POST',
          body: { confirmation: confirmation.value.trim() || null },
        });
        execution.value = result.execution;
        step.value = 'running';
        poll();
      } catch (err) {
        error.value = err.message;
      } finally {
        busy.value = false;
      }
    }

    function poll() {
      clearTimeout(poller);
      poller = setTimeout(async () => {
        try {
          const result = await api(`/api/plans/${plan.value.planId}`);
          execution.value = result.execution;
          if (result.execution.status === 'running') {
            poll();
            return;
          }
          step.value = 'done';
          const ok = new Set(result.execution.results.filter((r) => r.outcome === 'ok').map((r) => r.index));
          emit('executed', plan.value.items.filter((i) => ok.has(i.index)).map((i) => i.itemId));
        } catch (err) {
          error.value = err.message;
          poll();
        }
      }, 400);
    }

    async function stop() {
      try {
        await api(`/api/plans/${plan.value.planId}/cancel`, { method: 'POST' });
      } catch (err) {
        error.value = err.message;
      }
    }

    onBeforeUnmount(() => clearTimeout(poller));

    function undoText(r) {
      if (!r.undo) return '';
      if (r.undo.type === 'recycleBin') return '可以從資源回收筒還原';
      if (r.undo.type === 'quarantine') return '可以在「紀錄與隔離區」還原';
      if (r.undo.type === 'move') return `已搬到 ${r.undo.from}`;
      return '';
    }

    return {
      step, mode, destination, plan, execution, error, busy, confirmation, filter, visible, commands,
      items, filtered, shown, runnable, canExecute, results, failures, progressPercent,
      MODES, ACTION_TEXT, STATE_TEXT, OUTCOME_TEXT, CONFIRM_TEXT, PAGE,
      preview, execute, stop, undoText, emit,
      formatBytes, formatNumber, formatDuration,
    };
  },
  template: `
    <section class="card plan-card" aria-label="執行計畫">
      <div class="summary-head">
        <h2>執行計畫</h2>
        <button v-if="step !== 'running'" class="btn small" type="button" @click="emit('close')">關閉</button>
      </div>

      <!-- 1. 選擇動作 -->
      <template v-if="step === 'choose'">
        <p class="muted" style="margin-top:0">
          已勾選 {{ formatNumber(selection.count) }} 項（{{ formatBytes(selection.size) }}）。
          先產生預覽：會重新檢查每一項（是否還存在、是否受保護、是否正在使用中），<strong>這一步不會動到任何檔案</strong>。
        </p>
        <div class="mode-list" role="radiogroup" aria-label="動作">
          <label v-for="m in MODES" :key="m.value" class="mode" :class="{ active: mode === m.value, danger: m.value === 'delete' }">
            <input type="radio" name="plan-mode" :value="m.value" v-model="mode">
            <span><strong>{{ m.label }}</strong><small class="muted">{{ m.help }}</small></span>
          </label>
        </div>
        <label v-if="mode === 'move'" class="field" style="margin-top:10px">
          <span>目的地資料夾</span>
          <input class="input mono" v-model="destination" placeholder="例如 D:\\封存" spellcheck="false">
        </label>
        <p v-if="error" class="message error">{{ error }}</p>
        <div class="editor-actions">
          <button class="btn primary" type="button" :disabled="busy" @click="preview">{{ busy ? '檢查中…' : '產生預覽' }}</button>
        </div>
      </template>

      <!-- 2. 預覽 -->
      <template v-else-if="step === 'preview' && plan">
        <dl class="stats">
          <div><dt>可以執行</dt><dd>{{ formatNumber(runnable) }} <small>項</small></dd></div>
          <div><dt>預計釋放</dt><dd>{{ formatBytes(plan.summary.bytes) }}</dd></div>
          <div v-if="plan.summary.warning"><dt>需要注意</dt><dd class="warn">{{ formatNumber(plan.summary.warning) }}</dd></div>
          <div v-if="plan.summary.blocked"><dt>擋下</dt><dd class="error-text">{{ formatNumber(plan.summary.blocked) }}</dd></div>
          <div v-if="plan.summary.skipped"><dt>略過</dt><dd class="muted">{{ formatNumber(plan.summary.skipped) }}</dd></div>
        </dl>

        <div v-if="plan.summary.commands.length" class="message info">
          會執行下列清理命令（每個只執行一次）：
          <ul class="command-list">
            <li v-for="id in plan.summary.commands" :key="id"><code>{{ commands[id] ? commands[id].display : id }}</code>
              <span class="muted" v-if="commands[id]"> — {{ commands[id].description }}</span></li>
          </ul>
        </div>

        <div class="segmented" role="group" aria-label="篩選" style="margin:10px 0">
          <button type="button" :class="{ active: filter === 'all' }" @click="filter = 'all'; visible = PAGE">全部 {{ formatNumber(items.length) }}</button>
          <button v-for="s in ['ready', 'warning', 'blocked', 'skipped']" :key="s" type="button" :class="{ active: filter === s }"
                  @click="filter = s; visible = PAGE">{{ STATE_TEXT[s] }} {{ formatNumber(items.filter(i => i.state === s).length) }}</button>
        </div>

        <div class="table-wrap plan-table">
          <table class="items">
            <thead><tr><th>狀態</th><th>動作</th><th>項目</th><th class="num">大小</th><th class="hide-sm">說明</th></tr></thead>
            <tbody>
              <tr v-for="i in shown" :key="i.index" :class="'state-' + i.state">
                <td><span class="state-badge" :class="'state-' + i.state">{{ STATE_TEXT[i.state] }}</span></td>
                <td class="nowrap">{{ i.action === 'command' ? (i.commandId || ACTION_TEXT.command) : ACTION_TEXT[i.action] }}</td>
                <td class="mono plan-path">{{ i.path }}<small v-if="i.category" class="muted"> · {{ i.category }}</small></td>
                <td class="num">{{ i.action === 'command' ? '' : formatBytes(i.size) }}</td>
                <td class="hide-sm plan-reason">{{ i.reason }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="table-foot">
          <span class="muted">顯示 {{ formatNumber(shown.length) }} / {{ formatNumber(filtered.length) }} 項</span>
          <button v-if="shown.length < filtered.length" class="btn" type="button" @click="visible += PAGE">顯示更多</button>
        </div>

        <div v-if="plan.summary.requiresConfirmation" class="message error confirm-box">
          <strong>這個計畫包含永久刪除，刪除後無法還原。</strong>
          <label class="field">
            <span>請輸入「{{ CONFIRM_TEXT }}」確認</span>
            <input class="input" v-model="confirmation" :placeholder="CONFIRM_TEXT" autocomplete="off">
          </label>
        </div>
        <p v-if="error" class="message error">{{ error }}</p>
        <div class="editor-actions">
          <button class="btn primary" :class="{ danger: plan.summary.requiresConfirmation }" type="button" :disabled="!canExecute || busy" @click="execute">
            確認執行 {{ formatNumber(runnable) }} 項
          </button>
          <button class="btn" type="button" @click="step = 'choose'">返回修改</button>
          <span v-if="runnable === 0" class="muted">沒有可以執行的項目。</span>
        </div>
      </template>

      <!-- 3. 執行中 -->
      <template v-else-if="step === 'running' && execution">
        <p><strong>執行中… {{ formatNumber(execution.done) }} / {{ formatNumber(execution.total) }}</strong></p>
        <div class="progress"><span :style="{ width: progressPercent + '%' }"></span></div>
        <p class="mono current-path muted">{{ execution.currentPath || '準備中…' }}</p>
        <div class="editor-actions">
          <button class="btn" type="button" @click="stop" title="正在處理的那一項完成後停止，其餘不會執行">停止</button>
        </div>
      </template>

      <!-- 4. 完成 -->
      <template v-else-if="step === 'done' && execution">
        <p v-if="execution.error" class="message error">執行失敗：{{ execution.error }}</p>
        <dl class="stats">
          <div><dt>完成</dt><dd>{{ formatNumber(execution.totals.ok) }}</dd></div>
          <div><dt>釋放</dt><dd>{{ formatBytes(execution.totals.bytes) }}</dd></div>
          <div v-if="execution.totals.failed"><dt>失敗</dt><dd class="error-text">{{ formatNumber(execution.totals.failed) }}</dd></div>
          <div v-if="execution.totals.skipped"><dt>略過</dt><dd class="muted">{{ formatNumber(execution.totals.skipped) }}</dd></div>
        </dl>
        <p class="muted">
          {{ execution.status === 'cancelled' ? '已停止，剩下的項目沒有執行。' : '' }}
          移到資源回收筒的項目可以從 Windows 的資源回收筒還原；移到隔離區的項目可以在「紀錄與隔離區」頁面還原。每一項都已寫入操作紀錄。
        </p>
        <template v-if="failures.length">
          <h4>失敗的項目</h4>
          <ul class="error-list mono">
            <li v-for="r in failures" :key="r.index">{{ r.path }} <span class="muted">— {{ r.message }}</span></li>
          </ul>
        </template>
        <details v-if="results.length" class="advanced">
          <summary>全部結果（{{ formatNumber(results.length) }}）</summary>
          <ul class="error-list mono">
            <li v-for="r in results" :key="r.index">
              <span class="state-badge" :class="'outcome-' + r.outcome">{{ OUTCOME_TEXT[r.outcome] }}</span>
              {{ r.path }} <span class="muted">{{ r.message || undoText(r) }}</span>
              <pre v-if="r.output" class="command-output">{{ r.output }}</pre>
            </li>
          </ul>
        </details>
        <div class="editor-actions">
          <button class="btn primary" type="button" @click="emit('rescan')">重新掃描</button>
          <button class="btn" type="button" @click="emit('close')">關閉</button>
        </div>
      </template>
    </section>
  `,
};
