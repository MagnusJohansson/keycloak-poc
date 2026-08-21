import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
  plugins: [vue()],
  // 5174, because 5173 belongs to the React app and both redirect URIs are
  // registered separately in Keycloak. strictPort so a silent shift to another
  // port fails loudly instead of producing "Invalid redirect_uri".
  server: { port: 5174, strictPort: true },
});
