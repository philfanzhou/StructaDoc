<script setup lang="ts">
import { computed } from 'vue'
import type { ParseArtifact, ParseAsset } from '../../api'

const props = defineProps<{
  runId: string
  assets: ParseAsset[]
  artifacts: ParseArtifact[]
  loading: boolean
  loaded: boolean
}>()

const imageAssets = computed(() => props.assets.filter(asset => asset.mediaType.startsWith('image/')))
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
        <a v-for="asset in imageAssets" :key="asset.id" :href="assetUrl(asset.id)" target="_blank" rel="noopener">
          <img loading="lazy" :src="assetUrl(asset.id)" :alt="asset.name">
          <small>{{ asset.name }}<template v-if="asset.width && asset.height"> · {{ asset.width }}×{{ asset.height }}</template> · {{ prettyBytes(asset.sizeBytes) }}</small>
        </a>
      </div>
      <h4 v-if="artifacts.length">制品</h4>
      <div v-if="artifacts.length" class="artifact-list">
        <a v-for="artifact in artifacts" :key="artifact.id" :href="artifactUrl(artifact.id)">
          <span class="artifact-type">{{ artifact.type }}</span>
          <span class="file-copy"><strong>{{ artifact.name }}</strong><small>{{ artifact.mediaType }} · {{ prettyBytes(artifact.sizeBytes) }}</small></span>
        </a>
      </div>
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
</style>
