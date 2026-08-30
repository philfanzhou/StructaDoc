<script setup lang="ts">
import { computed } from 'vue'
import type { ParseAsset, ParseBlock, ParsePage } from '../../api'

const props = defineProps<{
  runId: string
  pages: ParsePage[]
  pageNumber?: number
  blocks: ParseBlock[]
  incomplete: boolean
  selectedBlockId?: string
  assets: ParseAsset[]
  loading: boolean
  loaded: boolean
}>()

const emit = defineEmits<{
  selectPage: [pageNumber: number]
  selectBlock: [blockId: string]
}>()

const blockTypeColors: Record<string, string> = {
  title: '#1b543d', text: '#4d8268', list: '#6b8f57', table: '#b0761e', formula: '#7a4f9c',
  image: '#2b6c8f', code: '#546b7a', header: '#94a29a', footer: '#94a29a', footnote: '#a0846b',
  unknown: '#8a7f6b',
}
const blockTypeNames: Record<string, string> = {
  title: '标题', text: '正文', list: '列表', table: '表格', formula: '公式',
  image: '图片', code: '代码', header: '页眉', footer: '页脚', footnote: '脚注', unknown: '未识别',
}
function blockColor(type: string) { return blockTypeColors[type] ?? '#69766f' }
function blockTypeName(type: string) { return blockTypeNames[type] ?? type }
function assetUrl(assetId: string) { return `/api/v1/parse-runs/${props.runId}/assets/${assetId}/content` }

const assetsById = computed(() => new Map(props.assets.map(asset => [asset.id, asset])))
const layoutPage = computed(() => props.pages.find(page => page.number === props.pageNumber))
const layoutAspectKnown = computed(() => !!layoutPage.value?.width && !!layoutPage.value?.height)
const layoutHeight = computed(() => layoutAspectKnown.value
  ? Math.round(1000 * (layoutPage.value!.height! / layoutPage.value!.width!))
  : 1414)
const layoutBoxes = computed(() => props.blocks
  .filter(block => block.boundingBox)
  .map(block => ({
    block,
    x: block.boundingBox!.x0 * 1000,
    y: block.boundingBox!.y0 * layoutHeight.value,
    width: Math.max((block.boundingBox!.x1 - block.boundingBox!.x0) * 1000, 1),
    height: Math.max((block.boundingBox!.y1 - block.boundingBox!.y0) * layoutHeight.value, 1),
  })))
const layoutLegend = computed(() => [...new Set(props.blocks.map(block => block.type))].sort())
const layoutBoxlessCount = computed(() => props.blocks.length - layoutBoxes.value.length)
const selectedBlock = computed(() => props.blocks.find(block => block.id === props.selectedBlockId))
</script>

<template>
  <div class="result-pane" data-result-panel="layout">
    <p v-if="loading && !loaded" class="muted pane-empty" data-result-loading="layout">正在加载页面与版面…</p>
    <template v-else-if="loaded">
      <template v-if="pages.length">
        <div class="page-picker">
          <button v-for="page in pages" :key="page.number" :class="{ active: page.number === pageNumber }" @click="emit('selectPage', page.number)">{{ page.number }}</button>
        </div>
        <div v-if="layoutPage" class="layout-view">
          <svg class="layout-map" :viewBox="`0 0 1000 ${layoutHeight}`" preserveAspectRatio="xMidYMin meet" role="group" aria-label="页面版面示意">
            <rect class="layout-paper" x="0" y="0" width="1000" :height="layoutHeight" />
            <g
              v-for="(box, index) in layoutBoxes"
              :key="box.block.id"
              class="layout-box"
              :class="{ selected: box.block.id === selectedBlockId }"
              role="button"
              tabindex="0"
              :aria-label="`第 ${index + 1} 块，类型 ${blockTypeName(box.block.type)}`"
              :aria-pressed="box.block.id === selectedBlockId"
              @click="emit('selectBlock', box.block.id)"
              @keydown.enter.prevent="emit('selectBlock', box.block.id)"
              @keydown.space.prevent="emit('selectBlock', box.block.id)">
              <rect :x="box.x" :y="box.y" :width="box.width" :height="box.height" :stroke="blockColor(box.block.type)" :fill="blockColor(box.block.type)" />
              <text :x="box.x + 10" :y="box.y + 40" :fill="blockColor(box.block.type)">{{ index + 1 }}</text>
            </g>
          </svg>
          <div class="layout-side">
            <p class="hint">
              第 {{ layoutPage.number }} 页 ·
              <template v-if="layoutAspectKnown">{{ layoutPage.width }}×{{ layoutPage.height }} {{ layoutPage.unit || '' }}</template>
              <template v-else>提供方未报告页面尺寸，按 A4 比例示意</template>
            </p>
            <p class="hint">框内数字是这一页中有位置信息的块的阅读顺序；点击任意框查看它的内容。</p>
            <p v-if="layoutBoxlessCount > 0" class="hint">本页另有 {{ layoutBoxlessCount }} 个内容块没有位置信息，只出现在“结构”里。</p>
            <p v-if="incomplete" class="hint">本页内容块超过 1000 个，版面图只画了前 1000 个。</p>
            <div class="layout-legend">
              <span v-for="type in layoutLegend" :key="type"><i :style="{ background: blockColor(type) }"></i>{{ blockTypeName(type) }}</span>
            </div>
            <div v-if="selectedBlock" class="layout-selected">
              <strong>#{{ selectedBlock.sequence }} · {{ blockTypeName(selectedBlock.type) }}</strong>
              <img v-if="selectedBlock.assetId && assetsById.get(selectedBlock.assetId)?.mediaType.startsWith('image/')" loading="lazy" :src="assetUrl(selectedBlock.assetId)" :alt="assetsById.get(selectedBlock.assetId)?.name">
              <p>{{ selectedBlock.content || '（无文本内容）' }}</p>
            </div>
          </div>
        </div>
        <p v-else class="muted pane-empty">选择一页查看它的版面。</p>
      </template>
      <p v-else class="muted pane-empty">这次解析没有可靠的物理页面，内容块只有全局阅读顺序。</p>
    </template>
    <p v-else class="muted pane-empty">版面加载失败，重新打开“版面”标签页可重试。</p>
  </div>
</template>

<style scoped>
.result-pane{padding-top:16px}
.pane-empty{padding:44px 8px;text-align:center}
.page-picker{display:flex;flex-wrap:wrap;gap:5px;margin-bottom:14px;max-height:88px;overflow:auto}
.page-picker button{border:1px solid var(--line);background:#fff;color:var(--muted);font-size:11px;min-width:32px;padding:5px 8px;border-radius:3px}
.page-picker button.active{border-color:var(--green);background:var(--green);color:#fff}
.layout-view{display:grid;grid-template-columns:minmax(0,1.4fr) minmax(200px,1fr);gap:18px;align-items:start}
.layout-map{width:100%;max-height:620px}
.layout-paper{fill:#fff;stroke:var(--line)}
.layout-box rect{fill-opacity:.1;stroke-width:2}
.layout-box{cursor:pointer}
.layout-box:hover rect{fill-opacity:.24}
.layout-box:focus-visible{outline:5px solid #b0761e;outline-offset:6px}
.layout-box.selected rect{fill-opacity:.32;stroke-width:4}
.layout-box text{font:700 38px ui-monospace,monospace;paint-order:stroke;stroke:#fffef9;stroke-width:5px}
.layout-side{display:grid;gap:10px}
.layout-legend{display:flex;flex-wrap:wrap;gap:6px 12px;font-size:11px;color:#48584f}
.layout-legend span{display:flex;align-items:center;gap:5px}
.layout-legend i{width:10px;height:10px;border-radius:2px}
.layout-selected{border-top:1px solid var(--line);padding-top:12px;display:grid;gap:8px;max-height:300px;overflow:auto}
.layout-selected strong{font-size:12px}
.layout-selected img{max-width:100%;border:1px solid var(--line)}
.layout-selected p{font-size:12px;line-height:1.7;margin:0;overflow-wrap:anywhere;white-space:pre-wrap}
@media(max-width:900px){.layout-view{grid-template-columns:1fr}}
</style>
