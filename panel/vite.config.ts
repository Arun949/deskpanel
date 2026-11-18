import { defineConfig } from 'vitest/config';

// Development only: lets the panel run in a desktop browser against a local agent. The dev proxy plays the Android
// shell's role: it adds the device key (from DESKPANEL_DEV_AUTH="Device <id>.<key>") and drops the browser's Origin,
// which the agent would rightly refuse. The production bundle contains no key and no proxy.
const devAuth = process.env['DESKPANEL_DEV_AUTH'];
const agent = process.env['DESKPANEL_AGENT'] ?? 'http://127.0.0.1:8765';

export default defineConfig({
  // relative asset URLs: the app serves the panel from /assets/panel/ inside the APK
  base: './',
  // ES2020 keeps the bundle running on older Android System WebViews (Android 7+ with an updated WebView).
  build: { target: 'es2020', outDir: 'dist', assetsInlineLimit: 0 },
  server: {
    host: '127.0.0.1',
    proxy: {
      '/agent/api': {
        target: agent,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/agent\/api/, '/api'),
        configure: (proxy) => {
          proxy.on('proxyReq', (req) => {
            req.removeHeader('origin');
            req.removeHeader('referer');
            req.removeHeader('sec-fetch-site');
            req.removeHeader('sec-fetch-mode');
            if (devAuth) req.setHeader('authorization', devAuth);
          });
        },
      },
    },
  },
  test: { environment: 'jsdom', globals: true },
});
