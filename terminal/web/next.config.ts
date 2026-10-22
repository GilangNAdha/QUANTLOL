import type { NextConfig } from "next";

// API requests are proxied by web/app/api/[...path]/route.ts instead of a
// rewrite here, so the shared API key can be attached server-side. See
// web/lib/api-key.ts.
const nextConfig: NextConfig = {
  output: "standalone",
  // Next 15 refuses cross-origin dev requests (HMR websocket included) unless they are listed.
  // The preview environment serves the dev server through a proxy host, and localhost covers the
  // normal `npm run dev` case.
  allowedDevOrigins: [
    "localhost",
    "127.0.0.1",
    ...(process.env.DEV_ORIGINS ? process.env.DEV_ORIGINS.split(",").map((s) => s.trim()) : []),
    "*.e2b.app",
  ],
};

export default nextConfig;
