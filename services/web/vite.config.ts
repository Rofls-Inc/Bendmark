/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Координатор по умолчанию на 5080, как в README координатора
const coordinator = process.env.COORDINATOR_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    // Браузер ходит на /api того же origin, поэтому CORS в координаторе не нужен
    proxy: {
      '/api': {
        target: coordinator,
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
  test: {
    environment: 'node',
  },
})
