<script setup lang="ts">
import { computed } from 'vue'
import type { ParseAsset, ParseBlock } from '../../api'

const props = defineProps<{
  runId: string
  blocks: ParseBlock[]
  assets: ParseAsset[]
  loading: boolean
  loaded: boolean
  loadingMore: boolean
  hasMore: boolean
  pageSize: number
}>()

const emit = defineEmits<{ loadMore: [] }>()
const assetsById = computed(() => new Map(props.assets.map(asset => [asset.id, asset])))

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
</script>

<template>
  <div class="result-pane" data-result-panel="blocks">
    <p v-if="loading && !loaded" class="muted pane-empty" data-result-loading="blocks">正在加载结构与资源信息…</p>
    <template v-else-if="loaded">
      <div class="block-list">
        <article v-for="block in blocks" :key="block.id">
          <header>
            <span class="block-type" :style="{ background: blockColor(block.type) }">{{ blockTypeName(block.type) }}</span>
            <span class="block-meta">
              #{{ block.sequence }}
              <template v-if="block.subtype"> · {{ block.subtype }}</template>
              <template v-if="block.pageNumber"> · 第 {{ block.pageNumber }} 页</template>
              <template v-if="block.confidence !== undefined && block.confidence !== null"> · 置信度 {{ (block.confidence * 100).toFixed(0) }}%</template>
              <template v-if="block.boundingBox"> · 有位置</template>
            </span>
          </header>
          <img v-if="block.assetId && assetsById.get(block.assetId)?.mediaType.startsWith('image/')" class="block-asset" loading="lazy" :src="assetUrl(block.assetId)" :alt="assetsById.get(block.assetId)?.name">
          <p v-if="block.content">{{ block.content }}</p>
          <p v-else class="muted">（无文本内容）</p>
        </article>
        <p v-if="!blocks.length" class="muted pane-empty">结果尚未生成或不含内容块。</p>
      </div>
      <div v-if="blocks.length" class="block-more">
        <button v-if="hasMore" class="secondary" :disabled="loadingMore" @click="emit('loadMore')">{{ loadingMore ? '加载中…' : `继续加载 ${pageSize} 块` }}</button>
        <span>已加载 {{ blocks.length }} 块{{ hasMore ? '，后面还有' : '，这是全部' }}</span>
      </div>
    </template>
    <p v-else class="muted pane-empty">结构加载失败，重新打开“结构”标签页可重试。</p>
  </div>
</template>

<style scoped>
.result-pane{padding-top:16px}
.pane-empty{padding:44px 8px;text-align:center}
.block-list{max-height:620px;overflow:auto}
.block-list article{padding:14px 0;border-bottom:1px solid #e6eae7}
.block-list header{display:flex;align-items:center;gap:9px;margin-bottom:7px}
.block-type{font-size:10px;font-weight:700;color:#fff;padding:3px 8px;border-radius:3px}
.block-meta{font-size:10px;color:var(--muted)}
.block-list p{font-size:13px;line-height:1.7;margin:0;overflow-wrap:anywhere;white-space:pre-wrap}
.block-asset{max-width:100%;max-height:240px;border:1px solid var(--line);margin-bottom:8px}
.block-more{display:flex;align-items:center;gap:12px;padding-top:14px}
.block-more span{font-size:11px;color:var(--muted)}
</style>
