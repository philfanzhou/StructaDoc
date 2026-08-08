<script setup lang="ts">
defineProps<{ headline: string[]; lead: string; trust: string[] }>()
</script>

<template>
  <main class="auth-shell">
    <section class="auth-story">
      <div class="brand-mark">S</div><p class="eyebrow">STRUCTADOC</p>
      <h1><template v-for="(line, index) in headline" :key="line"><br v-if="index">{{ line }}</template></h1>
      <p>{{ lead }}</p>
      <div class="trust-line"><span v-for="item in trust" :key="item">{{ item }}</span></div>
    </section>
    <section class="login-card"><slot /></section>
  </main>
</template>
