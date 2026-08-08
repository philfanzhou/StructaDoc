<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { get, mutate } from '../api'
import { message } from '../messages'

const providers = ref<any[]>([])
const clients = ref<any[]>([])
const newProvider = ref({ name: '', providerType: 'mineru-local', baseUrl: '', credential: '', model: '', backend: '', isEnabled: true, isDefault: false, clearCredential: false })
const newClient = ref({ name: '', scopes: ['documents:read', 'documents:write', 'parses:read', 'parses:write'] })
const issuedCredential = ref('')

async function load() {
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

onMounted(() => load())
</script>

<template>
  <header class="page-header"><div><p class="eyebrow">ADMINISTRATION</p><h1>系统管理</h1><p>管理解析提供方与服务 API 客户端。</p></div></header>

  <div class="admin-grid">
    <section class="panel admin-card"><p class="eyebrow">PROVIDERS</p><h2>解析提供方</h2><div class="admin-list"><div v-for="provider in providers" :key="provider.id"><span><strong>{{ provider.name }}</strong><small>{{ provider.providerType }} · {{ provider.baseUrl }}</small></span><span class="row-actions"><span class="status" :class="provider.isEnabled ? 'succeeded' : ''">{{ provider.isEnabled ? '启用' : '停用' }}</span><button @click="toggleProvider(provider)">{{ provider.isEnabled ? '停用' : '启用' }}</button></span></div></div><details><summary>新增提供方</summary><div class="form-grid"><label>名称<input v-model="newProvider.name"></label><label>类型<input v-model="newProvider.providerType"></label><label class="wide">服务地址<input v-model="newProvider.baseUrl" type="url"></label><label class="wide">凭据<input v-model="newProvider.credential" type="password"></label><label><input v-model="newProvider.isDefault" type="checkbox"> 设为默认</label><button class="primary" @click="createProvider">创建</button></div></details></section>
    <section class="panel admin-card"><p class="eyebrow">API CLIENTS</p><h2>服务客户端</h2><div class="admin-list"><div v-for="client in clients" :key="client.id"><span><strong>{{ client.name }}</strong><small>{{ client.scopes.join(' · ') }}</small></span><span class="row-actions"><span class="status" :class="client.isActive ? 'succeeded' : 'failed'">{{ client.isActive ? '有效' : '已吊销' }}</span><button v-if="client.isActive" @click="rotateClient(client)">轮换</button><button v-if="client.isActive" class="danger-link" @click="revokeClient(client)">吊销</button></span></div></div><details><summary>新增 API 客户端</summary><div class="form-grid"><label class="wide">名称<input v-model="newClient.name"></label><button class="primary" @click="createClient">创建并签发凭据</button><div v-if="issuedCredential" class="credential wide"><strong>仅显示一次</strong><code>{{ issuedCredential }}</code></div></div></details></section>
  </div>
</template>

<style scoped>
.row-actions{display:flex;align-items:center;gap:8px}.row-actions button{border:0;background:transparent;color:#37664f;font-size:11px;padding:3px}.row-actions .danger-link{color:#a23b36}
</style>
