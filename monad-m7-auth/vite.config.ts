import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'fs'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3001,                 // Vite dev on :3001
    host: '127.0.0.1',          // keep it local behind Nginx
    allowedHosts: ['auth.rxmsolutions.com'],  // <-- allow your public host

    // HMR over HTTPS (browser requires WSS if page is https)
    origin: 'https://auth.rxmsolutions.com',
    hmr: {
      protocol: 'wss',
      host: 'auth.rxmsolutions.com',
      clientPort: 443,
    },
  },
})
