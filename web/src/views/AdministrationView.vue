<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { get, mutate } from '../api'
import { message } from '../messages'

const providers = ref<any[]>([])
const clients = ref<any[]>([])
const administrators = ref<any[]>([])
const newProvider = ref({ name: '', providerType: 'mineru-local', baseUrl: '', credential: '', model: '', backend: '', isEnabled: true, isDefault: false, clearCredential: false })
const newClient = ref({ name: '', scopes: ['documents:read', 'documents:write', 'parses:read', 'parses:write'] })
const newAdministrator = ref({ username: '', displayName: '', password: '' })
const ownPassword = ref({ currentPassword: '', newPassword: '', confirmPassword: '' })
const issuedCredential = ref('')

type Setting = { key: string; kind: string; value: string; requiresRestart: boolean; isManagedExternally: boolean; isStored: boolean; isPendingRestart: boolean; minimum: number; maximum: number }
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
const serviceSettings = computed(() => settings.value.filter(setting => !setting.key.startsWith('Oidc:')))
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

async function load() {
  try {
    [providers.value, clients.value, administrators.value, settings.value, oidcStatus.value] = await Promise.all([get('/api/v1/admin/provider-configs'), get('/api/v1/admin/api-clients'), get('/api/v1/admin/administrators'), get('/api/v1/admin/settings'), get('/api/v1/admin/settings/oidc')])
    oidcAuthorityDraft.value = settingOf('Oidc:Authority')?.value ?? ''
  }
  catch (e) { message((e as Error).message, true) }
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
async function saveOidcSecret() {
  if (!oidcSecretDraft.value) return
  await saveSettingByKey('Oidc:ClientSecret', oidcSecretDraft.value)
  oidcSecretDraft.value = ''
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

onMounted(() => load())
</script>

<template>
  <header class="page-header"><div><p class="eyebrow">ADMINISTRATION</p><h1>系统管理</h1><p>管理服务设置、本地管理员、解析提供方与服务 API 客户端。</p></div></header>

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
            <template v-else>
              <input class="setting-input" :value="setting.value" :disabled="setting.isManagedExternally" @change="saveSetting(setting, ($event.target as HTMLInputElement).value)">
            </template>
            <button v-if="setting.isStored && !setting.isManagedExternally" @click="saveSetting(setting, '')">恢复默认</button>
          </span>
        </div>
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
    <section class="panel admin-card"><p class="eyebrow">PROVIDERS</p><h2>解析提供方</h2><div class="admin-list"><div v-for="provider in providers" :key="provider.id"><span><strong>{{ provider.name }}</strong><small>{{ provider.providerType }} · {{ provider.baseUrl }}</small></span><span class="row-actions"><span class="status" :class="provider.isEnabled ? 'succeeded' : ''">{{ provider.isEnabled ? '启用' : '停用' }}</span><button @click="toggleProvider(provider)">{{ provider.isEnabled ? '停用' : '启用' }}</button></span></div></div><details><summary>新增提供方</summary><div class="form-grid"><label>名称<input v-model="newProvider.name"></label><label>类型<input v-model="newProvider.providerType"></label><label class="wide">服务地址<input v-model="newProvider.baseUrl" type="url"></label><label class="wide">凭据<input v-model="newProvider.credential" type="password"></label><label><input v-model="newProvider.isDefault" type="checkbox"> 设为默认</label><button class="primary" @click="createProvider">创建</button></div></details></section>
    <section class="panel admin-card"><p class="eyebrow">API CLIENTS</p><h2>服务客户端</h2><div class="admin-list"><div v-for="client in clients" :key="client.id"><span><strong>{{ client.name }}</strong><small>{{ client.scopes.join(' · ') }}</small></span><span class="row-actions"><span class="status" :class="client.isActive ? 'succeeded' : 'failed'">{{ client.isActive ? '有效' : '已吊销' }}</span><button v-if="client.isActive" @click="rotateClient(client)">轮换</button><button v-if="client.isActive" class="danger-link" @click="revokeClient(client)">吊销</button></span></div></div><details><summary>新增 API 客户端</summary><div class="form-grid"><label class="wide">名称<input v-model="newClient.name"></label><button class="primary" @click="createClient">创建并签发凭据</button><div v-if="issuedCredential" class="credential wide"><strong>仅显示一次</strong><code>{{ issuedCredential }}</code></div></div></details></section>
  </div>
</template>

<style scoped>
.row-actions{display:flex;align-items:center;gap:8px}.row-actions button{border:0;background:transparent;color:#37664f;font-size:11px;padding:3px}.row-actions .danger-link{color:#a23b36}
</style>
