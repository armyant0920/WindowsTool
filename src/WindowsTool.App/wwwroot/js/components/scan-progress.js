// 掃描進度。

import { formatBytes, formatNumber, formatDuration } from '../format.js';

const { computed, ref } = Vue;

export const ScanProgressView = {
  name: 'ScanProgressView',
  props: {
    scan: { type: Object, required: true },
    cancel: { type: Function, required: true },
  },
  setup(props) {
    const cancelling = ref(false);

    // 掃描磁碟根目錄時，可以用「已使用空間」估算百分比
    const percent = computed(() => {
      const expected = props.scan.expectedBytes;
      if (!expected) return null;
      return Math.min(99, (props.scan.progress.bytes / expected) * 100);
    });

    async function onCancel() {
      cancelling.value = true;
      await props.cancel();
    }

    return { percent, cancelling, onCancel, formatBytes, formatNumber, formatDuration };
  },
  template: `
    <section class="card">
      <div class="progress-head">
        <h2>掃描中</h2>
        <button class="btn" type="button" @click="onCancel" :disabled="cancelling">{{ cancelling ? '取消中…' : '取消' }}</button>
      </div>
      <p class="mono path-line" :title="scan.root">{{ scan.root }}</p>

      <div class="progress" :class="{ indeterminate: percent === null }" role="progressbar"
           :aria-valuenow="percent === null ? undefined : Math.round(percent)" aria-valuemin="0" aria-valuemax="100">
        <span :style="percent === null ? null : { width: percent.toFixed(1) + '%' }"></span>
      </div>

      <dl class="stats">
        <div><dt>大小</dt><dd>{{ formatBytes(scan.progress.bytes) }}<small v-if="percent !== null" class="muted">（約 {{ percent.toFixed(0) }}%）</small></dd></div>
        <div><dt>檔案</dt><dd>{{ formatNumber(scan.progress.files) }}</dd></div>
        <div><dt>資料夾</dt><dd>{{ formatNumber(scan.progress.dirs) }}</dd></div>
        <div><dt>無法讀取</dt><dd :class="{ warn: scan.progress.denied > 0 }">{{ formatNumber(scan.progress.denied) }}</dd></div>
        <div><dt>經過時間</dt><dd>{{ formatDuration(scan.elapsedMs) }}</dd></div>
      </dl>

      <p class="mono current-path muted" :title="scan.progress.currentPath">{{ scan.progress.currentPath || '準備中…' }}</p>
    </section>
  `,
};
