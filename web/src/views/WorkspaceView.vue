<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { get, mutate, upload, type DocumentItem, type ParseBlock, type ParseRun } from '../api'
import { message } from '../messages'
import { session } from '../session'

const busy = ref(false)
const documents = ref<DocumentItem[]>([])
const selectedDocument = ref<DocumentItem>()
const runs = ref<ParseRun[]>([])
const selectedRun = ref<ParseRun>()
const blocks = ref<ParseBlock[]>([])
const pages = ref<any[]>([])
const assets = ref<any[]>([])
const artifacts = ref<any[]>([])
const markdown = ref('')
const fileNameFilter = ref('')
const statusFilter = ref('')
const share = ref({ issuer: '', subject: '', permissions: ['read', 'parse', 'export'] })

const statusText: Record<string, string> = { queued: '排队中', claimed: '已领取', running: '解析中', 'retry-wait': '等待重试', 'cancel-requested': '正在取消', succeeded: '已完成', failed: '失败', cancelled: '已取消' }
const finalStatuses = ['succeeded', 'failed', 'cancelled']
const canAdmin = computed(() => session.value?.isAdministrator === true)
const canCancelRun = computed(() => selectedRun.value !== undefined && !finalStatuses.includes(selectedRun.value.status))

function prettyBytes(bytes: number) { if (bytes < 1024) return `${bytes} B`; if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1048576).toFixed(1)} MB` }
function prettyDate(value: string) { return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) }

async function loadDocuments() {
  const query = new URLSearchParams({ limit: '100' })
  if (fileNameFilter.value.trim()) query.set('fileName', fileNameFilter.value.trim())
  if (statusFilter.value) query.set('parseStatus', statusFilter.value)
  try { documents.value = (await get<{ items: DocumentItem[] }>(`/api/v1/documents?${query}`)).items }
  catch (e) { message((e as Error).message, true) }
}

async function onFiles(files: FileList | null) {
  if (!files?.length) return
  busy.value = true
  try { for (const file of Array.from(files)) await upload(file); message(`已上传 ${files.length} 个文档`); await loadDocuments() }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function openDocument(document: DocumentItem) {
  selectedDocument.value = document; selectedRun.value = undefined; blocks.value = []; pages.value = []; assets.value = []; artifacts.value = []; markdown.value = ''
  try { runs.value = await get(`/api/v1/documents/${document.id}/parse-runs`) }
  catch (e) { message((e as Error).message, true) }
}

async function createParse() {
  if (!selectedDocument.value) return
  busy.value = true
  try { await mutate(`/api/v1/documents/${selectedDocument.value.id}/parse-runs`, 'POST', { options: {}, maxAttempts: 3 }); message('解析任务已进入可靠队列'); await openDocument(selectedDocument.value); await loadDocuments() }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function openRun(run: ParseRun) {
  selectedRun.value = run; markdown.value = ''
  try {
    const loaded = await Promise.all([
      get<{ items: ParseBlock[] }>(`/api/v1/parse-runs/${run.id}/blocks?limit=500`),
      get<any[]>(`/api/v1/parse-runs/${run.id}/pages`),
      get<any[]>(`/api/v1/parse-runs/${run.id}/assets`),
      get<any[]>(`/api/v1/parse-runs/${run.id}/artifacts`),
    ])
    blocks.value = loaded[0].items; pages.value = loaded[1]; assets.value = loaded[2]; artifacts.value = loaded[3]
    const response = await fetch(`/api/v1/parse-runs/${run.id}/markdown`, { credentials: 'same-origin' })
    if (response.ok) markdown.value = await response.text()
  } catch (e) { message((e as Error).message, true) }
}

async function cancelRun(run: ParseRun) {
  if (!confirm('确认取消这次解析？StructaDoc 会停止本地处理，随后该记录进入“已取消”。')) return
  busy.value = true
  try {
    await mutate(`/api/v1/parse-runs/${run.id}/cancel`, 'POST')
    message('取消请求已受理')
    await refreshRuns(run.id)
    // Completion is a durable transition, so re-read once more after the maintenance cycle.
    window.setTimeout(() => refreshRuns(run.id).catch(() => undefined), 2000)
  }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function refreshRuns(keepSelectedId?: string) {
  if (!selectedDocument.value) return
  runs.value = await get(`/api/v1/documents/${selectedDocument.value.id}/parse-runs`)
  if (keepSelectedId) selectedRun.value = runs.value.find(item => item.id === keepSelectedId) ?? selectedRun.value
  await loadDocuments()
}

async function deleteCurrent() {
  if (!selectedDocument.value || !confirm(`确认删除“${selectedDocument.value.originalFileName}”？对象与关系数据将由可恢复清理任务处理。`)) return
  try { await mutate(`/api/v1/documents/${selectedDocument.value.id}`, 'DELETE'); selectedDocument.value = undefined; message('删除请求已进入清理队列'); await loadDocuments() }
  catch (e) { message((e as Error).message, true) }
}

async function grantAccess() {
  if (!selectedDocument.value) return
  try { await mutate(`/api/v1/documents/${selectedDocument.value.id}/access-grants`, 'POST', share.value); message('访问权限已保存') }
  catch (e) { message((e as Error).message, true) }
}

onMounted(() => loadDocuments())
</script>

<template>
  <header class="page-header"><div><p class="eyebrow">WORKSPACE</p><h1>你的文档</h1><p>从原始文件到结构化结果，过程和产物都清晰可见。</p></div></header>

  <section class="upload-zone" @dragover.prevent @drop.prevent="onFiles($event.dataTransfer?.files || null)">
    <div><strong>把文档拖到这里</strong><span>PDF、Word、PowerPoint、Excel；支持多文件</span></div>
    <label class="primary file-button">选择文件<input type="file" multiple hidden @change="onFiles(($event.target as HTMLInputElement).files)"></label>
  </section>
  <section class="toolbar"><input v-model="fileNameFilter" placeholder="按文件名筛选" @keyup.enter="loadDocuments"><select v-model="statusFilter" @change="loadDocuments"><option value="">全部状态</option><option value="unparsed">未解析</option><option value="queued">排队中</option><option value="running">解析中</option><option value="succeeded">已完成</option><option value="failed">失败</option></select><button class="ghost" @click="loadDocuments">刷新</button></section>
  <div class="workspace-grid">
    <section class="panel document-list">
      <button v-for="document in documents" :key="document.id" class="document-row" :class="{ selected: selectedDocument?.id === document.id }" @click="openDocument(document)">
        <span class="file-type">{{ document.extension.replace('.', '').slice(0, 4).toUpperCase() }}</span><span class="file-copy"><strong>{{ document.originalFileName }}</strong><small>{{ prettyBytes(document.sizeBytes) }} · {{ prettyDate(document.createdAt) }}</small></span><span class="status" :class="document.latestParseStatus">{{ statusText[document.latestParseStatus || ''] || '未解析' }}</span>
      </button>
      <div v-if="!documents.length" class="empty"><strong>还没有符合条件的文档</strong><span>上传一个文件，开始第一次结构化解析。</span></div>
    </section>
    <section class="panel detail" v-if="selectedDocument">
      <div class="detail-head"><div><p class="eyebrow">DOCUMENT</p><h2>{{ selectedDocument.originalFileName }}</h2><p>{{ selectedDocument.mediaType }} · SHA-256 {{ selectedDocument.sha256.slice(0, 12) }}…</p></div><button class="danger-link" @click="deleteCurrent">删除</button></div>
      <div class="actions"><button class="primary" :disabled="busy" @click="createParse">开始新解析</button><a class="secondary button-link" :href="`/api/v1/documents/${selectedDocument.id}/content`">下载原文</a></div>
      <div class="run-list"><h3>解析记录</h3><button v-for="run in runs" :key="run.id" :class="{ selected: selectedRun?.id === run.id }" @click="openRun(run)"><span><strong>{{ run.providerType }}</strong><small>{{ prettyDate(run.createdAt) }} · 第 {{ run.attemptCount }}/{{ run.maxAttempts }} 次尝试</small></span><span class="status" :class="run.status">{{ statusText[run.status] || run.status }}</span></button><p v-if="!runs.length" class="muted">尚未创建解析任务。</p></div>
      <template v-if="selectedRun">
        <div v-if="selectedRun.errorMessage" class="inline-error">{{ selectedRun.errorCode }}：{{ selectedRun.errorMessage }}</div>
        <div v-if="canCancelRun" class="run-actions"><button class="secondary" :disabled="busy" @click="cancelRun(selectedRun)">取消解析</button><span>取消是尽力而为的：StructaDoc 会停止本地处理，但已提交给在线解析提供方的任务可能仍在上游继续消耗资源。</span></div>
        <div class="export-row"><span>导出</span><a v-for="format in ['markdown','html','zip','pdf']" :key="format" :href="`/api/v1/parse-runs/${selectedRun.id}/exports/${format}`">{{ format.toUpperCase() }}</a></div>
        <div class="result-tabs"><div class="result-title"><h3>结构化内容</h3><span>{{ pages.length }} 页</span><span>{{ blocks.length }} 块</span><span>{{ assets.length }} 资源</span><span>{{ artifacts.length }} 制品</span></div><div v-if="markdown" class="markdown-preview"><pre>{{ markdown }}</pre></div><div v-else class="block-list"><article v-for="block in blocks" :key="block.id"><span>#{{ block.sequence }} · {{ block.type }}<template v-if="block.pageNumber"> · P{{ block.pageNumber }}</template></span><p>{{ block.content || '（无文本内容）' }}</p></article><p v-if="!blocks.length" class="muted">结果尚未生成或不含文本块。</p></div><details v-if="assets.length || artifacts.length" class="resource-list"><summary>资源与制品下载</summary><a v-for="asset in assets" :key="asset.id" :href="`/api/v1/parse-runs/${selectedRun.id}/assets/${asset.id}/content`">{{ asset.name }} · {{ prettyBytes(asset.sizeBytes) }}</a><a v-for="artifact in artifacts" :key="artifact.id" :href="`/api/v1/parse-runs/${selectedRun.id}/artifacts/${artifact.id}/content`">{{ artifact.type }} · {{ artifact.name }}</a></details></div>
      </template>
      <details v-if="selectedDocument.ownedByCurrentUser || canAdmin" class="share-box"><summary>共享访问</summary><label>OIDC Issuer<input v-model="share.issuer" placeholder="https://identity.example.com"></label><label>Subject<input v-model="share.subject" placeholder="用户的 sub"></label><fieldset><legend>权限</legend><label v-for="permission in ['read','write','parse','export','delete','share']" :key="permission"><input v-model="share.permissions" type="checkbox" :value="permission"> {{ permission }}</label></fieldset><button class="secondary" @click="grantAccess">保存授权</button></details>
    </section>
    <section v-else class="panel detail empty-detail"><span class="big-number">01</span><h2>选择一个文档</h2><p>查看解析历史、规范化结果、资源文件和导出选项。</p></section>
  </div>
</template>

<style scoped>
.run-actions{display:flex;align-items:center;gap:10px;margin:12px 0}.run-actions span{font-size:11px;color:#5c6b62;line-height:1.5}.result-title{display:flex;align-items:center;gap:8px}.result-title h3{margin-right:auto}.result-title span{font-size:10px;color:#57816e;background:#e7eee9;padding:4px 7px;border-radius:20px}.resource-list{border-top:1px solid #d8ded9;padding-top:12px;margin-top:14px}.resource-list summary{font-size:12px;font-weight:700;cursor:pointer;margin-bottom:10px}.resource-list a{display:block;color:#123c2b;font-size:12px;padding:8px 0;border-bottom:1px solid #e8ebe8;text-decoration:none}.share-box fieldset{border:0;padding:8px 0 14px;display:flex;flex-wrap:wrap;gap:8px 14px}.share-box legend{font-size:12px;font-weight:700}.share-box fieldset label{display:flex;align-items:center;gap:5px;font-size:11px}.share-box fieldset input{width:auto}
</style>
