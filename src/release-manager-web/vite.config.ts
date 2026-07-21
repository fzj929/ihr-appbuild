import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
export default defineConfig({ plugins: [vue()], server: { port: 5173, proxy: { '/api': 'http://localhost:5088', '/hubs': { target: 'http://localhost:5088', ws: true } } }, build: { outDir: '../ReleaseManager.Api/wwwroot', emptyOutDir: true } })
