import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true, // fail loudly: a shifted port breaks the registered redirect URI
  },
  build: {
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
        // Second entry point so the silent-renew iframe gets a real bundle.
        'silent-renew': resolve(__dirname, 'silent-renew.html'),
      },
    },
  },
});
