<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import AuthShell from '../components/AuthShell.vue'
import { mutate, resetAntiforgery } from '../api'
import { message } from '../messages'
import { loadSession } from '../session'

const router = useRouter()
const username = ref('')
const displayName = ref('')
const password = ref('')
const confirmation = ref('')
const busy = ref(false)

const mismatch = computed(() => confirmation.value.length > 0 && password.value !== confirmation.value)

async function claim() {
  if (password.value !== confirmation.value) { message('两次输入的密码不一致。', true); return }
  busy.value = true
  try {
    await mutate('/api/v1/setup', 'POST', {
      username: username.value,
      password: password.value,
      displayName: displayName.value || null,
    })
    // The claim signs the new administrator in, so the principal changed and the previous
    // antiforgery token is no longer valid for subsequent writes.
    resetAntiforgery()
    await loadSession()
    await router.replace('/admin')
  } catch (e) { message((e as Error).message, true) } finally { busy.value = false }
}
</script>

<template>
  <AuthShell
    :headline="['初始化这台', 'StructaDoc 实例。']"
    lead="创建第一个管理员账号。此后所有配置都在管理页面完成，无需再修改配置文件或重新部署。"
    :trust="['本地账号', '无需外部身份平台', '数据留在本机']">
    <p class="eyebrow">首次初始化</p><h2>创建管理员</h2>
    <form @submit.prevent="claim">
      <label>用户名<input v-model="username" type="text" name="username" autocomplete="username" minlength="3" maxlength="64" required></label>
      <label>显示名称（可选）<input v-model="displayName" type="text" maxlength="255"></label>
      <label>密码<input v-model="password" type="password" name="new-password" autocomplete="new-password" minlength="12" required></label>
      <label>确认密码<input v-model="confirmation" type="password" autocomplete="new-password" minlength="12" required></label>
      <p v-if="mismatch" class="login-note">两次输入的密码不一致。</p>
      <button class="secondary" :disabled="busy || mismatch">{{ busy ? '创建中…' : '创建管理员' }}</button>
    </form>
    <p class="login-note">用户名由 3–64 个字母、数字、<code>.</code>、<code>_</code>、<code>-</code> 组成，密码至少 12 位。此账号在身份平台故障时仍可登录，请妥善保管。</p>
  </AuthShell>
</template>
