import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(rootDir, "../../src/Athlon.Agent.App/Assets/AthlonWeb");

const vllmMetricsProxyTarget = process.env.VITE_VLLM_METRICS_PROXY_TARGET?.trim();

export default defineConfig({
  // Relative asset URLs — AthlonWebStaticServer hosts the SPA at a fixed local port.
  base: "./",
  plugins: [react(), tailwindcss()],
  server: {
    host: true,
    port: 5178,
    cors: true,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET,HEAD,PUT,PATCH,POST,DELETE,OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, Authorization, X-Requested-With",
    },
    ...(vllmMetricsProxyTarget
      ? {
          proxy: {
            "/__vllm_metrics": {
              target: vllmMetricsProxyTarget.replace(/\/$/, ""),
              changeOrigin: true,
              rewrite: () => "/metrics",
            },
          },
        }
      : {}),
  },
  preview: {
    host: true,
    cors: true,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET,HEAD,PUT,PATCH,POST,DELETE,OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, Authorization, X-Requested-With",
    },
  },
  build: {
    outDir,
    emptyOutDir: true,
  },
});
