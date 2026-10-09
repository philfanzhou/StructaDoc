<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import AuthShell from '../components/AuthShell.vue'
import { safeReturnUrl, session } from '../session'

const route = useRoute()
const loginUrl = computed(() => `/api/v1/session/login?returnUrl=${encodeURIComponent(safeReturnUrl(route.query.returnUrl, '/'))}`)
</script>

<template>
  <AuthShell
    :headline="['让文档结构', '成为可用的数据。']"
    lead="登录后可以上传文档、开始解析、查看结果和导出文件。使用组织账号登录，账号由所在组织的身份平台管理。"
    :trust="['查看解析进度', '阅读结构化内容', '下载与导出结果']">
    <p class="eyebrow">欢迎回来</p><h2>进入文档工作台</h2>
    <a v-if="session?.oidcEnabled" class="primary button-link" :href="loginUrl">使用组织账号登录</a>
    <p v-else class="login-note">本服务未启用组织账号登录。请从<RouterLink to="/admin/signin">管理员入口</RouterLink>使用本地管理员账号进入。</p>
    <p class="login-note">管理员可前往<RouterLink to="/admin">系统管理</RouterLink>配置解析提供方与服务客户端。</p>
  </AuthShell>
</template>
