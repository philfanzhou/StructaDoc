<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { antiforgery, get, mutate } from './api'
import { error, message, notice } from './messages'
import { session } from './session'

const route = useRoute()
const canAdmin = computed(() => session.value?.isAdministrator === true)

// First-run setup cannot authenticate its caller, so the claim is reported to administrators until
// one confirms it. Administrators only: the claimant address is not other users' business.
type ClaimWarning = { claimedFromAddress: string; claimedAtUtc: string }
const claim = ref<ClaimWarning>()

watch(canAdmin, async isAdministrator => {
  if (!isAdministrator) { claim.value = undefined; return }
  try { claim.value = await get<ClaimWarning | undefined>('/api/v1/admin/setup-claim') } catch { /* the banner is advisory */ }
}, { immediate: true })

async function acknowledgeClaim() {
  try {
    await mutate('/api/v1/admin/setup-claim/acknowledge', 'POST')
    claim.value = undefined
  } catch (e) { message((e as Error).message, true) }
}

function claimedAt(value: string) {
  return new Date(value).toLocaleString()
}

const antiforgeryFieldName = '__RequestVerificationToken'

async function logout(event: SubmitEvent) {
  const form = event.currentTarget as HTMLFormElement
  const tokenField = form.elements.namedItem(antiforgeryFieldName) as HTMLInputElement
  try {
    tokenField.value = (await antiforgery()).requestToken
    // A document navigation lets the authority end its session using its own browser cookie.
    form.submit()
  } catch (e) { message((e as Error).message, true) }
}
</script>

<template>
  <div v-if="error" class="toast error">{{ error }}</div><div v-if="notice" class="toast">{{ notice }}</div>
  <div v-if="!session" class="loading">正在连接 StructaDoc…</div>
  <RouterView v-else-if="route.meta.bare" />
  <div v-else class="app-shell">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark small">S</span><strong>StructaDoc</strong></div>
      <nav>
        <RouterLink to="/" exact-active-class="active">文档工作台</RouterLink>
        <RouterLink v-if="canAdmin" to="/admin" exact-active-class="active">系统管理</RouterLink>
      </nav>
      <div class="account">
        <span class="avatar">{{ (session.displayName || session.email || 'U').slice(0, 1).toUpperCase() }}</span>
        <div><strong>{{ session.displayName || session.email || '用户' }}</strong><small>{{ session.isAdministrator ? '管理员' : '工作空间成员' }}</small></div>
        <form action="/api/v1/session/logout?redirectToHome=true" method="post" @submit.prevent="logout">
          <input type="hidden" :name="antiforgeryFieldName">
          <button title="退出登录" type="submit">退出</button>
        </form>
      </div>
    </aside>
    <main class="content">
      <div v-if="claim" class="claim-banner">
        <div>管理员账号由 <strong>{{ claim.claimedFromAddress }}</strong> 于 {{ claimedAt(claim.claimedAtUtc) }} 创建。若非本人操作，请立即修改密码并检查该实例。</div>
        <button @click="acknowledgeClaim">确认是我</button>
      </div>
      <RouterView />
    </main>
  </div>
</template>
