import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "dist",
  },
  server: {
    proxy: {
      // ws:true so the Listener audio WebSocket (/api/listener/stream) proxies to the backend in dev
      "/api": { target: "http://localhost:5074", ws: true },
    },
  },
})
