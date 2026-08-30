<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ParseArtifact, ParseAsset } from '../../api'

const props = defineProps<{
  runId: string
  assets: ParseAsset[]
  artifacts: ParseArtifact[]
  loading: boolean
  loaded: boolean
}>()

const imageAssets = computed(() => props.assets.filter(asset => asset.mediaType.startsWith('image/')))
const assetPageSize = 24
const artifactPageSize = 50
const assetPage = ref(1)
const artifactPage = ref(1)
const assetPageCount = computed(() => Math.max(1, Math.ceil(imageAssets.value.length / assetPageSize)))
const artifactPageCount = computed(() => Math.max(1, Math.ceil(props.artifacts.length / artifactPageSize)))
const assetPageStart = computed(() => (assetPage.value - 1) * assetPageSize)
const artifactPageStart = computed(() => (artifactPage.value - 1) * artifactPageSize)
const visibleAssets = computed(() => imageAssets.value.slice(assetPageStart.value, assetPageStart.value + assetPageSize))
const visibleArtifacts = computed(() => props.artifacts.slice(artifactPageStart.value, artifactPageStart.value + artifactPageSize))
const assetPageEnd = computed(() => Math.min(assetPageStart.value + visibleAssets.value.length, imageAssets.value.length))
const artifactPageEnd = computed(() => Math.min(artifactPageStart.value + visibleArtifacts.value.length, props.artifacts.length))

function clampPage(page: number, pageCount: number) {
  return Math.min(Math.max(page, 1), pageCount)
}

watch(assetPageCount, pageCount => { assetPage.value = clampPage(assetPage.value, pageCount) })
watch(artifactPageCount, pageCount => { artifactPage.value = clampPage(artifactPage.value, pageCount) })
watch(() => props.runId, () => { assetPage.value = 1; artifactPage.value = 1 })

function prettyBytes(bytes: number) { if (bytes < 1024) return `${bytes} B`; if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1048576).toFixed(1)} MB` }
function assetUrl(assetId: string) { return `/api/v1/parse-runs/${props.runId}/assets/${assetId}/content` }
function artifactUrl(artifactId: string) { return `/api/v1/parse-runs/${props.runId}/artifacts/${artifactId}/content` }
</script>

<template>
  <div class="result-pane" data-result-panel="resources">
    <p v-if="loading && !loaded" class="muted pane-empty" data-result-loading="resources">正在加载资源与制品…</p>
    <template v-else-if="loaded">
      <h4 v-if="imageAssets.length">图片资源</h4>
      <div v-if="imageAssets.length" class="asset-grid">
        <a v-for="asset in visibleAssets" :key="asset.id" :href="assetUrl(asset.id)" target="_blank" rel="noopener">
          <img loading="lazy" :src="assetUrl(asset.id)" :alt="asset.name">
          <small>{{ asset.name }}<template v-if="asset.width && asset.height"> · {{ asset.width }}×{{ asset.height }}</template> · {{ prettyBytes(asset.sizeBytes) }}</small>
        </a>
      </div>
      <nav v-if="assetPageCount > 1" class="local-pagination" aria-label="图片资源分页">
        <button type="button" :disabled="assetPage === 1" @click="assetPage = clampPage(assetPage - 1, assetPageCount)">上一页图片</button>
        <span aria-live="polite">第 {{ assetPageStart + 1 }}–{{ assetPageEnd }} 项，共 {{ imageAssets.length }} 项</span>
        <button type="button" :disabled="assetPage === assetPageCount" @click="assetPage = clampPage(assetPage + 1, assetPageCount)">下一页图片</button>
      </nav>
      <h4 v-if="artifacts.length">制品</h4>
      <div v-if="artifacts.length" class="artifact-list">
        <a v-for="artifact in visibleArtifacts" :key="artifact.id" :href="artifactUrl(artifact.id)">
          <span class="artifact-type">{{ artifact.type }}</span>
          <span class="file-copy"><strong>{{ artifact.name }}</strong><small>{{ artifact.mediaType }} · {{ prettyBytes(artifact.sizeBytes) }}</small></span>
        </a>
      </div>
      <nav v-if="artifactPageCount > 1" class="local-pagination" aria-label="制品分页">
        <button type="button" :disabled="artifactPage === 1" @click="artifactPage = clampPage(artifactPage - 1, artifactPageCount)">上一页制品</button>
        <span aria-live="polite">第 {{ artifactPageStart + 1 }}–{{ artifactPageEnd }} 项，共 {{ artifacts.length }} 项</span>
        <button type="button" :disabled="artifactPage === artifactPageCount" @click="artifactPage = clampPage(artifactPage + 1, artifactPageCount)">下一页制品</button>
      </nav>
      <p v-if="!assets.length && !artifacts.length" class="muted pane-empty">这次解析没有产出资源或制品。</p>
    </template>
    <p v-else class="muted pane-empty">资源加载失败，重新打开“资源”标签页可重试。</p>
  </div>
</template>

<style scoped>
.result-pane{padding-top:16px}
.pane-empty{padding:44px 8px;text-align:center}
.result-pane h4{font-size:12px;margin:0 0 12px}
.result-pane h4~h4{margin-top:26px}
.asset-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}
.asset-grid a{display:grid;gap:6px;text-decoration:none;color:var(--ink)}
.asset-grid img{width:100%;height:110px;object-fit:contain;background:#fff;border:1px solid var(--line)}
.asset-grid small{font-size:10px;color:var(--muted);overflow-wrap:anywhere}
.artifact-list a{display:grid;grid-template-columns:auto 1fr;align-items:center;gap:12px;padding:11px 0;border-bottom:1px solid #e6eae7;text-decoration:none;color:var(--ink)}
.artifact-type{font-size:10px;font-weight:700;color:#37664f;background:#e0e9e3;padding:5px 9px;border-radius:3px;white-space:nowrap}
.local-pagination{display:flex;align-items:center;justify-content:flex-end;gap:9px;margin-top:12px;font-size:11px;color:var(--muted)}
.local-pagination button{border:1px solid var(--line);background:#fff;color:var(--green);font-size:11px;padding:5px 9px;border-radius:3px}
.local-pagination button:disabled{color:#9aa59f;background:#f2f4f2;cursor:default}
</style>
