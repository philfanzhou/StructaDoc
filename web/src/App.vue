<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { antiforgery, get, mutate, resetAntiforgery, upload, type DocumentItem, type ParseBlock, type ParseRun, type Session } from './api'

const session = ref<Session>()
const view = ref<'documents' | 'admin'>('documents')
const busy = ref(false)
const error = ref('')
const notice = ref('')
const email = ref('')
const password = ref('')
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
const providers = ref<any[]>([])
const clients = ref<any[]>([])
const newProvider = ref({ name: '', providerType: 'mineru-local', baseUrl: '', credential: '', model: '', backend: '', isEnabled: true, isDefault: false, clearCredential: false })
const newClient = ref({ name: '', scopes: ['documents:read', 'documents:write', 'parses:read', 'parses:write'] })
const issuedCredential = ref('')
const share = ref({ issuer: '', subject: '', permissions: ['read', 'parse', 'export'] })

const statusText: Record<string, string> = { queued: '排队中', claimed: '已领取', running: '解析中', 'retry-wait': '等待重试', succeeded: '已完成', failed: '失败', cancelled: '已取消' }
const canAdmin = computed(() => session.value?.isAdministrator)

function message(value: string, failure = false) { if (failure) error.value = value; else notice.value = value; window.setTimeout(() => failure ? error.value = '' : notice.value = '', 5000) }
function prettyBytes(bytes: number) { if (bytes < 1024) return `${bytes} B`; if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1048576).toFixed(1)} MB` }
function prettyDate(value: string) { return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) }

async function initialize() {
  session.value = await get<Session>('/api/v1/session')
  if (session.value.authenticated) await loadDocuments()
}

async function localLogin() {
  busy.value = true
  try { await antiforgery(); await mutate('/api/v1/admin/session', 'POST', { email: email.value, password: password.value }); resetAntiforgery(); await initialize() }
  catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}

async function logout() {
  try {
    if (session.value?.subjectType === 'administrator') await mutate('/api/v1/admin/session', 'DELETE')
    else await mutate('/api/v1/session/logout', 'POST')
    location.assign('/')
  } catch (e) { message((e as Error).message, true) }
}

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

async function loadAdmin() {
  view.value = 'admin'
  try { [providers.value, clients.value] = await Promise.all([get('/api/v1/admin/provider-configs'), get('/api/v1/admin/api-clients')]) }
  catch (e) { message((e as Error).message, true) }
}

async function createProvider() {
  try { await mutate('/api/v1/admin/provider-configs', 'POST', newProvider.value); message('解析提供方已创建'); providers.value = await get('/api/v1/admin/provider-configs') }
  catch (e) { message((e as Error).message, true) }
}

async function createClient() {
  try { const result = await mutate<any>('/api/v1/admin/api-clients', 'POST', newClient.value); issuedCredential.value = result.credential; message('API 客户端已创建，请立即保存凭据'); clients.value = await get('/api/v1/admin/api-clients') }
  catch (e) { message((e as Error).message, true) }
}

async function rotateClient(client: any) {
  try { const result = await mutate<any>(`/api/v1/admin/api-clients/${client.id}/rotate`, 'POST'); issuedCredential.value = result.credential; message('新凭据仅显示一次，请立即保存') }
  catch (e) { message((e as Error).message, true) }
}

async function revokeClient(client: any) {
  if (!confirm(`确认吊销 API 客户端“${client.name}”？`)) return
  try { await mutate(`/api/v1/admin/api-clients/${client.id}`, 'DELETE'); clients.value = await get('/api/v1/admin/api-clients'); message('API 客户端已吊销') }
  catch (e) { message((e as Error).message, true) }
}

async function toggleProvider(provider: any) {
  try {
    await mutate(`/api/v1/admin/provider-configs/${provider.id}`, 'PUT', { name: provider.name, providerType: provider.providerType, baseUrl: provider.baseUrl, model: provider.model, backend: provider.backend, credential: null, clearCredential: false, isEnabled: !provider.isEnabled, isDefault: provider.isDefault })
    providers.value = await get('/api/v1/admin/provider-configs'); message(provider.isEnabled ? '解析提供方已停用' : '解析提供方已启用')
  } catch (e) { message((e as Error).message, true) }
}

onMounted(() => initialize().catch(e => message(e.message, true)))
</script>

<template>
  <div v-if="!session" class="loading">正在连接 StructaDoc…</div>
  <main v-else-if="!session.authenticated" class="auth-shell">
    <section class="auth-story">
      <div class="brand-mark">S</div><p class="eyebrow">STRUCTADOC</p>
      <h1>让文档结构<br>成为可用的数据。</h1>
      <p>上传、解析、检查与导出，都在一个面向使用者的工作空间里。身份由标准 OIDC 接入，StructaDoc 不绑定任何特定身份平台。</p>
      <div class="trust-line"><span>稳定结果契约</span><span>可恢复任务</span><span>完整清理</span></div>
    </section>
    <section class="login-card">
      <p class="eyebrow">欢迎回来</p><h2>进入文档工作台</h2>
      <a v-if="session.oidcEnabled" class="primary button-link" href="/api/v1/session/login?returnUrl=/">使用组织账号登录</a>
      <div v-if="session.oidcEnabled" class="divider"><span>或使用本地应急管理员</span></div>
      <form @submit.prevent="localLogin">
        <label>管理员邮箱<input v-model="email" type="email" autocomplete="username" required></label>
        <label>密码<input v-model="password" type="password" autocomplete="current-password" required></label>
        <button class="secondary" :disabled="busy">{{ busy ? '登录中…' : '管理员登录' }}</button>
      </form>
      <p class="login-note">本地管理员用于引导配置与身份平台故障时的应急访问。</p>
    </section>
  </main>

  <div v-else class="app-shell">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark small">S</span><strong>StructaDoc</strong></div>
      <nav>
        <button :class="{ active: view === 'documents' }" @click="view = 'documents'">文档工作台</button>
        <button v-if="canAdmin" :class="{ active: view === 'admin' }" @click="loadAdmin">系统管理</button>
      </nav>
      <div class="account"><span class="avatar">{{ (session.displayName || session.email || 'U').slice(0, 1).toUpperCase() }}</span><div><strong>{{ session.displayName || session.email || '用户' }}</strong><small>{{ session.isAdministrator ? '管理员' : '工作空间成员' }}</small></div><button title="退出登录" @click="logout">退出</button></div>
    </aside>

    <main class="content">
      <header class="page-header"><div><p class="eyebrow">{{ view === 'documents' ? 'WORKSPACE' : 'ADMINISTRATION' }}</p><h1>{{ view === 'documents' ? '你的文档' : '系统管理' }}</h1><p>{{ view === 'documents' ? '从原始文件到结构化结果，过程和产物都清晰可见。' : '管理解析提供方与服务 API 客户端。' }}</p></div></header>
      <div v-if="error" class="toast error">{{ error }}</div><div v-if="notice" class="toast">{{ notice }}</div>

      <template v-if="view === 'documents'">
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
              <div class="export-row"><span>导出</span><a v-for="format in ['markdown','html','zip','pdf']" :key="format" :href="`/api/v1/parse-runs/${selectedRun.id}/exports/${format}`">{{ format.toUpperCase() }}</a></div>
              <div class="result-tabs"><div class="result-title"><h3>结构化内容</h3><span>{{ pages.length }} 页</span><span>{{ blocks.length }} 块</span><span>{{ assets.length }} 资源</span><span>{{ artifacts.length }} 制品</span></div><div v-if="markdown" class="markdown-preview"><pre>{{ markdown }}</pre></div><div v-else class="block-list"><article v-for="block in blocks" :key="block.id"><span>#{{ block.sequence }} · {{ block.type }}<template v-if="block.pageNumber"> · P{{ block.pageNumber }}</template></span><p>{{ block.content || '（无文本内容）' }}</p></article><p v-if="!blocks.length" class="muted">结果尚未生成或不含文本块。</p></div><details v-if="assets.length || artifacts.length" class="resource-list"><summary>资源与制品下载</summary><a v-for="asset in assets" :key="asset.id" :href="`/api/v1/parse-runs/${selectedRun.id}/assets/${asset.id}/content`">{{ asset.name }} · {{ prettyBytes(asset.sizeBytes) }}</a><a v-for="artifact in artifacts" :key="artifact.id" :href="`/api/v1/parse-runs/${selectedRun.id}/artifacts/${artifact.id}/content`">{{ artifact.type }} · {{ artifact.name }}</a></details></div>
            </template>
            <details v-if="selectedDocument.ownedByCurrentUser || canAdmin" class="share-box"><summary>共享访问</summary><label>OIDC Issuer<input v-model="share.issuer" placeholder="https://identity.example.com"></label><label>Subject<input v-model="share.subject" placeholder="用户的 sub"></label><fieldset><legend>权限</legend><label v-for="permission in ['read','write','parse','export','delete','share']" :key="permission"><input v-model="share.permissions" type="checkbox" :value="permission"> {{ permission }}</label></fieldset><button class="secondary" @click="grantAccess">保存授权</button></details>
          </section>
          <section v-else class="panel detail empty-detail"><span class="big-number">01</span><h2>选择一个文档</h2><p>查看解析历史、规范化结果、资源文件和导出选项。</p></section>
        </div>
      </template>

      <template v-else>
        <div class="admin-grid">
          <section class="panel admin-card"><p class="eyebrow">PROVIDERS</p><h2>解析提供方</h2><div class="admin-list"><div v-for="provider in providers" :key="provider.id"><span><strong>{{ provider.name }}</strong><small>{{ provider.providerType }} · {{ provider.baseUrl }}</small></span><span class="row-actions"><span class="status" :class="provider.isEnabled ? 'succeeded' : ''">{{ provider.isEnabled ? '启用' : '停用' }}</span><button @click="toggleProvider(provider)">{{ provider.isEnabled ? '停用' : '启用' }}</button></span></div></div><details><summary>新增提供方</summary><div class="form-grid"><label>名称<input v-model="newProvider.name"></label><label>类型<input v-model="newProvider.providerType"></label><label class="wide">服务地址<input v-model="newProvider.baseUrl" type="url"></label><label class="wide">凭据<input v-model="newProvider.credential" type="password"></label><label><input v-model="newProvider.isDefault" type="checkbox"> 设为默认</label><button class="primary" @click="createProvider">创建</button></div></details></section>
          <section class="panel admin-card"><p class="eyebrow">API CLIENTS</p><h2>服务客户端</h2><div class="admin-list"><div v-for="client in clients" :key="client.id"><span><strong>{{ client.name }}</strong><small>{{ client.scopes.join(' · ') }}</small></span><span class="row-actions"><span class="status" :class="client.isActive ? 'succeeded' : 'failed'">{{ client.isActive ? '有效' : '已吊销' }}</span><button v-if="client.isActive" @click="rotateClient(client)">轮换</button><button v-if="client.isActive" class="danger-link" @click="revokeClient(client)">吊销</button></span></div></div><details><summary>新增 API 客户端</summary><div class="form-grid"><label class="wide">名称<input v-model="newClient.name"></label><button class="primary" @click="createClient">创建并签发凭据</button><div v-if="issuedCredential" class="credential wide"><strong>仅显示一次</strong><code>{{ issuedCredential }}</code></div></div></details></section>
        </div>
      </template>
    </main>
  </div>
</template>

<style scoped>
.result-title,.row-actions{display:flex;align-items:center;gap:8px}.result-title h3{margin-right:auto}.result-title span{font-size:10px;color:#57816e;background:#e7eee9;padding:4px 7px;border-radius:20px}.resource-list{border-top:1px solid #d8ded9;padding-top:12px;margin-top:14px}.resource-list summary{font-size:12px;font-weight:700;cursor:pointer;margin-bottom:10px}.resource-list a{display:block;color:#123c2b;font-size:12px;padding:8px 0;border-bottom:1px solid #e8ebe8;text-decoration:none}.row-actions button{border:0;background:transparent;color:#37664f;font-size:11px;padding:3px}.row-actions .danger-link{color:#a23b36}.share-box fieldset{border:0;padding:8px 0 14px;display:flex;flex-wrap:wrap;gap:8px 14px}.share-box legend{font-size:12px;font-weight:700}.share-box fieldset label{display:flex;align-items:center;gap:5px;font-size:11px}.share-box fieldset input{width:auto}
</style>
