// 掃描結果：摘要、樹狀目錄、可篩選排序的表格、項目詳情。

import { api } from '../api.js';
import {
  formatBytes, formatNumber, formatDate, formatDuration, formatPercent, FLAG_LABELS, extLabel,
} from '../format.js';

const { ref, reactive, computed, watch, provide, onMounted } = Vue;

const PAGE_SIZE = 200;

const MIN_SIZES = [
  { value: null, label: '不限大小' },
  { value: 1024 * 1024, label: '≥ 1 MB' },
  { value: 10 * 1024 * 1024, label: '≥ 10 MB' },
  { value: 100 * 1024 * 1024, label: '≥ 100 MB' },
  { value: 1024 * 1024 * 1024, label: '≥ 1 GB' },
];

const AGES = [
  { value: null, label: '不限時間' },
  { value: 30, label: '30 天以上未修改' },
  { value: 90, label: '90 天以上未修改' },
  { value: 180, label: '180 天以上未修改' },
  { value: 365, label: '1 年以上未修改' },
];

const KIND_ICONS = { dir: '📁', file: '📄', link: '🔗' };

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

export const ResultView = {
  name: 'ResultView',
  props: {
    scan: { type: Object, required: true },
  },
  emits: ['new-scan', 'rescan'],
  setup(props) {
    const scanId = props.scan.scanId;
    const summary = props.scan.summary;

    // ---- 樹狀目錄共用狀態 ----
    const selectedFolderId = ref(summary.rootId);
    const expandPath = ref([]);
    const rootItem = ref(null);

    // ---- 表格 ----
    const filters = reactive({ flat: false, kind: 'any', minSize: null, q: '', ext: '', olderThanDays: null });
    const sort = reactive({ by: 'size', desc: true });
    const page = reactive({ items: [], total: 0, parent: null, otherFiles: null, loading: false, error: null });

    // ---- 詳情 ----
    const folderDetail = ref(null);
    const selectedItem = ref(null);
    const itemDetail = ref(null);
    const showErrors = ref(false);
    const copied = ref(false);

    const hasFilters = computed(() =>
      filters.kind !== 'any' || filters.minSize !== null || filters.q.trim() !== '' || filters.ext.trim() !== '' || filters.olderThanDays !== null);

    const detail = computed(() => itemDetail.value ?? folderDetail.value);
    const breadcrumb = computed(() => {
      const d = folderDetail.value;
      return d ? [...d.ancestors, { id: d.item.id, name: d.item.name }] : [];
    });

    function query(offset) {
      const params = new URLSearchParams({
        parentId: selectedFolderId.value,
        sort: sort.by,
        order: sort.desc ? 'desc' : 'asc',
        offset,
        limit: PAGE_SIZE,
      });
      if (filters.flat) params.set('flat', 'true');
      if (filters.kind !== 'any') params.set('kind', filters.kind);
      if (filters.minSize !== null) params.set('minSize', filters.minSize);
      if (filters.q.trim()) params.set('q', filters.q.trim());
      if (filters.ext.trim()) params.set('ext', filters.ext.trim());
      if (filters.olderThanDays !== null) params.set('olderThanDays', filters.olderThanDays);
      return `/api/scans/${scanId}/items?${params}`;
    }

    let requestSeq = 0;
    async function loadItems(append = false) {
      const seq = ++requestSeq;
      page.loading = true;
      page.error = null;
      try {
        const result = await api(query(append ? page.items.length : 0));
        if (seq !== requestSeq) return; // 已經有更新的查詢
        page.items = append ? [...page.items, ...result.items] : result.items;
        page.total = result.total;
        page.parent = result.parent;
        page.otherFiles = result.otherFiles;
      } catch (err) {
        if (seq === requestSeq) page.error = err.message;
      } finally {
        if (seq === requestSeq) page.loading = false;
      }
    }

    async function loadFolderDetail() {
      try {
        folderDetail.value = await api(`/api/scans/${scanId}/items/${selectedFolderId.value}`);
      } catch (err) {
        page.error = err.message;
      }
    }

    async function openFolder(id) {
      selectedFolderId.value = id;
      selectedItem.value = null;
      itemDetail.value = null;
      await Promise.all([loadItems(), loadFolderDetail()]);
      // 讓樹狀目錄展開到這個資料夾
      expandPath.value = folderDetail.value ? folderDetail.value.ancestors.map((a) => a.id) : [];
    }

    async function selectItem(item) {
      selectedItem.value = item;
      try {
        itemDetail.value = await api(`/api/scans/${scanId}/items/${item.id}`);
      } catch (err) {
        page.error = err.message;
      }
    }

    function activate(item) {
      if (item.kind === 'dir') openFolder(item.id);
    }

    function sortBy(column) {
      if (sort.by === column) {
        sort.desc = !sort.desc;
      } else {
        sort.by = column;
        sort.desc = column !== 'name';
      }
    }

    function resetFilters() {
      Object.assign(filters, { kind: 'any', minSize: null, q: '', ext: '', olderThanDays: null });
    }

    async function copyPath(path) {
      try {
        await navigator.clipboard.writeText(path);
        copied.value = true;
        setTimeout(() => { copied.value = false; }, 1500);
      } catch {
        copied.value = false;
      }
    }

    provide('tree', {
      scanId,
      selectedId: selectedFolderId,
      expandPath,
      select: (item) => openFolder(item.id),
    });

    const reloadSoon = debounce(() => loadItems(), 300);
    watch(() => [filters.q, filters.ext], reloadSoon);
    watch(() => [filters.flat, filters.kind, filters.minSize, filters.olderThanDays, sort.by, sort.desc], () => loadItems());

    onMounted(async () => {
      try {
        const root = await api(`/api/scans/${scanId}/items/${summary.rootId}`);
        rootItem.value = root.item;
      } catch (err) {
        page.error = err.message;
      }
      await openFolder(summary.rootId);
    });

    function sortMark(column) {
      if (sort.by !== column) return '';
      return sort.desc ? '▼' : '▲';
    }

    function relativeParent(item) {
      const root = page.parent?.path ?? '';
      const parentPath = item.path.slice(0, item.path.length - item.name.length).replace(/[\\/]$/, '');
      return parentPath.startsWith(root) ? parentPath.slice(root.length).replace(/^[\\/]/, '') : parentPath;
    }

    return {
      scan: props.scan, summary, rootItem, filters, sort, page, detail, folderDetail, selectedItem, breadcrumb,
      hasFilters, showErrors, copied, MIN_SIZES, AGES, KIND_ICONS, FLAG_LABELS,
      openFolder, selectItem, activate, sortBy, sortMark, resetFilters, copyPath, relativeParent,
      loadMore: () => loadItems(true),
      formatBytes, formatNumber, formatDate, formatDuration, formatPercent, extLabel,
    };
  },
  template: `
    <section class="card summary-card">
      <div class="summary-head">
        <div class="summary-title">
          <h2>掃描結果</h2>
          <p class="mono path-line" :title="scan.root">{{ scan.root }}</p>
        </div>
        <div class="summary-actions">
          <button class="btn" type="button" @click="$emit('rescan')">重新掃描</button>
          <button class="btn" type="button" @click="$emit('new-scan')">新的掃描</button>
        </div>
      </div>
      <dl class="stats">
        <div><dt>總大小</dt><dd>{{ formatBytes(summary.totalSize) }}</dd></div>
        <div><dt>檔案</dt><dd>{{ formatNumber(summary.totalFiles) }}</dd></div>
        <div><dt>資料夾</dt><dd>{{ formatNumber(summary.totalDirs) }}</dd></div>
        <div><dt>耗時</dt><dd>{{ formatDuration(scan.elapsedMs) }}</dd></div>
      </dl>
      <div v-if="summary.errorCount > 0" class="message warn">
        <strong>{{ formatNumber(summary.errorCount) }} 個路徑無法讀取</strong>，大小可能被低估。
        <button class="link" type="button" @click="showErrors = !showErrors">{{ showErrors ? '隱藏清單' : '查看清單' }}</button>
        <ul v-if="showErrors" class="error-list mono">
          <li v-for="e in summary.errors" :key="e.path"><span>{{ e.path }}</span> <span class="muted">— {{ e.reason }}</span></li>
          <li v-if="summary.errors.length < summary.errorCount" class="muted">…另有 {{ formatNumber(summary.errorCount - summary.errors.length) }} 個未列出</li>
        </ul>
      </div>
    </section>

    <div class="result-layout">
      <aside class="card tree-pane" aria-label="資料夾樹狀結構">
        <ul v-if="rootItem" class="tree" role="tree">
          <tree-node :item="rootItem" :initially-expanded="true" />
        </ul>
        <p v-else class="muted">載入中…</p>
      </aside>

      <section class="card table-pane">
        <nav class="breadcrumb" aria-label="目前位置">
          <template v-for="(crumb, i) in breadcrumb" :key="crumb.id">
            <span v-if="i > 0" class="sep">›</span>
            <button v-if="i < breadcrumb.length - 1" type="button" class="link" @click="openFolder(crumb.id)">{{ crumb.name }}</button>
            <strong v-else>{{ crumb.name }}</strong>
          </template>
        </nav>

        <div class="toolbar">
          <div class="segmented" role="group" aria-label="範圍">
            <button type="button" :class="{ active: !filters.flat }" @click="filters.flat = false">這一層</button>
            <button type="button" :class="{ active: filters.flat }" @click="filters.flat = true">包含所有子資料夾</button>
          </div>
          <select class="input" v-model="filters.kind" aria-label="類型">
            <option value="any">資料夾和檔案</option>
            <option value="dir">只看資料夾</option>
            <option value="file">只看檔案</option>
          </select>
          <select class="input" v-model="filters.minSize" aria-label="大小">
            <option v-for="o in MIN_SIZES" :key="String(o.value)" :value="o.value">{{ o.label }}</option>
          </select>
          <select class="input" v-model="filters.olderThanDays" aria-label="修改時間">
            <option v-for="o in AGES" :key="String(o.value)" :value="o.value">{{ o.label }}</option>
          </select>
          <input class="input grow" v-model="filters.q" placeholder="名稱包含…" aria-label="名稱包含" spellcheck="false">
          <input class="input ext-input" v-model="filters.ext" placeholder="副檔名，如 mp4 iso" aria-label="副檔名" spellcheck="false">
          <button v-if="hasFilters" class="link" type="button" @click="resetFilters">清除條件</button>
        </div>

        <p v-if="page.error" class="message error">{{ page.error }}</p>

        <div class="table-wrap">
          <table class="items">
            <thead>
              <tr>
                <th><button type="button" class="th-btn" @click="sortBy('name')">名稱 {{ sortMark('name') }}</button></th>
                <th class="num"><button type="button" class="th-btn" @click="sortBy('size')">大小 {{ sortMark('size') }}</button></th>
                <th class="share">佔比</th>
                <th class="num hide-sm"><button type="button" class="th-btn" @click="sortBy('files')">檔案數 {{ sortMark('files') }}</button></th>
                <th class="hide-sm"><button type="button" class="th-btn" @click="sortBy('modified')">最後修改 {{ sortMark('modified') }}</button></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in page.items" :key="item.id" :class="{ selected: selectedItem && selectedItem.id === item.id }"
                  @click="selectItem(item)" @dblclick="activate(item)" tabindex="0" @keydown.enter="activate(item)">
                <td class="name-cell">
                  <span class="kind-icon" aria-hidden="true">{{ KIND_ICONS[item.kind] }}</span>
                  <span class="name-text">
                    <span :title="item.path">{{ item.name }}</span>
                    <small v-if="filters.flat" class="muted rel-path">{{ relativeParent(item) || '（這一層）' }}</small>
                  </span>
                  <span v-for="f in item.flags" :key="f" class="badge" :class="'badge-' + f" :title="FLAG_LABELS[f]?.text">{{ FLAG_LABELS[f]?.short ?? f }}</span>
                  <span v-if="item.kind === 'dir' && item.deniedCount > 0 && !item.flags.includes('accessDenied')" class="badge badge-accessDenied"
                        title="這個資料夾裡有無法讀取的路徑，大小可能被低估">含無法讀取</span>
                </td>
                <td class="num">{{ formatBytes(item.size) }}</td>
                <td class="share">
                  <span class="share-bar" aria-hidden="true"><span :style="{ width: page.parent && page.parent.size ? (item.size / page.parent.size * 100).toFixed(1) + '%' : '0' }"></span></span>
                  <span class="muted">{{ formatPercent(item.size, page.parent && page.parent.size) }}</span>
                </td>
                <td class="num hide-sm">{{ item.kind === 'dir' ? formatNumber(item.fileCount) : '' }}</td>
                <td class="hide-sm nowrap">{{ formatDate(item.newestWriteUtc) }}</td>
              </tr>
              <tr v-if="!filters.flat && !hasFilters && page.otherFiles" class="other-row">
                <td class="name-cell"><span class="kind-icon" aria-hidden="true">…</span> 其他 {{ formatNumber(page.otherFiles.count) }} 個小檔案</td>
                <td class="num">{{ formatBytes(page.otherFiles.size) }}</td>
                <td class="share"><span class="muted">{{ formatPercent(page.otherFiles.size, page.parent && page.parent.size) }}</span></td>
                <td class="hide-sm"></td>
                <td class="hide-sm"></td>
              </tr>
              <tr v-if="!page.loading && page.items.length === 0 && !page.otherFiles">
                <td colspan="5" class="empty muted">{{ hasFilters ? '沒有符合條件的項目。' : '這個資料夾是空的。' }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="table-foot">
          <span class="muted">{{ page.loading ? '載入中…' : '共 ' + formatNumber(page.total) + ' 項' + (page.items.length < page.total ? '，已顯示 ' + formatNumber(page.items.length) + ' 項' : '') }}</span>
          <button v-if="page.items.length < page.total" class="btn" type="button" @click="loadMore" :disabled="page.loading">顯示更多</button>
        </div>
        <p class="hint muted">按一下查看詳情；按兩下資料夾可以進入。</p>
      </section>

      <aside class="card detail-pane" aria-label="項目詳情">
        <template v-if="detail">
          <h3 class="detail-name"><span aria-hidden="true">{{ KIND_ICONS[detail.item.kind] }}</span> {{ detail.item.name }}</h3>
          <p class="mono detail-path">{{ detail.item.path }}</p>
          <button class="btn small" type="button" @click="copyPath(detail.item.path)">{{ copied ? '已複製' : '複製路徑' }}</button>

          <dl class="detail-list">
            <dt>大小</dt><dd>{{ formatBytes(detail.item.size) }} <small class="muted">（{{ formatNumber(detail.item.size) }} bytes）</small></dd>
            <template v-if="detail.item.kind === 'dir'">
              <dt>檔案數</dt><dd>{{ formatNumber(detail.item.fileCount) }}</dd>
              <dt>子資料夾</dt><dd>{{ formatNumber(detail.item.dirCount) }}</dd>
              <dt>最新修改</dt><dd>{{ formatDate(detail.item.newestWriteUtc) }} <small class="muted">（整個資料夾內）</small></dd>
            </template>
            <dt>修改時間</dt><dd>{{ formatDate(detail.item.lastWriteUtc) }}</dd>
          </dl>

          <ul v-if="detail.item.flags.length || detail.item.deniedCount" class="flag-notes">
            <li v-for="f in detail.item.flags" :key="f"><span class="badge" :class="'badge-' + f">{{ FLAG_LABELS[f]?.short ?? f }}</span> {{ FLAG_LABELS[f]?.text }}</li>
            <li v-if="detail.item.deniedCount > 0 && !detail.item.flags.includes('accessDenied')">
              <span class="badge badge-accessDenied">含無法讀取</span> 裡面有 {{ formatNumber(detail.item.deniedCount) }} 個無法讀取的資料夾，大小可能被低估。
            </li>
          </ul>

          <template v-if="detail.extStats.length">
            <h4>檔案類型</h4>
            <table class="ext-table">
              <tbody>
                <tr v-for="e in detail.extStats" :key="e.ext">
                  <td class="mono">{{ extLabel(e.ext) }}</td>
                  <td class="num">{{ formatBytes(e.size) }}</td>
                  <td class="share"><span class="share-bar"><span :style="{ width: detail.item.size ? (e.size / detail.item.size * 100).toFixed(1) + '%' : '0' }"></span></span></td>
                  <td class="num muted">{{ formatNumber(e.count) }} 個</td>
                </tr>
              </tbody>
            </table>
            <small class="muted">由各資料夾的統計加總，數字為近似值。</small>
          </template>
        </template>
        <p v-else class="muted">選擇一個項目查看詳情。</p>
      </aside>
    </div>
  `,
};
