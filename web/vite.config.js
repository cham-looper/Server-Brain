import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The dashboard is built into web/dist and embedded in ServerBrain.Core at compile time.
export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://127.0.0.1:7777', changeOrigin: false, headers: { 'X-SB-Client': '1' } } },
  },
});
