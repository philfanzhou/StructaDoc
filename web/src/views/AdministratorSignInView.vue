<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AuthShell from '../components/AuthShell.vue'
import { mutate, resetAntiforgery } from '../api'
import { message } from '../messages'
import { loadSession, safeReturnUrl, session } from '../session'

const route = useRoute()
const router = useRouter()
const email = ref('')
const password = ref('')
const busy = ref(false)
const target = computed(() => safeReturnUrl(route.query.returnUrl, '/admin'))
const loginUrl = computed(() => `/api/v1/session/login?returnUrl=${encodeURIComponent(target.value)}`)

async function signIn() {
  busy.value = true
  try {
    await mutate('/api/v1/admin/session', 'POST', { email: email.value, password: password.value })
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
    lead="配置解析提供方、签发服务客户端凭据，并保持实例可运维。管理接口的访问控制始终由服务端策略决定，与此页面的地址无关。"
    :trust="['不可变提供方版本', '一次性凭据', '可审计变更']">
    <p class="eyebrow">系统管理</p><h2>管理员登录</h2>
    <template v-if="session?.authenticated">
      <p class="login-note">当前账号 {{ session.displayName || session.email || session.subjectId }} 没有管理员权限。请改用管理员账号，或返回<RouterLink to="/">文档工作台</RouterLink>。</p>
    </template>
    <template v-else>
      <a v-if="session?.oidcEnabled" class="primary button-link" :href="loginUrl">使用组织账号登录</a>
      <div v-if="session?.oidcEnabled" class="divider"><span>或使用本地应急管理员</span></div>
      <form @submit.prevent="signIn">
        <label>管理员邮箱<input v-model="email" type="email" autocomplete="username" required></label>
        <label>密码<input v-model="password" type="password" autocomplete="current-password" required></label>
        <button class="secondary" :disabled="busy">{{ busy ? '登录中…' : '管理员登录' }}</button>
      </form>
      <p class="login-note">本地管理员用于引导配置与身份平台故障时的应急访问。普通使用者请前往<RouterLink to="/">文档工作台</RouterLink>。</p>
    </template>
  </AuthShell>
</template>
