// 左側樹狀目錄：子資料夾在展開時才載入，依大小排序。

import { api } from '../api.js';
import { formatBytes } from '../format.js';

const { ref, inject, computed, watch } = Vue;

export const TreeNode = {
  name: 'TreeNode',
  props: {
    item: { type: Object, required: true },
    parentSize: { type: Number, default: 0 },
    depth: { type: Number, default: 0 },
    initiallyExpanded: { type: Boolean, default: false },
  },
  setup(props) {
    const tree = inject('tree');
    const expanded = ref(false);
    const children = ref(null);
    const loading = ref(false);
    const error = ref(null);

    const isSelected = computed(() => tree.selectedId.value === props.item.id);
    const hasSubdirs = computed(() => props.item.dirCount > 0);
    const ratio = computed(() => (props.parentSize ? props.item.size / props.parentSize : 1));

    async function load() {
      if (children.value || loading.value) return;
      loading.value = true;
      error.value = null;
      try {
        const page = await api(`/api/scans/${tree.scanId}/items?parentId=${props.item.id}&kind=dir&limit=500`);
        children.value = page.items;
      } catch (err) {
        error.value = err.message;
      } finally {
        loading.value = false;
      }
    }

    async function toggle() {
      if (!hasSubdirs.value) return;
      expanded.value = !expanded.value;
      if (expanded.value) await load();
    }

    function select() {
      tree.select(props.item);
    }

    // 從表格進入子資料夾時，自動展開沿路的節點
    watch(() => tree.expandPath.value, (path) => {
      if (path.includes(props.item.id) && hasSubdirs.value && !expanded.value) {
        expanded.value = true;
        load();
      }
    }, { immediate: true });

    if (props.initiallyExpanded) {
      expanded.value = true;
      load();
    }

    return { tree, expanded, children, loading, error, isSelected, hasSubdirs, ratio, toggle, select, formatBytes };
  },
  template: `
    <li class="tree-item" role="treeitem" :aria-expanded="hasSubdirs ? expanded : undefined" :aria-selected="isSelected">
      <div class="tree-row" :class="{ selected: isSelected }" :style="{ paddingLeft: (depth * 14 + 4) + 'px' }"
           @click="select" @dblclick="toggle">
        <button class="caret" type="button" :class="{ open: expanded, hidden: !hasSubdirs }" @click.stop="toggle"
                :aria-label="expanded ? '收合' : '展開'" tabindex="-1">▸</button>
        <span class="tree-name" :title="item.path">{{ item.name }}</span>
        <span v-if="item.deniedCount > 0" class="dot-warn" title="包含無法讀取的路徑">!</span>
        <span class="tree-size">{{ formatBytes(item.size) }}</span>
        <span class="tree-bar" aria-hidden="true"><span :style="{ width: (ratio * 100).toFixed(1) + '%' }"></span></span>
      </div>
      <ul v-if="expanded" role="group" class="tree-children">
        <li v-if="loading" class="tree-status muted" :style="{ paddingLeft: ((depth + 1) * 14 + 22) + 'px' }">載入中…</li>
        <li v-else-if="error" class="tree-status error" :style="{ paddingLeft: ((depth + 1) * 14 + 22) + 'px' }">{{ error }}</li>
        <tree-node v-for="child in children || []" :key="child.id" :item="child" :parent-size="item.size" :depth="depth + 1" />
      </ul>
    </li>
  `,
};
