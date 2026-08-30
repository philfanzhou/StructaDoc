<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { get, mutate, upload, type DocumentItem, type ParseArtifact, type ParseAsset, type ParseBlock, type ParseBlockList, type ParseExecutionStatus, type ParsePage, type ParseRun } from '../api'
import { message } from '../messages'
import { session } from '../session'

const busy = ref(false)
const documents = ref<DocumentItem[]>([])
const selectedDocument = ref<DocumentItem>()
const runs = ref<ParseRun[]>([])
const selectedRun = ref<ParseRun>()
const blocks = ref<ParseBlock[]>([])
const pages = ref<ParsePage[]>([])
const assets = ref<ParseAsset[]>([])
const artifacts = ref<ParseArtifact[]>([])
const fileNameFilter = ref('')
const statusFilter = ref('')
const share = ref({ issuer: '', subject: '', permissions: ['read', 'parse', 'export'] })

const statusText: Record<string, string> = { queued: '排队中', claimed: '已领取', running: '解析中', 'retry-wait': '等待重试', 'cancel-requested': '正在取消', succeeded: '已完成', failed: '失败', cancelled: '已取消' }
const finalStatuses = ['succeeded', 'failed', 'cancelled']
const canAdmin = computed(() => session.value?.isAdministrator === true)
const canCancelRun = computed(() => selectedRun.value !== undefined && !finalStatuses.includes(selectedRun.value.status))

// A Host started without Workers still accepts uploads and still queues Parse Runs, and nothing on
// this page could otherwise tell that from a queue about to move. Nobody with a browser can fix it,
// which is exactly why it has to be said rather than left to look like parsing being broken.
const parseExecution = ref<ParseExecutionStatus>()
const parsingHalted = computed(() => parseExecution.value?.workerEnabled === false)
// The other reason a parse cannot start, and the one a fresh deployment starts in: the official
// endpoint is configured as the default but nobody has entered its token yet. Only an administrator
// can, so this is said here rather than left to a refusal in English on the next click.
const providerCredentialMissing = computed(() => parseExecution.value?.providerCredentialMissing === true)

async function loadParseExecution() {
  try { parseExecution.value = await get<ParseExecutionStatus>('/api/v1/parse-execution') }
  catch { parseExecution.value = undefined }
}

// Parsing runs on the service and finishes without telling the browser, so anything unfinished on
// screen has to be read again. Polling is tied to what is actually unfinished rather than left
// running: a workspace showing only completed work makes no requests at all.
const pollIntervalMs = 3000
function isUnfinished(status?: string | null) { return !!status && !finalStatuses.includes(status) }
const hasUnfinishedWork = computed(() =>
  runs.value.some(run => isUnfinished(run.status))
  || documents.value.some(document => isUnfinished(document.latestParseStatus)))

function prettyBytes(bytes: number) { if (bytes < 1024) return `${bytes} B`; if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1048576).toFixed(1)} MB` }
function prettyDate(value: string) { return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) }

// `quiet` is what a background refresh passes. A poll that fails while the user is reading a result
// must not take over the screen with a toast; a refresh the user asked for still reports.
async function loadDocuments(quiet = false) {
  const query = new URLSearchParams({ limit: '100' })
  if (fileNameFilter.value.trim()) query.set('fileName', fileNameFilter.value.trim())
  if (statusFilter.value) query.set('parseStatus', statusFilter.value)
  try { documents.value = (await get<{ items: DocumentItem[] }>(`/api/v1/documents?${query}`)).items }
  catch (e) { if (!quiet) message((e as Error).message, true) }
}

async function onFiles(files: FileList | null) {
  if (!files?.length) return
  busy.value = true
  try { for (const file of Array.from(files)) await upload(file); message(`已上传 ${files.length} 个文档`); await loadDocuments() }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function openDocument(document: DocumentItem) {
  selectedDocument.value = document; runs.value = []; clearRunResult(); selectedRun.value = undefined
  try {
    const documentRuns = await get<ParseRun[]>(`/api/v1/documents/${document.id}/parse-runs`)
    if (!stillSelectedDocument(document.id)) return
    runs.value = documentRuns
  }
  catch (e) { if (stillSelectedDocument(document.id)) message((e as Error).message, true) }
}

async function createParse() {
  if (!selectedDocument.value) return
  busy.value = true
  try { await mutate(`/api/v1/documents/${selectedDocument.value.id}/parse-runs`, 'POST', { options: {}, maxAttempts: 3 }); message('解析任务已进入可靠队列'); await openDocument(selectedDocument.value); await loadDocuments() }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

// ---------------------------------------------------------------------------------------------
// Result presentation
//
// A Parse Run's value is its canonical result — Pages, Blocks with reading order and bounding
// boxes, Assets, and Artifacts — and each of those is a separate authorized read. They are shown
// as tabs over one selection rather than one scrolling column, because a reader is either checking
// how the document reads, or checking whether the parser got its structure right, and those two
// want different things on screen.
// ---------------------------------------------------------------------------------------------

type ResultTab = 'document' | 'blocks' | 'layout' | 'resources'
const resultTab = ref<ResultTab>('document')

// Blocks arrive one cursor page at a time. There is no count endpoint, so the number on screen is
// what has been loaded, never a total this page does not know.
const blockPageSize = 200
const nextBlockSequence = ref<number>()
const loadingBlocks = ref(false)
const hasMoreBlocks = computed(() => nextBlockSequence.value !== undefined)

const layoutPageNumber = ref<number>()
const layoutBlocks = ref<ParseBlock[]>([])
const layoutIncomplete = ref(false)
const selectedBlockId = ref<string>()

const hasMarkdown = computed(() => artifacts.value.some(artifact => artifact.type === 'markdown'))
const previewUrl = computed(() => selectedRun.value ? `/api/v1/parse-runs/${selectedRun.value.id}/markdown/preview` : '')
const imageAssets = computed(() => assets.value.filter(asset => asset.mediaType.startsWith('image/')))
const assetsById = computed(() => new Map(assets.value.map(asset => [asset.id, asset])))

function assetUrl(assetId: string) { return `/api/v1/parse-runs/${selectedRun.value?.id}/assets/${assetId}/content` }
function artifactUrl(artifactId: string) { return `/api/v1/parse-runs/${selectedRun.value?.id}/artifacts/${artifactId}/content` }

// The registered Block types from the canonical model. The set is allowed to grow inside one API
// major version, so an unrecognized type is coloured and shown under its own name rather than
// dropped — and in a neutral colour, because a type this bundle predates is not a fault in the
// result. `unknown` is the model's own token for content the normalizer could not classify, so it
// is distinguished from a type this page simply has no entry for.
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

function clearRunResult() {
  blocks.value = []; pages.value = []; assets.value = []; artifacts.value = []
  nextBlockSequence.value = undefined; layoutBlocks.value = []; layoutPageNumber.value = undefined
  layoutIncomplete.value = false; selectedBlockId.value = undefined; resultTab.value = 'document'
}

// Run-list reads follow the same selection rule as result reads below. The request owns the
// Document it started for; a later selection owns the screen, whether the old request succeeds or
// fails.
function stillSelectedDocument(documentId: string) { return selectedDocument.value?.id === documentId }

// Every result read below is checked against the current selection once it returns. Selecting a
// second Parse Run while the first one's reads are still in flight is one click, and without the
// check the slower answer lands in the newer run's panel.
function stillSelected(runId: string) { return selectedRun.value?.id === runId }

async function openRun(run: ParseRun) {
  selectedRun.value = run
  clearRunResult()
  try {
    const [pageList, assetList, artifactList] = await Promise.all([
      get<ParsePage[]>(`/api/v1/parse-runs/${run.id}/pages`),
      get<ParseAsset[]>(`/api/v1/parse-runs/${run.id}/assets`),
      get<ParseArtifact[]>(`/api/v1/parse-runs/${run.id}/artifacts`),
    ])
    if (!stillSelected(run.id)) return
    pages.value = pageList; assets.value = assetList; artifacts.value = artifactList
    // Without a rendered document there is nothing on the first tab, so open where the result is.
    if (!hasMarkdown.value) resultTab.value = 'blocks'
    await loadBlocks(true)
  } catch (e) { message((e as Error).message, true) }
}

async function loadBlocks(reset = false) {
  const run = selectedRun.value
  if (!run) return
  // Only appending is guarded against a second start: a reset belongs to a selection the user just
  // made, and dropping it would leave the panel claiming the result has no content blocks.
  // `nextSequence` is a Block sequence and sequences start at zero, so absence is the only end
  // marker; treating a falsy cursor as the end would stop after the first page.
  if (!reset && (loadingBlocks.value || nextBlockSequence.value === undefined)) return
  loadingBlocks.value = true
  try {
    const query = new URLSearchParams({ limit: String(blockPageSize) })
    if (!reset) query.set('afterSequence', String(nextBlockSequence.value))
    const page = await get<ParseBlockList>(`/api/v1/parse-runs/${run.id}/blocks?${query}`)
    if (!stillSelected(run.id)) return
    blocks.value = reset ? page.items : [...blocks.value, ...page.items]
    nextBlockSequence.value = page.nextSequence ?? undefined
  } catch (e) { message((e as Error).message, true) } finally { loadingBlocks.value = false }
}

// The layout view reads one page at a time through the Blocks endpoint's own page filter, so it
// does not depend on how far the sequential list above happens to have been scrolled.
async function openLayoutPage(pageNumber: number) {
  const run = selectedRun.value
  if (!run) return
  layoutPageNumber.value = pageNumber
  selectedBlockId.value = undefined
  try {
    const page = await get<ParseBlockList>(`/api/v1/parse-runs/${run.id}/blocks?pageNumber=${pageNumber}&limit=1000`)
    if (!stillSelected(run.id) || layoutPageNumber.value !== pageNumber) return
    layoutBlocks.value = page.items
    layoutIncomplete.value = page.nextSequence !== undefined && page.nextSequence !== null
  } catch (e) { message((e as Error).message, true) }
}

async function showTab(tab: ResultTab) {
  resultTab.value = tab
  if (tab === 'layout' && layoutPageNumber.value === undefined && pages.value.length) {
    await openLayoutPage(pages.value[0].number)
  }
}

const layoutPage = computed(() => pages.value.find(page => page.number === layoutPageNumber.value))
// Bounding boxes are normalized to the page, so the drawing only needs the page's shape. Provider
// dimensions give it; without them the boxes are still in the right relative places, and A4 is the
// least misleading shape to put them on.
const layoutAspectKnown = computed(() => !!layoutPage.value?.width && !!layoutPage.value?.height)
const layoutHeight = computed(() => layoutAspectKnown.value
  ? Math.round(1000 * (layoutPage.value!.height! / layoutPage.value!.width!))
  : 1414)
const layoutBoxes = computed(() => layoutBlocks.value
  .filter(block => block.boundingBox)
  .map(block => ({
    block,
    x: block.boundingBox!.x0 * 1000,
    y: block.boundingBox!.y0 * layoutHeight.value,
    width: Math.max((block.boundingBox!.x1 - block.boundingBox!.x0) * 1000, 1),
    height: Math.max((block.boundingBox!.y1 - block.boundingBox!.y0) * layoutHeight.value, 1),
  })))
const layoutLegend = computed(() => [...new Set(layoutBlocks.value.map(block => block.type))].sort())
const layoutBoxlessCount = computed(() => layoutBlocks.value.length - layoutBoxes.value.length)
const selectedBlock = computed(() => layoutBlocks.value.find(block => block.id === selectedBlockId.value))

async function cancelRun(run: ParseRun) {
  if (!confirm('确认取消这次解析？StructaDoc 会停止本地处理，随后该记录进入“已取消”。')) return
  busy.value = true
  try {
    await mutate(`/api/v1/parse-runs/${run.id}/cancel`, 'POST')
    // Cancellation completes as a durable transition some cycles later. The record is still
    // unfinished at this point, which is what keeps the poll below running until it settles.
    message('取消请求已受理')
    await refreshRuns(run.id)
  }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

// A record the user no longer wants is deletable whether it succeeded or failed, and down to the
// last one — a Document with no Parse Runs is simply an unparsed Document again. Only an unfinished
// run is held back, because the cleanup and the execution Worker would be after the same files;
// cancelling it first is what makes it deletable.
function canDeleteRun(run: ParseRun) { return finalStatuses.includes(run.status) }

async function deleteRun(run: ParseRun) {
  if (!confirm('确认删除这条解析记录？该次解析的结构化内容、图片、制品和原始结果归档都会被彻底清理，无法恢复。')) return
  busy.value = true
  try {
    await mutate(`/api/v1/parse-runs/${run.id}`, 'DELETE')
    if (selectedRun.value?.id === run.id) { selectedRun.value = undefined; clearRunResult() }
    message('删除请求已进入清理队列')
    await refreshRuns(selectedRun.value?.id)
  }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function refreshRuns(keepSelectedId?: string, quiet = false, document = selectedDocument.value) {
  if (!document) return
  try {
    const documentRuns = await get<ParseRun[]>(`/api/v1/documents/${document.id}/parse-runs`)
    if (!stillSelectedDocument(document.id)) return
    runs.value = documentRuns
    if (keepSelectedId) selectedRun.value = documentRuns.find(item => item.id === keepSelectedId) ?? selectedRun.value
    await loadDocuments(quiet)
  }
  catch (e) {
    if (stillSelectedDocument(document.id) && !quiet) throw e
  }
}

// A background pass keeps the selection where the user left it and stays silent on failure, so a
// service that is briefly unreachable does not clear the screen or interrupt reading.
async function pollProgress() {
  const document = selectedDocument.value
  const previous = selectedRun.value
  // Only while the notice is up. Whoever opens the switch is on another page or another machine, and
  // this is what takes the notice down without asking the user to reload.
  if (parsingHalted.value || providerCredentialMissing.value) await loadParseExecution()
  await refreshRuns(previous?.id, true, document)
  if (!document || !stillSelectedDocument(document.id)) return
  const current = selectedRun.value
  // Pages, blocks, assets, and the rendered document only exist once a run reaches a final status,
  // so they are read at that transition rather than on every tick.
  if (previous && current && isUnfinished(previous.status) && !isUnfinished(current.status)) {
    await openRun(current)
  }
}

let pollHandle: number | undefined
let pollInFlight = false
let pollingUnmounted = false
function stopPolling() {
  if (pollHandle !== undefined) { window.clearTimeout(pollHandle); pollHandle = undefined }
}

function schedulePolling() {
  if (pollingUnmounted || pollHandle !== undefined || pollInFlight || !hasUnfinishedWork.value) return
  // A one-shot timer is scheduled from `finally`, after the pass relinquishes its in-flight slot.
  // That keeps failures retryable without letting a slow pass overlap the next interval.
  pollHandle = window.setTimeout(async () => {
    pollHandle = undefined
    pollInFlight = true
    try { await pollProgress() }
    catch { /* a background failure stays quiet and the next pass still runs */ }
    finally { pollInFlight = false; schedulePolling() }
  }, pollIntervalMs)
}

watch(hasUnfinishedWork, unfinished => {
  if (unfinished) schedulePolling()
  else stopPolling()
})

onUnmounted(() => { pollingUnmounted = true; stopPolling() })

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

onMounted(() => Promise.all([loadDocuments(), loadParseExecution()]))
</script>

<template>
  <header class="page-header"><div><p class="eyebrow">WORKSPACE</p><h1>你的文档</h1><p>从原始文件到结构化结果，过程和产物都清晰可见。</p></div></header>

  <div v-if="parsingHalted" class="notice-banner">
    <div>本服务未启用解析 Worker，新建的解析任务会一直停留在“排队中”。这一项由部署方在启动参数中固定（<code>Worker__Enabled</code>），无法在网页上修改。</div>
  </div>

  <div v-if="providerCredentialMissing" class="notice-banner">
    <div>默认解析提供方还没有填写凭据，“开始新解析”会被拒绝。文档可以正常上传和保存，等管理员在管理后台填入 API Token 之后再解析即可。</div>
  </div>

  <section class="upload-zone" @dragover.prevent @drop.prevent="onFiles($event.dataTransfer?.files || null)">
    <div><strong>把文档拖到这里</strong><span>PDF、Word、PowerPoint、Excel；支持多文件</span></div>
    <label class="primary file-button">选择文件<input type="file" multiple hidden @change="onFiles(($event.target as HTMLInputElement).files)"></label>
  </section>
  <section class="toolbar"><input v-model="fileNameFilter" placeholder="按文件名筛选" @keyup.enter="loadDocuments()"><select v-model="statusFilter" @change="loadDocuments()"><option value="">全部状态</option><option value="unparsed">未解析</option><option value="queued">排队中</option><option value="running">解析中</option><option value="succeeded">已完成</option><option value="failed">失败</option></select><button class="ghost" @click="loadDocuments()">刷新</button><span v-if="hasUnfinishedWork" class="auto-refresh">解析进行中，状态每 {{ pollIntervalMs / 1000 }} 秒自动刷新</span></section>
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
      <div class="run-list"><h3>解析记录</h3><div v-for="run in runs" :key="run.id" class="run-row" :class="{ selected: selectedRun?.id === run.id }"><button class="run-open" @click="openRun(run)"><span><strong>{{ run.providerType }}</strong><small>{{ prettyDate(run.createdAt) }} · 第 {{ run.attemptCount }}/{{ run.maxAttempts }} 次尝试</small></span><span class="status" :class="run.status">{{ statusText[run.status] || run.status }}</span></button><button v-if="canDeleteRun(run)" class="danger-link run-delete" :disabled="busy" title="删除这条解析记录及其全部结果" @click="deleteRun(run)">删除</button></div><p v-if="!runs.length" class="muted">尚未创建解析任务。</p></div>
      <template v-if="selectedRun">
        <div v-if="selectedRun.errorMessage" class="inline-error">{{ selectedRun.errorCode }}：{{ selectedRun.errorMessage }}</div>
        <div v-if="canCancelRun" class="run-actions"><button class="secondary" :disabled="busy" @click="cancelRun(selectedRun)">取消解析</button><span>取消是尽力而为的：StructaDoc 会停止本地处理，但已提交给在线解析提供方的任务可能仍在上游继续消耗资源。</span></div>
        <div class="export-row"><span>导出</span><a v-for="format in ['markdown','html','zip','pdf']" :key="format" :href="`/api/v1/parse-runs/${selectedRun.id}/exports/${format}`">{{ format.toUpperCase() }}</a></div>

        <nav class="result-tabs">
          <button :class="{ active: resultTab === 'document' }" @click="showTab('document')">文档</button>
          <button :class="{ active: resultTab === 'blocks' }" @click="showTab('blocks')">结构<small>{{ blocks.length }}{{ hasMoreBlocks ? '+' : '' }}</small></button>
          <button :class="{ active: resultTab === 'layout' }" @click="showTab('layout')">版面<small>{{ pages.length }}</small></button>
          <button :class="{ active: resultTab === 'resources' }" @click="showTab('resources')">资源<small>{{ assets.length + artifacts.length }}</small></button>
        </nav>

        <div v-show="resultTab === 'document'" class="result-pane">
          <!-- Rendered by the service and shown in a sandboxed frame: the Markdown comes from a
               Provider archive, so it is never given the workspace's own origin to run in. -->
          <iframe v-if="hasMarkdown" class="document-frame" sandbox="" :src="previewUrl" title="解析结果预览"></iframe>
          <p v-else class="muted pane-empty">这次解析没有产出 Markdown 制品。切到“结构”查看规范化后的内容块。</p>
        </div>

        <div v-show="resultTab === 'blocks'" class="result-pane">
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
              <img v-if="block.assetId && assetsById.get(block.assetId)?.mediaType.startsWith('image/')" class="block-asset" :src="assetUrl(block.assetId)" :alt="assetsById.get(block.assetId)?.name">
              <p v-if="block.content">{{ block.content }}</p>
              <p v-else class="muted">（无文本内容）</p>
            </article>
            <p v-if="!blocks.length" class="muted pane-empty">结果尚未生成或不含内容块。</p>
          </div>
          <div v-if="blocks.length" class="block-more">
            <button v-if="hasMoreBlocks" class="secondary" :disabled="loadingBlocks" @click="loadBlocks()">{{ loadingBlocks ? '加载中…' : `继续加载 ${blockPageSize} 块` }}</button>
            <span>已加载 {{ blocks.length }} 块{{ hasMoreBlocks ? '，后面还有' : '，这是全部' }}</span>
          </div>
        </div>

        <div v-show="resultTab === 'layout'" class="result-pane">
          <template v-if="pages.length">
            <div class="page-picker">
              <button v-for="page in pages" :key="page.number" :class="{ active: page.number === layoutPageNumber }" @click="openLayoutPage(page.number)">{{ page.number }}</button>
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
                  @click="selectedBlockId = box.block.id"
                  @keydown.enter.prevent="selectedBlockId = box.block.id"
                  @keydown.space.prevent="selectedBlockId = box.block.id">
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
                <p v-if="layoutIncomplete" class="hint">本页内容块超过 1000 个，版面图只画了前 1000 个。</p>
                <div class="layout-legend">
                  <span v-for="type in layoutLegend" :key="type"><i :style="{ background: blockColor(type) }"></i>{{ blockTypeName(type) }}</span>
                </div>
                <div v-if="selectedBlock" class="layout-selected">
                  <strong>#{{ selectedBlock.sequence }} · {{ blockTypeName(selectedBlock.type) }}</strong>
                  <img v-if="selectedBlock.assetId && assetsById.get(selectedBlock.assetId)?.mediaType.startsWith('image/')" :src="assetUrl(selectedBlock.assetId)" :alt="assetsById.get(selectedBlock.assetId)?.name">
                  <p>{{ selectedBlock.content || '（无文本内容）' }}</p>
                </div>
              </div>
            </div>
            <p v-else class="muted pane-empty">选择一页查看它的版面。</p>
          </template>
          <p v-else class="muted pane-empty">这次解析没有可靠的物理页面，内容块只有全局阅读顺序。</p>
        </div>

        <div v-show="resultTab === 'resources'" class="result-pane">
          <h4 v-if="imageAssets.length">图片资源</h4>
          <div v-if="imageAssets.length" class="asset-grid">
            <a v-for="asset in imageAssets" :key="asset.id" :href="assetUrl(asset.id)" target="_blank" rel="noopener">
              <img :src="assetUrl(asset.id)" :alt="asset.name">
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
        </div>
      </template>
      <details v-if="selectedDocument.ownedByCurrentUser || canAdmin" class="share-box"><summary>共享访问</summary><label>OIDC Issuer<input v-model="share.issuer" placeholder="https://identity.example.com"></label><label>Subject<input v-model="share.subject" placeholder="用户的 sub"></label><fieldset><legend>权限</legend><label v-for="permission in ['read','parse','export','delete','share']" :key="permission"><input v-model="share.permissions" type="checkbox" :value="permission"> {{ permission }}</label></fieldset><button class="secondary" @click="grantAccess">保存授权</button></details>
    </section>
    <section v-else class="panel detail empty-detail"><span class="big-number">01</span><h2>选择一个文档</h2><p>查看解析历史、规范化结果、资源文件和导出选项。</p></section>
  </div>
</template>

<style scoped>
.auto-refresh{display:flex;align-items:center;font-size:11px;color:#57816e}
.run-row{display:flex;align-items:center;border:1px solid var(--line);background:#fff;margin-bottom:7px}
.run-row.selected{border-color:#4d8268;background:#f0f6f2}
.run-open{flex:1;min-width:0;display:flex;justify-content:space-between;align-items:center;gap:10px;text-align:left;border:0;background:transparent;padding:12px}
.run-delete{align-self:center;font-size:11px;padding:6px 12px}
.run-delete:disabled{opacity:.5;cursor:default}
.run-actions{display:flex;align-items:center;gap:10px;margin:12px 0}
.run-actions span{font-size:11px;color:#5c6b62;line-height:1.5}

/* Result tabs */
.result-tabs{display:flex;gap:2px;border-bottom:1px solid var(--line);margin-top:22px}
.result-tabs button{border:0;background:transparent;padding:11px 16px;font-size:13px;font-weight:700;color:var(--muted);border-bottom:2px solid transparent;display:flex;align-items:center;gap:7px}
.result-tabs button:hover{color:var(--ink)}
.result-tabs button.active{color:var(--green);border-bottom-color:var(--green)}
.result-tabs small{font-size:10px;font-weight:700;background:#e7eee9;color:#57816e;padding:2px 7px;border-radius:20px}
.result-tabs button.active small{background:var(--mint);color:var(--green)}
.result-pane{padding-top:16px}
.pane-empty{padding:44px 8px;text-align:center}

/* Document */
.document-frame{width:100%;height:620px;border:1px solid var(--line);background:#fff}

/* Blocks */
.block-list{max-height:620px;overflow:auto}
.block-list article{padding:14px 0;border-bottom:1px solid #e6eae7}
.block-list header{display:flex;align-items:center;gap:9px;margin-bottom:7px}
.block-type{font-size:10px;font-weight:700;color:#fff;padding:3px 8px;border-radius:3px}
.block-meta{font-size:10px;color:var(--muted)}
.block-list p{font-size:13px;line-height:1.7;margin:0;overflow-wrap:anywhere;white-space:pre-wrap}
.block-asset{max-width:100%;max-height:240px;border:1px solid var(--line);margin-bottom:8px}
.block-more{display:flex;align-items:center;gap:12px;padding-top:14px}
.block-more span{font-size:11px;color:var(--muted)}

/* Layout */
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

/* Resources */
.result-pane h4{font-size:12px;margin:0 0 12px}
.result-pane h4~h4{margin-top:26px}
.asset-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}
.asset-grid a{display:grid;gap:6px;text-decoration:none;color:var(--ink)}
.asset-grid img{width:100%;height:110px;object-fit:contain;background:#fff;border:1px solid var(--line)}
.asset-grid small{font-size:10px;color:var(--muted);overflow-wrap:anywhere}
.artifact-list a{display:grid;grid-template-columns:auto 1fr;align-items:center;gap:12px;padding:11px 0;border-bottom:1px solid #e6eae7;text-decoration:none;color:var(--ink)}
.artifact-type{font-size:10px;font-weight:700;color:#37664f;background:#e0e9e3;padding:5px 9px;border-radius:3px;white-space:nowrap}

@media(max-width:900px){.layout-view{grid-template-columns:1fr}}
</style>
