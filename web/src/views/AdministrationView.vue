<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { get, mutate } from '../api'
import { message } from '../messages'

const providers = ref<any[]>([])
const clients = ref<any[]>([])
const administrators = ref<any[]>([])
// The service accepts a closed set of Provider types, so this is a choice rather than something to
// spell. A typed one would only ever come back as an English validation error.
const providerTypes = [
  { value: 'mineru-local', label: 'MinerU 本地服务（自建，文档不出网）' },
  { value: 'mineru-cloud', label: 'MinerU 云端服务（文档会上传到外部）' },
]
type ProviderDraft = { id?: string; name: string; providerType: string; baseUrl: string; model: string; backend: string; credential: string; clearCredential: boolean; isEnabled: boolean; isDefault: boolean; hasCredential: boolean }
function blankProvider(): ProviderDraft { return { name: '', providerType: 'mineru-local', baseUrl: '', model: '', backend: '', credential: '', clearCredential: false, isEnabled: true, isDefault: false, hasCredential: false } }
const newProvider = ref<ProviderDraft>(blankProvider())
const providerDraft = ref<ProviderDraft | null>(null)
// The workspace starts a parse without naming a Provider, so a deployment with no enabled default
// has a button that can only fail. Saying so here is cheaper than reading that failure.
const hasDefaultProvider = computed(() => providers.value.some(provider => provider.isDefault && provider.isEnabled))
const newClient = ref({ name: '', scopes: ['documents:read', 'documents:write', 'parses:read', 'parses:write'] })
const newAdministrator = ref({ username: '', displayName: '', password: '' })
const ownPassword = ref({ currentPassword: '', newPassword: '', confirmPassword: '' })
const issuedCredential = ref('')
// Which build is running, shown rather than left to `docker image inspect`: the administrator who
// needs it during a "this machine behaves differently" call is the one who cannot reach the machine.
const serviceVersion = ref('')
// The service reports `<version>+<commit>` with the commit at full length, which is what the registry
// tag uses and what a deployment should be pinned to. Seven characters are enough to read at a
// glance; the full string stays on the element so it can be hovered and copied.
const versionLabel = computed(() => {
  const [version, revision] = serviceVersion.value.split('+')
  return revision ? `${version} · 构建 ${revision.slice(0, 7)}` : version
})

type Setting = { key: string; kind: string; value: string; requiresRestart: boolean; isManagedExternally: boolean; isStored: boolean; isPendingRestart: boolean; minimum: number; maximum: number; allowedValues: string[] }
const settings = ref<Setting[]>([])
// The banner follows what the server reports rather than what this tab did, so a change another
// administrator made is not silently forgotten by a reload.
const restartPending = computed(() => settings.value.some(setting => setting.isPendingRestart))
const restarting = ref(false)
const settingLabels: Record<string, string> = {
  'Worker:ExecutionEnabled': '启用解析执行',
  'Worker:MaxConcurrency': '并发解析数',
  'Documents:UploadApiEnabled': '开放上传接口',
  'Documents:MaxUploadBytes': '单文件上传上限（字节）',
  'Oidc:Enabled': '启用组织账号登录',
  'Oidc:Authority': '身份提供方地址',
  'Oidc:ClientId': '客户端 ID',
  'Oidc:ClientSecret': '客户端密钥',
  'Oidc:RequireHttpsMetadata': '要求 HTTPS 元数据',
  'Oidc:NameClaim': '姓名 claim',
  'Oidc:EmailClaim': '邮箱 claim',
  'Oidc:RoleClaim': '角色 claim',
  'Oidc:AdministratorRole': '管理员角色值',
  'Storage:Provider': '存储方式',
  'Storage:RootPath': '本地目录',
  'Storage:ServiceUrl': '服务地址',
  'Storage:Region': '区域',
  'Storage:Bucket': '存储桶',
  'Storage:Prefix': '路径前缀',
  'Storage:AccessKey': 'Access Key',
  'Storage:SecretKey': 'Secret Key',
  'Storage:ForcePathStyle': '强制路径风格',
  'Database:Provider': '数据库类型',
  'Database:ConnectionString': '连接字符串',
  'Database:ServerVersion': '服务器版本',
}

// Where documents are kept and where business data lives get panels of their own rather than rows
// among the service settings: they carry secrets that must not be rendered as ordinary text boxes,
// and they are the only settings whose wrong value leaves nothing else working.
const storageKeys = ['Storage:Provider', 'Storage:RootPath', 'Storage:ServiceUrl', 'Storage:Region', 'Storage:Bucket', 'Storage:Prefix', 'Storage:AccessKey', 'Storage:SecretKey', 'Storage:ForcePathStyle']
const databaseKeys = ['Database:Provider', 'Database:ConnectionString', 'Database:ServerVersion']
const storageProviderLabels: Record<string, string> = { Local: '本地目录（容器卷）', S3: 'S3 兼容对象存储' }
const databaseProviderLabels: Record<string, string> = { Sqlite: 'SQLite（单实例，随容器卷）', PostgreSql: 'PostgreSQL 17', MySql: 'MySQL 8.4', MariaDb: 'MariaDB 11.4' }

type StorageStatus = { provider: string; startupFault: string | null; hasCredential: boolean }
type DatabaseStatus = { provider: string; startupFault: string | null; isReachable: boolean; hasPendingMigrations: boolean }
const storageStatus = ref<StorageStatus | null>(null)
const databaseStatus = ref<DatabaseStatus | null>(null)
// Secrets are typed here and forgotten by this page. The service never sends one back, so leaving it
// in the field would make the browser the only place it exists.
const storageAccessKeyDraft = ref('')
const storageSecretKeyDraft = ref('')
const databaseConnectionDraft = ref('')
const storageTestResult = ref('')
const databaseTestResult = ref('')
const storageTesting = ref(false)
const databaseTesting = ref(false)

const storageTestMessages: Record<string, string> = {
  Writable: '连接成功：该位置可以写入',
  InvalidConfiguration: '配置不完整或不合法，请检查必填项',
  Unreachable: '无法连接到该地址',
  AccessDenied: '凭据被拒绝，或没有写入权限',
  BucketNotFound: '存储桶不存在',
  NotWritable: '可以连接，但无法写入',
  TimedOut: '连接超时',
}

const databaseTestMessages: Record<string, string> = {
  Reachable: '连接成功：数据库可用，表结构已是最新',
  ReachableWithPendingMigrations: '连接成功：数据库可用，重启后会自动补齐缺少的表结构',
  InvalidConfiguration: '配置不完整或不合法，请检查必填项',
  Unreachable: '无法连接到该数据库',
  TimedOut: '连接超时',
}

// The identity provider is the only way an end user reaches the workspace, so it gets a panel of its
// own rather than a row among the service settings.
type OidcStatus = { enabled: boolean; startupFault: string | null; callbackPath: string; signedOutCallbackPath: string; scopes: string[] }
const oidcStatus = ref<OidcStatus | null>(null)
const oidcSecretDraft = ref('')
const oidcAuthorityDraft = ref('')
const oidcTestResult = ref('')
const oidcTesting = ref(false)
const oidcClaimKeys = ['Oidc:NameClaim', 'Oidc:EmailClaim', 'Oidc:RoleClaim', 'Oidc:AdministratorRole']
const serviceSettings = computed(() => settings.value.filter(setting => !setting.key.startsWith('Oidc:') && !storageKeys.includes(setting.key) && !databaseKeys.includes(setting.key)))
const usesObjectStorage = computed(() => settingOf('Storage:Provider')?.value === 'S3')
const needsServerVersion = computed(() => ['MySql', 'MariaDb'].includes(settingOf('Database:Provider')?.value ?? ''))
// Composed in the browser rather than by the service: behind a reverse proxy only the browser knows
// the address a user actually reaches, and registering the wrong one at the provider fails the
// sign-in at its last step.
const oidcRedirectUri = computed(() => oidcStatus.value ? location.origin + oidcStatus.value.callbackPath : '')
const oidcSignedOutUri = computed(() => oidcStatus.value ? location.origin + oidcStatus.value.signedOutCallbackPath : '')
function settingOf(key: string) { return settings.value.find(setting => setting.key === key) }

// The service answers with a stable code so it does not have to guess the reader's language, and
// carries the deployment-specific part separately.
const oidcTestMessages: Record<string, string> = {
  Reachable: '连接成功：发现文档可读，issuer 与填写的地址一致',
  InvalidAuthority: '地址无效，需要是以 http:// 或 https:// 开头的完整地址',
  InsecureAuthority: '当前要求 HTTPS 元数据，该地址不是 https',
  Unreachable: '无法连接到该地址',
  TimedOut: '连接超时',
  HttpError: '该地址返回了错误状态码',
  MalformedDocument: '该地址返回的不是合法 JSON',
  IncompleteDocument: '返回的文档缺少 OIDC 必需字段，该地址可能不是身份提供方',
  IssuerMismatch: '该地址的发现文档声明了不同的 issuer，登录时每个令牌都会被拒绝',
}

// Loaded independently rather than together. Providers and API clients live in the business
// database, and this page is where a deployment pointed at an unreachable one is repaired: if one
// failed read could blank the page, the settings needed to fix it would go with it.
async function load() {
  const sources = ['/api/v1/admin/settings', '/api/v1/admin/settings/oidc', '/api/v1/admin/settings/storage', '/api/v1/admin/settings/database', '/api/v1/admin/administrators', '/api/v1/admin/provider-configs', '/api/v1/admin/api-clients', '/api/v1/system/info']
  const results = await Promise.allSettled(sources.map(source => get(source)))
  const value = <T,>(index: number, fallback: T): T => results[index].status === 'fulfilled' ? (results[index] as PromiseFulfilledResult<T>).value : fallback
  settings.value = value(0, settings.value)
  oidcStatus.value = value(1, oidcStatus.value)
  storageStatus.value = value(2, storageStatus.value)
  databaseStatus.value = value(3, databaseStatus.value)
  administrators.value = value(4, [])
  providers.value = value(5, [])
  clients.value = value(6, [])
  serviceVersion.value = value<{ version?: string } | null>(7, null)?.version ?? ''
  oidcAuthorityDraft.value = settingOf('Oidc:Authority')?.value ?? ''

  // Only a failure that is not already explained by the banners is worth a toast.
  const unexplained = results.slice(0, 5).find(result => result.status === 'rejected')
  if (unexplained) message((unexplained as PromiseRejectedResult).reason?.message ?? '部分设置读取失败', true)
}

async function saveSetting(setting: Setting, value: string) {
  try {
    const result = await mutate<{ restartRequired: boolean }>('/api/v1/admin/settings', 'PUT', { key: setting.key, value })
    settings.value = await get('/api/v1/admin/settings')
    message(result.restartRequired ? '已保存，需重启服务后生效' : '已保存并生效')
  } catch (e) { message((e as Error).message, true); settings.value = await get('/api/v1/admin/settings') }
}

async function saveSettingByKey(key: string, value: string) {
  const setting = settingOf(key)
  if (setting) await saveSetting(setting, value)
}

// Written from the draft and then forgotten by this page. The service never sends a secret back, so
// leaving it in the field would be the only copy of it in a browser.
async function saveOidcSecret() { await saveSecret('Oidc:ClientSecret', oidcSecretDraft) }

// Every secret follows the same path: written from a draft, then forgotten by this page.
async function saveSecret(key: string, draft: { value: string }) {
  if (!draft.value) return
  await saveSettingByKey(key, draft.value)
  draft.value = ''
}

// The two halves of a storage credential are written and cleared together, because the service
// refuses a configuration that has one without the other.
async function saveStorageCredential() {
  await saveSecret('Storage:AccessKey', storageAccessKeyDraft)
  await saveSecret('Storage:SecretKey', storageSecretKeyDraft)
}

async function clearStorageCredential() {
  await saveSettingByKey('Storage:AccessKey', '')
  await saveSettingByKey('Storage:SecretKey', '')
}

async function saveDatabaseConnection() { await saveSecret('Database:ConnectionString', databaseConnectionDraft) }

// Tested before it is saved, and with what is typed rather than what is stored. An omitted field
// falls back to what is in force, so a bucket name can be checked without retyping a Secret Key the
// service never sends back.
async function testStorage() {
  storageTesting.value = true
  storageTestResult.value = ''
  try {
    const result = await mutate<{ succeeded: boolean; code: string; detail: string }>('/api/v1/admin/settings/storage/test', 'POST', {
      provider: settingOf('Storage:Provider')?.value,
      rootPath: settingOf('Storage:RootPath')?.value,
      serviceUrl: settingOf('Storage:ServiceUrl')?.value,
      region: settingOf('Storage:Region')?.value,
      bucket: settingOf('Storage:Bucket')?.value,
      prefix: settingOf('Storage:Prefix')?.value,
      accessKey: storageAccessKeyDraft.value || null,
      secretKey: storageSecretKeyDraft.value || null,
      forcePathStyle: settingOf('Storage:ForcePathStyle')?.value === 'true',
    })
    storageTestResult.value = (storageTestMessages[result.code] ?? result.code) + (result.detail ? `（${result.detail}）` : '')
  } catch (e) { storageTestResult.value = (e as Error).message }
  finally { storageTesting.value = false }
}

async function testDatabase() {
  databaseTesting.value = true
  databaseTestResult.value = ''
  try {
    const result = await mutate<{ succeeded: boolean; code: string; detail: string }>('/api/v1/admin/settings/database/test', 'POST', {
      provider: settingOf('Database:Provider')?.value,
      connectionString: databaseConnectionDraft.value || null,
      serverVersion: settingOf('Database:ServerVersion')?.value,
    })
    databaseTestResult.value = (databaseTestMessages[result.code] ?? result.code) + (result.detail ? `（${result.detail}）` : '')
  } catch (e) { databaseTestResult.value = (e as Error).message }
  finally { databaseTesting.value = false }
}

// Tests what is typed rather than what is stored, so a wrong address is caught before it is saved.
// Only the address is checked; whether the identity provider accepts the client id and secret cannot
// be known without completing a sign-in.
async function testOidc() {
  oidcTesting.value = true
  oidcTestResult.value = ''
  try {
    const result = await mutate<{ succeeded: boolean; code: string; detail: string; issuer: string | null }>('/api/v1/admin/settings/oidc/test', 'POST', { authority: oidcAuthorityDraft.value, requireHttpsMetadata: settingOf('Oidc:RequireHttpsMetadata')?.value !== 'false' })
    const detail = result.detail ? `（${result.detail}）` : ''
    const issuer = !result.succeeded && result.issuer ? ` issuer=${result.issuer}` : ''
    oidcTestResult.value = (oidcTestMessages[result.code] ?? result.code) + detail + issuer
  } catch (e) { oidcTestResult.value = (e as Error).message }
  finally { oidcTesting.value = false }
}

// The Host can only stop itself; what brings it back is the container restart policy. Saying so in
// the confirmation is the only warning a user gets before a deployment without one stays down.
async function restart() {
  if (!confirm('确认重启服务？只有在容器带 --restart unless-stopped 等重启策略启动时才会自动恢复，否则需要手动重新启动容器。')) return
  try {
    await mutate('/api/v1/admin/system/restart', 'POST')
    restarting.value = true
    for (let attempt = 0; attempt < 60; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 1000))
      try { await get('/api/v1/system/info'); location.reload(); return } catch { /* still down */ }
    }
    restarting.value = false
    message('服务未在 60 秒内恢复，请检查容器重启策略', true)
  } catch (e) { message((e as Error).message, true) }
}

async function reloadAdministrators() { administrators.value = await get('/api/v1/admin/administrators') }

async function createAdministrator() {
  try { await mutate('/api/v1/admin/administrators', 'POST', newAdministrator.value); newAdministrator.value = { username: '', displayName: '', password: '' }; await reloadAdministrators(); message('管理员已创建') }
  catch (e) { message((e as Error).message, true) }
}

async function changeOwnPassword() {
  if (ownPassword.value.newPassword !== ownPassword.value.confirmPassword) { message('两次输入的新密码不一致', true); return }
  try {
    await mutate('/api/v1/admin/administrators/me/password', 'POST', { currentPassword: ownPassword.value.currentPassword, newPassword: ownPassword.value.newPassword })
    ownPassword.value = { currentPassword: '', newPassword: '', confirmPassword: '' }
    message('密码已修改，其他设备上的登录已失效')
  } catch (e) { message((e as Error).message, true) }
}

async function resetPassword(administrator: any) {
  const password = prompt(`为“${administrator.displayName}”设置新密码（至少 8 位）`)
  if (!password) return
  try { await mutate(`/api/v1/admin/administrators/${administrator.id}/password`, 'POST', { newPassword: password }); message('密码已重置，该管理员的登录已失效') }
  catch (e) { message((e as Error).message, true) }
}

async function toggleAdministrator(administrator: any) {
  try { await mutate(`/api/v1/admin/administrators/${administrator.id}/active`, 'PUT', { isActive: !administrator.isActive }); await reloadAdministrators(); message(administrator.isActive ? '管理员已停用' : '管理员已启用') }
  catch (e) { message((e as Error).message, true) }
}

async function deleteAdministrator(administrator: any) {
  if (!confirm(`确认删除管理员“${administrator.displayName}”？删除后无法恢复，停用则可随时启用。`)) return
  try { await mutate(`/api/v1/admin/administrators/${administrator.id}`, 'DELETE'); await reloadAdministrators(); message('管理员已删除') }
  catch (e) { message((e as Error).message, true) }
}

function providerTypeLabel(providerType: string) { return providerTypes.find(type => type.value === providerType)?.label ?? providerType }

async function reloadProviders() { providers.value = await get('/api/v1/admin/provider-configs') }

// Optional text fields are sent as null rather than as an empty string: the service treats blank as
// "not set", and an empty string would be a value it has to reject.
function providerPayload(draft: ProviderDraft) {
  return {
    name: draft.name,
    providerType: draft.providerType,
    baseUrl: draft.baseUrl,
    model: draft.model || null,
    backend: draft.backend || null,
    // Clearing and setting are mutually exclusive; sending both is refused.
    credential: draft.clearCredential ? null : draft.credential || null,
    clearCredential: draft.clearCredential,
    isEnabled: draft.isEnabled,
    // The service refuses a configuration that is disabled and default at once, so the form cannot
    // submit that pair however the two boxes were left.
    isDefault: draft.isEnabled && draft.isDefault,
  }
}

async function createProvider() {
  try { await mutate('/api/v1/admin/provider-configs', 'POST', providerPayload(newProvider.value)); newProvider.value = blankProvider(); message('解析提供方已创建'); await reloadProviders() }
  catch (e) { message((e as Error).message, true) }
}

// The stored credential never comes back from the service, so the field starts empty and an empty
// field means "leave it as it is". Erasing one is a separate, deliberate checkbox.
function editProvider(provider: any) {
  providerDraft.value = { id: provider.id, name: provider.name, providerType: provider.providerType, baseUrl: provider.baseUrl, model: provider.model ?? '', backend: provider.backend ?? '', credential: '', clearCredential: false, isEnabled: provider.isEnabled, isDefault: provider.isDefault, hasCredential: provider.hasCredential }
}

async function saveProvider() {
  const draft = providerDraft.value
  if (!draft) return
  try { await mutate(`/api/v1/admin/provider-configs/${draft.id}`, 'PUT', providerPayload(draft)); providerDraft.value = null; await reloadProviders(); message('解析提供方已更新') }
  catch (e) { message((e as Error).message, true) }
}

// A row action replaces the whole configuration, so every field is resent unchanged. The credential
// is deliberately omitted: a row action must never overwrite one that is already stored.
async function writeProvider(provider: any, changes: Record<string, unknown>, notice: string) {
  try {
    await mutate(`/api/v1/admin/provider-configs/${provider.id}`, 'PUT', { name: provider.name, providerType: provider.providerType, baseUrl: provider.baseUrl, model: provider.model, backend: provider.backend, credential: null, clearCredential: false, isEnabled: provider.isEnabled, isDefault: provider.isDefault, ...changes })
    await reloadProviders(); message(notice)
  } catch (e) { message((e as Error).message, true) }
}

async function deleteProvider(provider: any) {
  if (!confirm(`确认删除解析提供方“${provider.name}”？已经用它解析过的文档无法删除它，停用即可阻止新任务使用。`)) return
  try { await mutate(`/api/v1/admin/provider-configs/${provider.id}`, 'DELETE'); if (providerDraft.value?.id === provider.id) providerDraft.value = null; await reloadProviders(); message('解析提供方已删除') }
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

// Stopping the default Provider gives up its default marker with it: the service refuses a
// configuration that is disabled and default at once, so keeping the flag would fail the write.
function toggleProvider(provider: any) {
  return writeProvider(
    provider,
    provider.isEnabled ? { isEnabled: false, isDefault: false } : { isEnabled: true },
    provider.isEnabled ? '解析提供方已停用' : '解析提供方已启用')
}

function makeDefaultProvider(provider: any) {
  return writeProvider(provider, { isDefault: true, isEnabled: true }, '已设为默认解析提供方')
}

onMounted(() => load())
</script>

<template>
  <header class="page-header"><div><p class="eyebrow">ADMINISTRATION</p><h1>系统管理</h1><p>管理服务设置、本地管理员、解析提供方与服务 API 客户端。</p><p v-if="versionLabel" class="hint">当前运行版本 <code :title="serviceVersion">{{ versionLabel }}</code></p></div></header>

  <div class="admin-grid">
    <section class="panel admin-card wide-card">
      <p class="eyebrow">SERVICE SETTINGS</p><h2>服务设置</h2>
      <div v-if="restartPending" class="notice-banner"><div>部分设置需重启服务后才会生效。</div><button :disabled="restarting" @click="restart">{{ restarting ? '正在重启…' : '立即重启' }}</button></div>
      <div class="admin-list">
        <div v-for="setting in serviceSettings" :key="setting.key">
          <span><strong>{{ settingLabels[setting.key] || setting.key }}</strong><small>{{ setting.key }}<template v-if="setting.isManagedExternally"> · 由部署环境变量固定</template><template v-else-if="!setting.isStored"> · 使用默认值</template><template v-if="setting.isPendingRestart"> · 待重启生效</template></small></span>
          <span class="row-actions">
            <template v-if="setting.kind === 'Boolean'">
              <span class="status" :class="setting.value === 'true' ? 'succeeded' : ''">{{ setting.value === 'true' ? '开启' : '关闭' }}</span>
              <button v-if="!setting.isManagedExternally" @click="saveSetting(setting, setting.value === 'true' ? 'false' : 'true')">{{ setting.value === 'true' ? '关闭' : '开启' }}</button>
            </template>
            <template v-else-if="setting.allowedValues.length">
              <select class="setting-input" :value="setting.value" :disabled="setting.isManagedExternally" @change="saveSetting(setting, ($event.target as HTMLSelectElement).value)"><option v-for="allowed in setting.allowedValues" :key="allowed" :value="allowed">{{ allowed }}</option></select>
            </template>
            <template v-else>
              <input class="setting-input" :value="setting.value" :disabled="setting.isManagedExternally" @change="saveSetting(setting, ($event.target as HTMLInputElement).value)">
            </template>
            <button v-if="setting.isStored && !setting.isManagedExternally" @click="saveSetting(setting, '')">恢复默认</button>
          </span>
        </div>
      </div>
    </section>

    <section class="panel admin-card" v-if="settingOf('Storage:Provider')">
      <p class="eyebrow">STORAGE</p><h2>文件存储</h2>
      <p class="hint">原始文档、解析结果与导出文件的存放位置。修改后需重启服务，已经存放的文件不会自动迁移。</p>
      <div v-if="storageStatus?.startupFault" class="notice-banner"><div>保存的存储配置在服务启动时被拒绝，当前未生效：{{ storageStatus.startupFault }}</div></div>
      <div class="form-grid">
        <label class="wide">存储方式<select :value="settingOf('Storage:Provider')!.value" :disabled="settingOf('Storage:Provider')!.isManagedExternally" @change="saveSettingByKey('Storage:Provider', ($event.target as HTMLSelectElement).value)"><option v-for="allowed in settingOf('Storage:Provider')!.allowedValues" :key="allowed" :value="allowed">{{ storageProviderLabels[allowed] || allowed }}</option></select><small>当前运行中：{{ storageProviderLabels[storageStatus?.provider || ''] || storageStatus?.provider }}</small></label>
        <label v-if="!usesObjectStorage" class="wide">本地目录<input :value="settingOf('Storage:RootPath')!.value" :disabled="settingOf('Storage:RootPath')!.isManagedExternally" @change="saveSettingByKey('Storage:RootPath', ($event.target as HTMLInputElement).value)"><small>必须是容器中持久卷内的路径，否则重启后文件会丢失</small></label>
        <template v-else>
          <label class="wide">服务地址<input :value="settingOf('Storage:ServiceUrl')!.value" type="url" placeholder="https://s3.example.com" :disabled="settingOf('Storage:ServiceUrl')!.isManagedExternally" @change="saveSettingByKey('Storage:ServiceUrl', ($event.target as HTMLInputElement).value)"><small>留空表示使用 AWS 官方地址</small></label>
          <label>存储桶<input :value="settingOf('Storage:Bucket')!.value" :disabled="settingOf('Storage:Bucket')!.isManagedExternally" @change="saveSettingByKey('Storage:Bucket', ($event.target as HTMLInputElement).value)"></label>
          <label>区域<input :value="settingOf('Storage:Region')!.value" placeholder="us-east-1" :disabled="settingOf('Storage:Region')!.isManagedExternally" @change="saveSettingByKey('Storage:Region', ($event.target as HTMLInputElement).value)"></label>
          <label>路径前缀<input :value="settingOf('Storage:Prefix')!.value" :disabled="settingOf('Storage:Prefix')!.isManagedExternally" @change="saveSettingByKey('Storage:Prefix', ($event.target as HTMLInputElement).value)"></label>
          <label><input type="checkbox" :checked="settingOf('Storage:ForcePathStyle')!.value === 'true'" :disabled="settingOf('Storage:ForcePathStyle')!.isManagedExternally" @change="saveSettingByKey('Storage:ForcePathStyle', ($event.target as HTMLInputElement).checked ? 'true' : 'false')"> 强制路径风格<small>MinIO 等自建服务通常需要开启</small></label>
          <label>Access Key<input v-model="storageAccessKeyDraft" type="password" autocomplete="off" :placeholder="settingOf('Storage:AccessKey')!.isStored ? '已设置（不回显）' : '未设置'" :disabled="settingOf('Storage:AccessKey')!.isManagedExternally"></label>
          <label>Secret Key<input v-model="storageSecretKeyDraft" type="password" autocomplete="new-password" :placeholder="settingOf('Storage:SecretKey')!.isStored ? '已设置（不回显）' : '未设置'" :disabled="settingOf('Storage:SecretKey')!.isManagedExternally"></label>
          <span class="row-actions wide"><button :disabled="!storageAccessKeyDraft && !storageSecretKeyDraft" @click="saveStorageCredential">保存凭据</button><button v-if="settingOf('Storage:AccessKey')!.isStored" class="danger-link" @click="clearStorageCredential">清除凭据</button></span>
        </template>
        <span class="row-actions wide"><button :disabled="storageTesting" @click="testStorage">{{ storageTesting ? '正在测试…' : '测试写入' }}</button></span>
        <p v-if="storageTestResult" class="hint wide">{{ storageTestResult }}</p>
      </div>
    </section>

    <section class="panel admin-card" v-if="settingOf('Database:Provider')">
      <p class="eyebrow">DATABASE</p><h2>业务数据库</h2>
      <p class="hint">文档元数据、解析记录与结构化结果的存放位置。管理员账号与本页设置存放在独立的控制库中，因此这里配置错误时本页仍可用。</p>
      <div v-if="databaseStatus?.startupFault" class="notice-banner"><div>{{ databaseStatus.startupFault }}</div></div>
      <div v-else-if="databaseStatus && !databaseStatus.isReachable" class="notice-banner"><div>当前数据库无法连接，上传与解析都不可用。</div></div>
      <div class="form-grid">
        <label class="wide">数据库类型<select :value="settingOf('Database:Provider')!.value" :disabled="settingOf('Database:Provider')!.isManagedExternally" @change="saveSettingByKey('Database:Provider', ($event.target as HTMLSelectElement).value)"><option v-for="allowed in settingOf('Database:Provider')!.allowedValues" :key="allowed" :value="allowed">{{ databaseProviderLabels[allowed] || allowed }}</option></select><small>当前运行中：{{ databaseProviderLabels[databaseStatus?.provider || ''] || databaseStatus?.provider }}<template v-if="databaseStatus?.isReachable"> · 可连接</template><template v-if="databaseStatus?.hasPendingMigrations"> · 重启后会补齐表结构</template></small></label>
        <label class="wide">连接字符串<input v-model="databaseConnectionDraft" type="password" autocomplete="new-password" :placeholder="settingOf('Database:ConnectionString')!.isStored ? '已设置（不回显）' : '使用镜像自带的 SQLite 默认值'" :disabled="settingOf('Database:ConnectionString')!.isManagedExternally"><small>其中通常包含密码，因此保存后不会再回显</small></label>
        <label v-if="needsServerVersion" class="wide">服务器版本<input :value="settingOf('Database:ServerVersion')!.value" placeholder="8.4.0" :disabled="settingOf('Database:ServerVersion')!.isManagedExternally" @change="saveSettingByKey('Database:ServerVersion', ($event.target as HTMLInputElement).value)"><small>MySQL 与 MariaDB 必填，服务不会通过连接去猜测</small></label>
        <span class="row-actions wide"><button :disabled="databaseTesting" @click="testDatabase">{{ databaseTesting ? '正在测试…' : '测试连接' }}</button><button class="primary" :disabled="!databaseConnectionDraft" @click="saveDatabaseConnection">保存连接字符串</button><button v-if="settingOf('Database:ConnectionString')!.isStored" class="danger-link" @click="saveSettingByKey('Database:ConnectionString', '')">恢复默认</button></span>
        <p v-if="databaseTestResult" class="hint wide">{{ databaseTestResult }}</p>
        <p class="hint wide">切换数据库不会迁移已有数据。新库会在重启时自动建表，原有文档与解析记录仍留在旧库中。</p>
      </div>
    </section>

    <section class="panel admin-card wide-card">
      <p class="eyebrow">SINGLE SIGN-ON</p><h2>组织账号登录</h2>
      <p class="hint">终端用户只能通过身份提供方登录；未配置时工作区无人可用，管理员仍可从本地账号进入。</p>
      <div v-if="oidcStatus?.startupFault" class="notice-banner"><div>已保存的配置在服务启动时被拒绝，当前未生效：{{ oidcStatus.startupFault }}</div></div>
      <div class="form-grid" v-if="settingOf('Oidc:Enabled')">
        <label class="wide"><input type="checkbox" :checked="settingOf('Oidc:Enabled')!.value === 'true'" :disabled="settingOf('Oidc:Enabled')!.isManagedExternally" @change="saveSettingByKey('Oidc:Enabled', ($event.target as HTMLInputElement).checked ? 'true' : 'false')"> 启用组织账号登录<small>当前运行状态：{{ oidcStatus?.enabled ? '已启用' : '未启用' }}</small></label>
        <label class="wide">身份提供方地址<input v-model="oidcAuthorityDraft" type="url" placeholder="https://id.example.com/realms/main" :disabled="settingOf('Oidc:Authority')!.isManagedExternally" @change="saveSettingByKey('Oidc:Authority', oidcAuthorityDraft)"><small>末尾斜杠会被去掉，保存后应与发现文档中的 issuer 完全一致</small></label>
        <label>客户端 ID<input :value="settingOf('Oidc:ClientId')!.value" :disabled="settingOf('Oidc:ClientId')!.isManagedExternally" @change="saveSettingByKey('Oidc:ClientId', ($event.target as HTMLInputElement).value)"></label>
        <label>客户端密钥<input v-model="oidcSecretDraft" type="password" autocomplete="new-password" :placeholder="settingOf('Oidc:ClientSecret')!.isStored ? '已设置（不回显）' : '未设置'" :disabled="settingOf('Oidc:ClientSecret')!.isManagedExternally"></label>
        <span class="row-actions wide"><button :disabled="!oidcSecretDraft" @click="saveOidcSecret">保存密钥</button><button v-if="settingOf('Oidc:ClientSecret')!.isStored" class="danger-link" @click="saveSettingByKey('Oidc:ClientSecret', '')">清除密钥</button><button :disabled="oidcTesting" @click="testOidc">{{ oidcTesting ? '正在测试…' : '测试连接' }}</button></span>
        <p v-if="oidcTestResult" class="hint wide">{{ oidcTestResult }}</p>
        <label class="wide"><input type="checkbox" :checked="settingOf('Oidc:RequireHttpsMetadata')!.value === 'true'" :disabled="settingOf('Oidc:RequireHttpsMetadata')!.isManagedExternally" @change="saveSettingByKey('Oidc:RequireHttpsMetadata', ($event.target as HTMLInputElement).checked ? 'true' : 'false')"> 要求 HTTPS 元数据<small>仅在内网 http 身份提供方下关闭</small></label>
        <label v-for="key in oidcClaimKeys" :key="key">{{ settingLabels[key] }}<input :value="settingOf(key)!.value" :disabled="settingOf(key)!.isManagedExternally" @change="saveSettingByKey(key, ($event.target as HTMLInputElement).value)"></label>
        <p class="hint wide">在身份提供方处需登记回调地址 <code>{{ oidcRedirectUri }}</code>，注销回调 <code>{{ oidcSignedOutUri }}</code>。请求的 scope 为 <code>{{ oidcStatus?.scopes.join(' ') }}</code>，此项与回调路径不可在此修改。</p>
      </div>
    </section>

    <section class="panel admin-card wide-card"><p class="eyebrow">ADMINISTRATORS</p><h2>管理员账号</h2><div class="admin-list"><div v-for="administrator in administrators" :key="administrator.id"><span><strong>{{ administrator.displayName }}</strong><small>{{ administrator.username }}<template v-if="administrator.isCurrent"> · 当前登录</template></small></span><span class="row-actions"><span class="status" :class="administrator.isActive ? 'succeeded' : 'failed'">{{ administrator.isActive ? '启用' : '停用' }}</span><button v-if="!administrator.isCurrent" @click="resetPassword(administrator)">重置密码</button><button v-if="!administrator.isCurrent" @click="toggleAdministrator(administrator)">{{ administrator.isActive ? '停用' : '启用' }}</button><button v-if="!administrator.isCurrent" class="danger-link" @click="deleteAdministrator(administrator)">删除</button></span></div></div><details><summary>新增管理员</summary><div class="form-grid"><label>用户名<input v-model="newAdministrator.username" autocomplete="off"></label><label>显示名称<input v-model="newAdministrator.displayName" autocomplete="off"></label><label class="wide">密码（至少 8 位）<input v-model="newAdministrator.password" type="password" autocomplete="new-password"></label><button class="primary" @click="createAdministrator">创建</button></div></details><details><summary>修改我的密码</summary><div class="form-grid"><label class="wide">当前密码<input v-model="ownPassword.currentPassword" type="password" autocomplete="current-password"></label><label>新密码<input v-model="ownPassword.newPassword" type="password" autocomplete="new-password"></label><label>确认新密码<input v-model="ownPassword.confirmPassword" type="password" autocomplete="new-password"></label><button class="primary" @click="changeOwnPassword">修改密码</button><p class="hint wide">修改后其他设备上的登录会立即失效，当前设备保持登录。</p></div></details></section>
    <section class="panel admin-card wide-card">
      <p class="eyebrow">PROVIDERS</p><h2>解析提供方</h2>
      <p class="hint">工作台的“开始新解析”总是使用默认提供方。选择云端类型意味着文档会被上传到外部服务。</p>
      <div v-if="providers.length && !hasDefaultProvider" class="notice-banner"><div>当前没有启用中的默认提供方，工作台的“开始新解析”会失败。请为其中一个提供方点击“设为默认”。</div></div>
      <div class="admin-list">
        <div v-for="provider in providers" :key="provider.id">
          <span><strong>{{ provider.name }}</strong><small>{{ providerTypeLabel(provider.providerType) }} · {{ provider.baseUrl }}<template v-if="provider.hasCredential"> · 已设置凭据</template> · 第 {{ provider.versionNumber }} 版</small></span>
          <span class="row-actions">
            <span v-if="provider.isDefault" class="status succeeded">默认</span>
            <span class="status" :class="provider.isEnabled ? 'succeeded' : ''">{{ provider.isEnabled ? '启用' : '停用' }}</span>
            <button @click="editProvider(provider)">编辑</button>
            <button v-if="!provider.isDefault" @click="makeDefaultProvider(provider)">设为默认</button>
            <button @click="toggleProvider(provider)">{{ provider.isEnabled ? '停用' : '启用' }}</button>
            <button class="danger-link" @click="deleteProvider(provider)">删除</button>
          </span>
        </div>
      </div>
      <p v-if="!providers.length" class="hint">还没有解析提供方。新增一个并设为默认后，工作台才能开始解析。</p>

      <div v-if="providerDraft" class="provider-editor">
        <h3>编辑“{{ providerDraft.name }}”</h3>
        <div class="form-grid">
          <label>名称<input v-model="providerDraft.name"></label>
          <label>类型<select v-model="providerDraft.providerType" disabled><option v-for="type in providerTypes" :key="type.value" :value="type.value">{{ type.label }}</option></select><small>类型不可更改，需要更换时请新增一个提供方</small></label>
          <label class="wide">服务地址<input v-model="providerDraft.baseUrl" type="url" placeholder="http://mineru.internal:8000"></label>
          <label>模型（可选）<input v-model="providerDraft.model" placeholder="留空使用服务端默认"></label>
          <label>后端（可选）<input v-model="providerDraft.backend" placeholder="留空使用服务端默认"></label>
          <label class="wide">凭据<input v-model="providerDraft.credential" type="password" autocomplete="new-password" :disabled="providerDraft.clearCredential" :placeholder="providerDraft.hasCredential ? '已设置（不回显），留空即保持不变' : '未设置'"></label>
          <label v-if="providerDraft.hasCredential"><input v-model="providerDraft.clearCredential" type="checkbox"> 清除已保存的凭据</label>
          <label><input v-model="providerDraft.isEnabled" type="checkbox"> 启用</label>
          <label><input v-model="providerDraft.isDefault" type="checkbox" :disabled="!providerDraft.isEnabled"> 设为默认</label>
          <span class="row-actions wide"><button class="primary" @click="saveProvider">保存</button><button class="secondary" @click="providerDraft = null">取消</button></span>
        </div>
      </div>

      <details><summary>新增提供方</summary><div class="form-grid">
        <label>名称<input v-model="newProvider.name"></label>
        <label>类型<select v-model="newProvider.providerType"><option v-for="type in providerTypes" :key="type.value" :value="type.value">{{ type.label }}</option></select></label>
        <label class="wide">服务地址<input v-model="newProvider.baseUrl" type="url" placeholder="http://mineru.internal:8000"></label>
        <label>模型（可选）<input v-model="newProvider.model" placeholder="留空使用服务端默认"></label>
        <label>后端（可选）<input v-model="newProvider.backend" placeholder="留空使用服务端默认"></label>
        <label class="wide">凭据<input v-model="newProvider.credential" type="password" autocomplete="new-password" placeholder="本地服务通常不需要"></label>
        <label><input v-model="newProvider.isDefault" type="checkbox"> 设为默认</label>
        <button class="primary" @click="createProvider">创建</button>
      </div></details>
    </section>
    <section class="panel admin-card"><p class="eyebrow">API CLIENTS</p><h2>服务客户端</h2><div class="admin-list"><div v-for="client in clients" :key="client.id"><span><strong>{{ client.name }}</strong><small>{{ client.scopes.join(' · ') }}</small></span><span class="row-actions"><span class="status" :class="client.isActive ? 'succeeded' : 'failed'">{{ client.isActive ? '有效' : '已吊销' }}</span><button v-if="client.isActive" @click="rotateClient(client)">轮换</button><button v-if="client.isActive" class="danger-link" @click="revokeClient(client)">吊销</button></span></div></div><details><summary>新增 API 客户端</summary><div class="form-grid"><label class="wide">名称<input v-model="newClient.name"></label><button class="primary" @click="createClient">创建并签发凭据</button><div v-if="issuedCredential" class="credential wide"><strong>仅显示一次</strong><code>{{ issuedCredential }}</code></div></div></details></section>
  </div>
</template>

<style scoped>
.row-actions{display:flex;align-items:center;gap:8px}.row-actions button{border:0;background:transparent;color:#37664f;font-size:11px;padding:3px}.row-actions .danger-link{color:#a23b36}
.provider-editor{border-top:1px solid #d8ded9;margin-top:20px;padding-top:18px}.provider-editor h3{font-size:13px;margin:0 0 14px}.provider-editor small{font-weight:400;color:#69766f;font-size:11px}.provider-editor .row-actions button{border:1px solid transparent;font-size:13px;padding:11px 17px}.provider-editor .row-actions .primary{border-color:transparent}.provider-editor .row-actions .secondary{border-color:#aab9b0;color:#123c2b}
</style>
