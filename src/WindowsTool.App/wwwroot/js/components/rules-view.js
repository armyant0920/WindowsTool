// 規則管理：列表、啟用 / 停用、新增與編輯（表單或 JSON）、驗證與試套用、匯入 / 匯出。

import { api, download } from '../api.js';
import { formatBytes, formatNumber, RISK_LABELS, ACTION_LABELS, SOURCE_LABELS, KIND_LABELS } from '../format.js';

const { ref, reactive, computed, watch, onMounted } = Vue;

const KIND_ORDER = ['protect', 'caution', 'classify'];
const GROUP_TITLES = {
  protect: '保護規則：命中的項目（含裡面所有東西）不可勾選、不可執行',
  caution: '提醒規則：不保護，但風險至少提高到指定等級，並取消預設勾選',
  classify: '分類規則：標註類別、風險與建議做法',
};

function lines(text) {
  return text.split(/\r?\n|;/).map((s) => s.trim()).filter(Boolean);
}

function words(text) {
  return text.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean);
}

function emptyDraft() {
  return {
    id: '', name: '', kind: 'classify', enabled: true, platforms: '',
    scopeType: 'global', scopeRoot: '',
    matchKind: 'any', pathGlob: '', excludeGlob: '', nameRegex: '', ext: '', minSizeMb: '', minAgeDays: '',
    siblingExists: '', childExists: '',
    category: '', risk: 'low', action: 'review', method: '', reason: '', autoSelect: false,
  };
}

/** 規則 JSON → 表單欄位。 */
function toDraft(rule) {
  const m = rule.match ?? {};
  const r = rule.result ?? {};
  return {
    id: rule.id ?? '', name: rule.name ?? '', kind: rule.kind ?? 'classify', enabled: rule.enabled !== false,
    platforms: (rule.platforms ?? []).join(', '),
    scopeType: rule.scope?.type ?? 'global', scopeRoot: rule.scope?.root ?? '',
    matchKind: m.kind ?? 'any',
    pathGlob: (m.pathGlob ?? []).join('\n'), excludeGlob: (m.excludeGlob ?? []).join('\n'),
    nameRegex: m.nameRegex ?? '', ext: (m.ext ?? []).join(' '),
    minSizeMb: m.minSize != null ? String(+(m.minSize / 1024 / 1024).toFixed(3)) : '',
    minAgeDays: m.minAgeDays != null ? String(m.minAgeDays) : '',
    siblingExists: (m.siblingExists ?? []).join(' '), childExists: (m.childExists ?? []).join(' '),
    category: r.category ?? '', risk: r.risk ?? (rule.kind === 'caution' ? 'high' : 'low'),
    action: r.suggestedAction ?? 'review', method: r.method ?? '', reason: r.reason ?? '', autoSelect: !!r.autoSelect,
  };
}

/** 表單欄位 → 規則 JSON（空白欄位不送）。 */
function toRule(d, base) {
  const list = (v) => { const a = lines(v); return a.length ? a : null; };
  const wordList = (v) => { const a = words(v); return a.length ? a : null; };
  const number = (v) => (String(v).trim() === '' ? null : Number(v));
  const mb = number(d.minSizeMb);

  const rule = {
    id: d.id.trim(),
    name: d.name.trim(),
    source: base?.source === 'ai' ? 'ai' : 'user',
    enabled: d.enabled,
    kind: d.kind,
    platforms: wordList(d.platforms),
    scope: d.scopeType === 'location' ? { type: 'location', root: d.scopeRoot.trim() } : { type: 'global' },
    match: {
      kind: d.matchKind,
      pathGlob: list(d.pathGlob),
      excludeGlob: list(d.excludeGlob),
      nameRegex: d.nameRegex.trim() || null,
      ext: wordList(d.ext),
      minSize: mb == null ? null : Math.round(mb * 1024 * 1024),
      minAgeDays: number(d.minAgeDays),
      siblingExists: wordList(d.siblingExists),
      childExists: wordList(d.childExists),
    },
    result: d.kind === 'protect'
      ? { category: d.category.trim() || null, reason: d.reason.trim() || null }
      : {
        category: d.category.trim() || null,
        risk: d.risk,
        suggestedAction: d.kind === 'caution' ? 'review' : d.action,
        method: d.kind === 'classify' && d.action === 'command' ? d.method || null : null,
        reason: d.reason.trim() || null,
        autoSelect: d.kind === 'classify' && d.autoSelect,
      },
  };
  if (base?.meta) rule.meta = base.meta;
  return stripNulls(rule);
}

function stripNulls(value) {
  if (Array.isArray(value)) return value.map(stripNulls);
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) {
      if (v !== null && v !== undefined) out[k] = stripNulls(v);
    }
    return out;
  }
  return value;
}

function globsOf(rule) {
  return (rule.match?.pathGlob ?? []).join('；');
}

export const RulesView = {
  name: 'RulesView',
  props: {
    scanId: { type: String, default: null },
    scanRoot: { type: String, default: null },
    prefill: { type: Object, default: null },
  },
  emits: ['changed', 'prefill-used'],
  setup(props, { emit }) {
    const data = ref(null);
    const loadError = ref(null);
    const notice = ref(null);
    const filters = reactive({ q: '', kind: '', source: '', allPlatforms: false });

    const selectedId = ref(null);
    const editing = ref(false); // true：編輯器開啟
    const isNew = ref(false);
    const baseRule = ref(null); // 編輯中規則的原始內容（保留 meta / source）
    const draft = reactive(emptyDraft());
    const mode = ref('form'); // form | json
    const jsonText = ref('');
    const jsonError = ref(null);
    const validation = ref(null);
    const busy = ref(false);
    const confirmDelete = ref(false);
    const importResult = ref(null);
    const fileInput = ref(null);

    const rules = computed(() => data.value?.rules ?? []);
    const commands = computed(() => data.value?.commands ?? []);
    const selected = computed(() => rules.value.find((r) => r.rule.id === selectedId.value) ?? null);
    const readOnly = computed(() => !isNew.value && selected.value && !selected.value.editable);

    const groups = computed(() => {
      const q = filters.q.trim().toLowerCase();
      const visible = rules.value.filter((info) => {
        const r = info.rule;
        if (!filters.allPlatforms && r.platforms?.length && !r.platforms.includes(data.value.platform)) return false;
        if (filters.kind && r.kind !== filters.kind) return false;
        if (filters.source && r.source !== filters.source) return false;
        if (!q) return true;
        return [r.id, r.name, r.result?.category, globsOf(r)].some((v) => v && v.toLowerCase().includes(q));
      });
      return KIND_ORDER
        .map((kind) => ({ kind, title: GROUP_TITLES[kind], items: visible.filter((i) => i.rule.kind === kind) }))
        .filter((g) => g.items.length > 0);
    });

    const counts = computed(() => ({
      total: rules.value.length,
      active: rules.value.filter((r) => r.active).length,
      user: rules.value.filter((r) => r.rule.source !== 'builtin').length,
    }));

    async function load() {
      try {
        data.value = await api('/api/rules');
        loadError.value = null;
      } catch (err) {
        loadError.value = err.message;
      }
    }

    function changed(text) {
      notice.value = text ? { kind: 'info', text } : null;
      emit('changed');
      return load();
    }

    function select(info) {
      selectedId.value = info.rule.id;
      isNew.value = false;
      openEditor(info.rule);
    }

    function openEditor(rule) {
      baseRule.value = rule;
      Object.assign(draft, rule ? toDraft(rule) : emptyDraft());
      jsonText.value = JSON.stringify(rule ?? toRule(draft, null), null, 2);
      jsonError.value = null;
      validation.value = null;
      confirmDelete.value = false;
      editing.value = true;
    }

    function newRule(template) {
      selectedId.value = null;
      isNew.value = true;
      openEditor(template ?? null);
      if (!template) Object.assign(draft, emptyDraft());
    }

    /** 內建規則複製成自訂規則。 */
    function duplicate() {
      const source = baseRule.value;
      if (!source) return;
      const copy = JSON.parse(JSON.stringify(source));
      copy.id = '';
      copy.name = `${source.name}（自訂）`;
      copy.source = 'user';
      delete copy.meta;
      newRule(copy);
    }

    function closeEditor() {
      editing.value = false;
      selectedId.value = null;
      validation.value = null;
    }

    function currentRule() {
      if (mode.value === 'json') {
        try {
          jsonError.value = null;
          return JSON.parse(jsonText.value);
        } catch (err) {
          jsonError.value = `JSON 格式錯誤：${err.message}`;
          return null;
        }
      }
      return toRule(draft, baseRule.value);
    }

    function switchMode(next) {
      if (next === mode.value) return;
      if (next === 'json') {
        jsonText.value = JSON.stringify(toRule(draft, baseRule.value), null, 2);
        jsonError.value = null;
      } else {
        const rule = currentRule();
        if (!rule) return; // 留在 JSON 模式修正錯誤
        Object.assign(draft, toDraft(rule));
      }
      mode.value = next;
    }

    async function validate() {
      const rule = currentRule();
      if (!rule) return;
      busy.value = true;
      try {
        validation.value = await api('/api/rules/validate', { method: 'POST', body: { rule, scanId: props.scanId } });
      } catch (err) {
        validation.value = { valid: false, issues: [{ field: '', message: err.message, isError: true }] };
      } finally {
        busy.value = false;
      }
    }

    async function save() {
      const rule = currentRule();
      if (!rule) return;
      busy.value = true;
      try {
        const path = isNew.value ? '/api/rules' : `/api/rules/${encodeURIComponent(selectedId.value)}`;
        const result = await api(path, { method: isNew.value ? 'POST' : 'PUT', body: { rule, scanId: props.scanId } });
        validation.value = result.validation;
        isNew.value = false;
        selectedId.value = result.rule.rule.id;
        baseRule.value = result.rule.rule;
        Object.assign(draft, toDraft(result.rule.rule));
        jsonText.value = JSON.stringify(result.rule.rule, null, 2);
        await changed(`已儲存「${result.rule.rule.name}」。`);
      } catch (err) {
        // 驗證失敗時伺服器回傳 ValidationResponse；其他錯誤只有訊息
        validation.value = err.body?.issues ? err.body : { valid: false, issues: [{ field: '', message: err.message, isError: true }] };
      } finally {
        busy.value = false;
      }
    }

    async function remove() {
      if (!selectedId.value) return;
      busy.value = true;
      try {
        await api(`/api/rules/${encodeURIComponent(selectedId.value)}`, { method: 'DELETE' });
        const name = draft.name;
        closeEditor();
        await changed(`已刪除「${name}」。`);
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      } finally {
        busy.value = false;
        confirmDelete.value = false;
      }
    }

    async function setEnabled(info, enabled) {
      try {
        await api(`/api/rules/${encodeURIComponent(info.rule.id)}/enabled`, { method: 'PUT', body: { enabled } });
        await changed(null);
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
        await load();
      }
    }

    function addSuggestedExcludes() {
      const suggestions = validation.value?.dryRun?.suggestedExcludes ?? [];
      if (mode.value === 'json') switchMode('form');
      const existing = lines(draft.excludeGlob);
      draft.excludeGlob = [...existing, ...suggestions.filter((s) => !existing.includes(s))].join('\n');
      validate();
    }

    async function exportRules() {
      try {
        await download('/api/rules/export');
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      }
    }

    async function importFile(event) {
      const file = event.target.files?.[0];
      event.target.value = '';
      if (!file) return;
      importResult.value = null;
      let parsed;
      try {
        // 允許 // 註解（規則檔的寫法）
        parsed = JSON.parse((await file.text()).replace(/^\s*\/\/.*$/gm, ''));
      } catch (err) {
        notice.value = { kind: 'error', text: `無法讀取 ${file.name}：${err.message}` };
        return;
      }
      try {
        const result = await api('/api/rules/import', { method: 'POST', body: parsed });
        await changed(`已匯入 ${result.imported} 條規則${result.disabledBuiltin ? `，停用 ${result.disabledBuiltin} 條內建規則` : ''}。`);
      } catch (err) {
        if (err.body?.problems) {
          importResult.value = err.body.problems;
          notice.value = { kind: 'error', text: '匯入失敗：以下規則沒有通過檢查，所有規則都沒有寫入。' };
        } else {
          notice.value = { kind: 'error', text: err.message };
        }
      }
    }

    async function reload() {
      try {
        data.value = await api('/api/rules/reload', { method: 'POST' });
        await changed('已重新讀取規則檔。');
      } catch (err) {
        notice.value = { kind: 'error', text: err.message };
      }
    }

    /** 把路徑開頭換成 {UserProfile} 這類變數（取最長的），規則換一台電腦也能用。 */
    function tokenize(path) {
      const normalized = path.replace(/\\/g, '/');
      const lower = normalized.toLowerCase();
      let best = null;
      for (const [name, value] of Object.entries(data.value?.tokens ?? {})) {
        if (!value || name === 'Temp' || name === 'SystemDrive') continue;
        const v = value.replace(/\\/g, '/');
        const vl = v.toLowerCase();
        if ((lower === vl || lower.startsWith(vl + '/')) && (!best || v.length > best.value.length)) {
          best = { name, value: v };
        }
      }
      return best ? `{${best.name}}${normalized.slice(best.value.length)}` : normalized;
    }

    /** 從掃描結果的項目建立規則。 */
    function applyPrefill(p) {
      if (!p) return;
      const glob = tokenize(p.path);
      newRule({
        id: '',
        name: p.name,
        kind: 'classify',
        match: { kind: p.kind === 'link' ? 'any' : p.kind, pathGlob: [glob] },
        result: { category: '', risk: 'low', suggestedAction: 'review' },
      });
      emit('prefill-used');
    }

    watch(() => props.prefill, applyPrefill);
    onMounted(async () => {
      await load();
      applyPrefill(props.prefill);
    });

    function riskClass(risk) {
      return 'risk risk-' + (risk || 'unknown');
    }

    function issueField(issue) {
      return issue.field ? issue.field : '';
    }

    return {
      data, loadError, notice, filters, groups, counts, selectedId, selected, editing, isNew, readOnly, draft, mode,
      jsonText, jsonError, validation, busy, confirmDelete, importResult, fileInput, commands,
      RISK_LABELS, ACTION_LABELS, SOURCE_LABELS, KIND_LABELS,
      select, newRule, duplicate, closeEditor, switchMode, validate, save, remove, setEnabled, addSuggestedExcludes,
      exportRules, importFile, reload, riskClass, issueField, globsOf, formatBytes, formatNumber,
    };
  },
  template: `
    <section class="card">
      <div class="summary-head">
        <div class="summary-title">
          <h2>規則</h2>
          <p class="muted" style="margin:0" v-if="data">
            共 {{ formatNumber(counts.total) }} 條，啟用中 {{ formatNumber(counts.active) }} 條，自訂 {{ formatNumber(counts.user) }} 條。
            自訂規則存在 <span class="mono">{{ data.filePath }}</span>{{ data.portable ? '（可攜模式）' : '' }}
          </p>
        </div>
        <div class="summary-actions">
          <button class="btn primary" type="button" @click="newRule()">新增規則</button>
          <button class="btn" type="button" @click="fileInput.click()">匯入</button>
          <button class="btn" type="button" @click="exportRules">匯出</button>
          <button class="btn" type="button" @click="reload" title="在外部編輯過規則檔後，重新讀取">重新讀取</button>
          <input ref="fileInput" type="file" accept=".json,application/json" hidden @change="importFile">
        </div>
      </div>
      <p v-if="loadError" class="message error">{{ loadError }}</p>
      <p v-if="data && data.loadError" class="message error">{{ data.loadError }}。修正或移除這個檔案之前，無法新增或修改規則。</p>
      <ul v-if="data && data.loadWarnings.length" class="issues">
        <li v-for="w in data.loadWarnings" :key="w" class="warning">{{ w }}</li>
      </ul>
      <p v-if="notice" class="message" :class="notice.kind">{{ notice.text }}</p>
      <ul v-if="importResult" class="issues">
        <li v-for="p in importResult" :key="p.ruleId" class="error">
          <strong>{{ p.ruleId || '（沒有 id）' }}</strong>：{{ p.issues.map(i => i.message).join('；') }}
        </li>
      </ul>
    </section>

    <div class="rules-layout">
      <section class="card">
        <div class="toolbar">
          <input class="input grow" v-model="filters.q" placeholder="搜尋名稱、id、類別或路徑樣式…" aria-label="搜尋規則" spellcheck="false">
          <select class="input" v-model="filters.kind" aria-label="種類">
            <option value="">所有種類</option>
            <option value="protect">保護</option>
            <option value="caution">提醒</option>
            <option value="classify">分類</option>
          </select>
          <select class="input" v-model="filters.source" aria-label="來源">
            <option value="">所有來源</option>
            <option value="builtin">內建</option>
            <option value="user">自訂</option>
            <option value="ai">AI</option>
          </select>
          <label class="check"><input type="checkbox" v-model="filters.allPlatforms"> 顯示其他作業系統的規則</label>
        </div>

        <p v-if="!data && !loadError" class="muted">載入中…</p>
        <div class="table-wrap" v-if="data">
          <table class="rule-list">
            <thead>
              <tr>
                <th style="width:44px">啟用</th>
                <th>名稱</th>
                <th>結果</th>
                <th class="hide-sm">路徑樣式</th>
                <th>來源</th>
              </tr>
            </thead>
            <tbody>
              <template v-for="g in groups" :key="g.kind">
                <tr class="rule-group"><td colspan="5">{{ g.title }}</td></tr>
                <tr v-for="info in g.items" :key="info.rule.id" :class="{ selected: selectedId === info.rule.id, inactive: !info.active }"
                    @click="select(info)" tabindex="0" @keydown.enter="select(info)">
                  <td @click.stop>
                    <label class="switch" :title="info.canDisable ? (info.rule.enabled ? '停用' : '啟用') : '內建的保護規則不能停用'">
                      <input type="checkbox" :checked="info.rule.enabled" :disabled="!info.canDisable"
                             @change="setEnabled(info, $event.target.checked)" :aria-label="'啟用 ' + info.rule.name">
                      <span></span>
                    </label>
                  </td>
                  <td>
                    <div>{{ info.rule.name }}</div>
                    <small class="muted mono">{{ info.rule.id }}</small>
                    <small v-if="info.rule.enabled && !info.active" class="muted">（這個平台上沒有作用）</small>
                    <small v-if="info.issues.some(i => i.isError)" class="error-text">（有錯誤）</small>
                  </td>
                  <td>
                    <span v-if="info.rule.kind === 'protect'" class="risk risk-protected"><span class="risk-dot"></span><span>{{ info.rule.result.category || '受保護' }}</span></span>
                    <span v-else :class="riskClass(info.rule.result.risk)"><span class="risk-dot"></span><span>{{ info.rule.result.category || '需要注意' }}</span></span>
                    <div v-if="info.rule.kind === 'classify'"><small class="muted">{{ ACTION_LABELS[info.rule.result.suggestedAction] }}{{ info.rule.result.autoSelect ? '・預設勾選' : '' }}</small></div>
                  </td>
                  <td class="globs mono hide-sm">{{ globsOf(info.rule) }}</td>
                  <td><span class="badge">{{ SOURCE_LABELS[info.rule.source] }}</span></td>
                </tr>
              </template>
              <tr v-if="groups.length === 0"><td colspan="5" class="empty muted">沒有符合的規則。</td></tr>
            </tbody>
          </table>
        </div>
      </section>

      <aside class="card editor" aria-label="規則編輯">
        <template v-if="editing">
          <div class="summary-head">
            <h3>{{ isNew ? '新增規則' : readOnly ? '內建規則（唯讀）' : '編輯規則' }}</h3>
            <div class="segmented" role="group" aria-label="編輯方式">
              <button type="button" :class="{ active: mode === 'form' }" @click="switchMode('form')">表單</button>
              <button type="button" :class="{ active: mode === 'json' }" @click="switchMode('json')">JSON</button>
            </div>
          </div>
          <p v-if="readOnly" class="message info">內建規則不能修改{{ selected && selected.canDisable ? '，但可以停用' : '' }}；可以複製成自訂規則後再調整。
            <button class="link" type="button" @click="duplicate">複製成自訂規則</button></p>

          <fieldset :disabled="readOnly || busy" style="border:0;padding:0;margin:0">
            <div v-if="mode === 'form'" class="editor-grid">
              <label class="field"><span>名稱</span><input class="input" v-model="draft.name" maxlength="100"></label>
              <label class="field"><span>id <small class="muted">（留白自動配發）</small></span><input class="input mono" v-model="draft.id" :readonly="!isNew" spellcheck="false"></label>
              <label class="field"><span>種類</span>
                <select class="input" v-model="draft.kind">
                  <option value="classify">分類</option>
                  <option value="caution">提醒（提高風險下限）</option>
                  <option value="protect">保護（不可勾選）</option>
                </select>
              </label>
              <label class="field"><span>套用範圍</span>
                <select class="input" v-model="draft.scopeType">
                  <option value="global">任何位置</option>
                  <option value="location">只在指定資料夾</option>
                </select>
              </label>
              <label v-if="draft.scopeType === 'location'" class="field wide"><span>資料夾</span><input class="input mono" v-model="draft.scopeRoot" placeholder="例如 D:/projects" spellcheck="false"></label>

              <label class="field wide"><span>路徑樣式（一行一個，符合任一即可）</span>
                <textarea class="input" rows="3" v-model="draft.pathGlob" spellcheck="false" placeholder="**/node_modules&#10;{LocalAppData}/Temp/**"></textarea>
                <span class="help">* 比對一段名稱、** 比對任意層資料夾；結尾 /** 代表「裡面所有東西」。可用 {UserProfile}、{LocalAppData}、{Windows} 等變數。沒有寫完整路徑時代表「任何位置」。</span>
              </label>
              <label class="field"><span>類型</span>
                <select class="input" v-model="draft.matchKind">
                  <option value="any">資料夾和檔案</option>
                  <option value="dir">只有資料夾</option>
                  <option value="file">只有檔案</option>
                </select>
              </label>
              <label class="field"><span>副檔名 <small class="muted">（空白分隔）</small></span><input class="input" v-model="draft.ext" placeholder="log tmp" spellcheck="false"></label>
              <label class="field"><span>最小大小（MB）</span><input class="input" type="number" min="0" step="any" v-model="draft.minSizeMb"></label>
              <label class="field"><span>至少幾天沒有修改</span><input class="input" type="number" min="0" step="1" v-model="draft.minAgeDays"></label>
              <label class="field"><span>同一層要有 <small class="muted">（例 package.json）</small></span><input class="input mono" v-model="draft.siblingExists" spellcheck="false"></label>
              <label class="field"><span>資料夾內要有 <small class="muted">（例 .git）</small></span><input class="input mono" v-model="draft.childExists" spellcheck="false"></label>
              <label class="field"><span>名稱正規表示式</span><input class="input mono" v-model="draft.nameRegex" spellcheck="false"></label>
              <label class="field"><span>平台 <small class="muted">（空白 = 全部）</small></span><input class="input" v-model="draft.platforms" placeholder="windows linux macos" spellcheck="false"></label>
              <label class="field wide"><span>排除樣式（一行一個）</span><textarea class="input" rows="2" v-model="draft.excludeGlob" spellcheck="false" placeholder="**/.git/**"></textarea></label>

              <label class="field"><span>類別</span><input class="input" v-model="draft.category" maxlength="50" :placeholder="draft.kind === 'protect' ? '受保護' : '例如：暫存檔'"></label>
              <label v-if="draft.kind !== 'protect'" class="field"><span>{{ draft.kind === 'caution' ? '風險至少' : '風險' }}</span>
                <select class="input" v-model="draft.risk">
                  <option v-if="draft.kind === 'classify'" value="low">低</option>
                  <option value="medium">中</option>
                  <option value="high">高</option>
                </select>
              </label>
              <template v-if="draft.kind === 'classify'">
                <label class="field"><span>建議動作</span>
                  <select class="input" v-model="draft.action">
                    <option v-for="(label, key) in ACTION_LABELS" :key="key" :value="key">{{ label }}</option>
                  </select>
                </label>
                <label v-if="draft.action === 'command'" class="field"><span>清理命令</span>
                  <select class="input" v-model="draft.method">
                    <option value="">（選擇白名單命令）</option>
                    <option v-for="c in commands" :key="c.id" :value="c.id">{{ c.id }} — {{ c.description }}</option>
                  </select>
                </label>
                <label class="check wide"><input type="checkbox" v-model="draft.autoSelect"> 預設勾選（只適用低 / 中風險、非永久刪除）</label>
              </template>
              <label class="field wide"><span>說明</span><textarea class="input" rows="2" v-model="draft.reason" maxlength="500"></textarea></label>
            </div>
            <div v-else>
              <textarea class="input" rows="22" v-model="jsonText" spellcheck="false" aria-label="規則 JSON"></textarea>
              <p v-if="jsonError" class="message error">{{ jsonError }}</p>
            </div>
          </fieldset>

          <div class="editor-actions">
            <template v-if="!readOnly">
              <button class="btn primary" type="button" :disabled="busy" @click="save">儲存</button>
              <button class="btn" type="button" :disabled="busy" @click="validate">檢查並試套用</button>
            </template>
            <button v-else class="btn" type="button" :disabled="busy" @click="validate">試套用</button>
            <button class="btn" type="button" @click="closeEditor">關閉</button>
            <template v-if="!isNew && !readOnly">
              <button v-if="!confirmDelete" class="link" type="button" style="margin-left:auto;color:var(--error)" @click="confirmDelete = true">刪除…</button>
              <span v-else style="margin-left:auto">確定刪除？
                <button class="btn small" type="button" @click="remove">刪除</button>
                <button class="link" type="button" @click="confirmDelete = false">取消</button>
              </span>
            </template>
          </div>
          <p class="help" style="margin-top:8px">
            {{ scanId ? '試套用的對象：' + (scanRoot || '目前的掃描結果') : '目前沒有掃描結果，只會檢查格式與保護路徑；先到「磁碟掃描」掃描一次，就能看到命中的項目。' }}
          </p>

          <template v-if="validation">
            <p class="message" :class="validation.valid ? 'info' : 'error'">{{ validation.valid ? '檢查通過。' : '沒有通過檢查，不能儲存。' }}</p>
            <ul v-if="validation.issues.length" class="issues">
              <li v-for="(i, n) in validation.issues" :key="n" :class="i.isError ? 'error' : 'warning'">
                <code v-if="issueField(i)">{{ i.field }}</code> {{ i.message }}
              </li>
            </ul>
            <div v-if="validation.dryRun" class="dry-run">
              <strong>試套用：</strong>命中 {{ formatNumber(validation.dryRun.count) }} 項，約 {{ formatBytes(validation.dryRun.size) }}
              <template v-if="validation.dryRun.conflictCount">，其中 <span class="error-text">{{ formatNumber(validation.dryRun.conflictCount) }} 項受保護</span></template>
              <div v-if="validation.dryRun.suggestedExcludes.length" style="margin-top:6px">
                <button class="btn small" type="button" @click="addSuggestedExcludes">加入排除：{{ validation.dryRun.suggestedExcludes.join('、') }}</button>
              </div>
              <ul v-if="validation.dryRun.conflicts.length">
                <li v-for="c in validation.dryRun.conflicts" :key="'c' + c.itemId" class="error-text"><span class="mono">{{ c.path }}</span> — {{ c.note }}</li>
              </ul>
              <ul v-if="validation.dryRun.samples.length">
                <li v-for="s in validation.dryRun.samples" :key="s.itemId"><span class="mono">{{ s.path }}</span> <span class="muted">{{ formatBytes(s.size) }}</span></li>
              </ul>
            </div>
          </template>
        </template>
        <div v-else class="muted">
          <p style="margin-top:0">選擇左邊的規則查看內容，或按「新增規則」。</p>
          <p>規則的優先順序：保護規則 → 自訂 → AI → 內建；同一層級取條件最具體的那條。保護規則永遠優先，任何規則都不能讓受保護的項目被勾選。</p>
        </div>
      </aside>
    </div>
  `,
};
