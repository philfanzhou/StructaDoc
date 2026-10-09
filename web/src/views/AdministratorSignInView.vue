<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AuthShell from '../components/AuthShell.vue'
import { mutate, resetAntiforgery } from '../api'
import { message } from '../messages'
import { loadSession, safeReturnUrl, session } from '../session'

const route = useRoute()
const router = useRouter()
const username = ref('')
const password = ref('')
const busy = ref(false)
const target = computed(() => safeReturnUrl(route.query.returnUrl, '/admin'))
const loginUrl = computed(() => `/api/v1/session/login?returnUrl=${encodeURIComponent(target.value)}`)

async function signIn() {
  busy.value = true
  try {
    await mutate('/api/v1/admin/session', 'POST', { username: username.value, password: password.value })
    resetAntiforgery()
    const current = await loadSession()
    if (!current.isAdministrator) { message('该账号没有管理员权限。', true); return }
    await router.replace(target.value)
  } catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}
</script>

<template>
  <AuthShell
    :headline="['管理这台', 'StructaDoc 实例。']"
    lead="设置解析服务、管理账号和 API 密钥，调整存储与数据库配置。管理操作需要管理员权限。"
    :trust="['保留历史解析配置', '密钥只显示一次', '记录管理操作']">
    <p class="eyebrow">系统管理</p><h2>管理员登录</h2>
    <template v-if="session?.authenticated">
      <p class="login-note">当前账号 {{ session.displayName || session.email || session.subjectId }} 没有管理员权限。请改用管理员账号，或返回<RouterLink to="/">文档工作台</RouterLink>。</p>
    </template>
    <template v-else>
      <a v-if="session?.oidcEnabled" class="primary button-link" :href="loginUrl">使用组织账号登录</a>
      <div v-if="session?.oidcEnabled" class="divider"><span>或使用本地应急管理员</span></div>
      <form @submit.prevent="signIn">
        <label>管理员用户名<input v-model="username" type="text" name="username" autocomplete="username" required></label>
        <label>密码<input v-model="password" type="password" autocomplete="current-password" required></label>
        <button class="secondary" :disabled="busy">{{ busy ? '登录中…' : '管理员登录' }}</button>
      </form>
      <p class="login-note">本地管理员用于首次配置，也可在身份平台故障时登录。普通使用者请前往<RouterLink to="/">文档工作台</RouterLink>。</p>
    </template>
  </AuthShell>
</template>
