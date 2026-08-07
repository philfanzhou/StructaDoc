import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: { outDir: '../src/StructaDoc.Host/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5078', '/health': 'http://localhost:5078' } },
})
