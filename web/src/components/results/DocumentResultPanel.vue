<script setup lang="ts">
import { computed } from 'vue'
import type { ParseArtifact } from '../../api'

const props = defineProps<{
  runId: string
  artifacts: ParseArtifact[]
  loading: boolean
  loaded: boolean
}>()

const hasMarkdown = computed(() => props.artifacts.some(artifact => artifact.type === 'markdown'))
const previewUrl = computed(() => `/api/v1/parse-runs/${props.runId}/markdown/preview`)
</script>

<template>
  <div class="result-pane" data-result-panel="document">
    <p v-if="loading" class="muted pane-empty" data-result-loading="document">正在加载文档制品…</p>
    <!-- Rendered by the service and shown in a sandboxed frame: the Markdown comes from a
         Provider archive, so it is never given the workspace's own origin to run in. -->
    <iframe v-else-if="loaded && hasMarkdown" class="document-frame" sandbox="" :src="previewUrl" title="解析结果预览"></iframe>
    <p v-else-if="loaded" class="muted pane-empty">这次解析没有产出 Markdown 制品。切到“结构”查看规范化后的内容块。</p>
    <p v-else class="muted pane-empty">文档制品加载失败，重新打开“文档”标签页可重试。</p>
  </div>
</template>

<style scoped>
.result-pane{padding-top:16px}
.pane-empty{padding:44px 8px;text-align:center}
.document-frame{width:100%;height:620px;border:1px solid var(--line);background:#fff}
</style>
