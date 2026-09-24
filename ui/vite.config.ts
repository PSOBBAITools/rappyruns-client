import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The build is embedded in RappyRunsClient.exe and served from
// https://app.rappyruns.internal/ (RappyRuns.App/Host/UiAssets.cs).
// For hot reload, run `npm run dev` and start the exe with
// RAPPYRUNS_UI_DEV_URL=http://localhost:5173/
export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // WebView2 is evergreen Chromium.
    target: 'chrome120',
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
