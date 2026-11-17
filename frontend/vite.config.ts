import { defineConfig } from "vite";

export default defineConfig({
  server: {
    port: 5173,
    proxy: { "/api": "http://localhost:5080" },
  },
  build: {
    // The production build is served by ASP.NET Core from wwwroot.
    outDir: "../backend/Attendance.Api/wwwroot",
    emptyOutDir: true,
  },
});
