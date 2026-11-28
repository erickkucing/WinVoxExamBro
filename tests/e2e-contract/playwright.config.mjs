import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: 0,
  workers: 1,
  use: {
    baseURL: 'http://127.0.0.1:18089',
    headless: true,
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0 ExamBro/1.0.0 ExamBrowser/1.0.0 LouisVox/1.0.0',
    launchOptions: {
      args: ['--no-sandbox', '--disable-setuid-sandbox']
    }
  },
  webServer: {
    command: 'node fake-cbt-server.mjs',
    url: 'http://127.0.0.1:18089/health',
    reuseExistingServer: false,
    timeout: 15_000
  }
});
