<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { mutate } from './api'
import { error, message, notice } from './messages'
import { session } from './session'

const route = useRoute()
const canAdmin = computed(() => session.value?.isAdministrator === true)

async function logout() {
  try {
    if (session.value?.subjectType === 'administrator') await mutate('/api/v1/admin/session', 'DELETE')
    else await mutate('/api/v1/session/logout', 'POST')
    location.assign('/')
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
      <div class="account"><span class="avatar">{{ (session.displayName || session.email || 'U').slice(0, 1).toUpperCase() }}</span><div><strong>{{ session.displayName || session.email || '用户' }}</strong><small>{{ session.isAdministrator ? '管理员' : '工作空间成员' }}</small></div><button title="退出登录" @click="logout">退出</button></div>
    </aside>
    <main class="content"><RouterView /></main>
  </div>
</template>
