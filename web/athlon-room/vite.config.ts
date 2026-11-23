import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(rootDir, "../../src/Athlon.Agent.App/Assets/AthlonRoom");

export default defineConfig({
  // Absolute asset URLs so /r/{roomId} does not resolve JS/CSS under /r/assets/.
  base: "/",
  plugins: [react(), tailwindcss()],
  server: {
    host: true,
    port: 5179,
  },
  build: {
    outDir,
    emptyOutDir: true,
  },
});
