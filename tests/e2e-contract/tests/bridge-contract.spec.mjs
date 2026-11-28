import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const bridgePath = path.resolve(__dirname, '../../../src/ExamBro.Exam/Resources/cbt-bridge.js');
const bridgeSource = fs.readFileSync(bridgePath, 'utf8');

test.describe('WinVoxExamBro cbt-bridge.js contract', () => {
  test.beforeEach(async ({ page, request }) => {
    await request.post('/_test/reset');

    await page.addInitScript(() => {
      window.__postedMessages = [];
      window.chrome = {
        webview: {
          postMessage: (raw) => {
            window.__postedMessages.push(typeof raw === 'string' ? JSON.parse(raw) : raw);
          }
        }
      };
    });
    await page.addInitScript({ content: bridgeSource });
  });

  test('LouisVoxExamApp methods post exact JSON schemas', async ({ page }) => {
    await page.goto('/exam/workspace/session-contract-1');

    const messages = await page.evaluate(() => {
      window.LouisVoxExamApp.onWebExamConfig(3, 1, false);
      window.LouisVoxExamApp.onWebViolationRecorded('window_blur', 2, 3, 'Pindah jendela', true);
      window.LouisVoxExamApp.onWebSessionUnlocked(2);
      window.LouisVoxExamApp.onExamFinished();
      return window.__postedMessages;
    });

    expect(messages).toEqual([
      { handler: 'CBT_CONFIG', maxStrikes: 3, strikes: 1, isLocked: false },
      {
        handler: 'CBT_VIOLATION',
        type: 'window_blur',
        strikes: 2,
        maxStrikes: 3,
        message: 'Pindah jendela',
        isLocked: true
      },
      { handler: 'CBT_UNLOCKED', strikes: 2 },
      { handler: 'CBT_FINISHED' }
    ]);
  });

  test('overrides data-is-exambrowser and dismisses fullscreen gate automatically', async ({ page }) => {
    await page.goto('/exam/workspace/session-contract-2');

    const state = await page.evaluate(() => {
      const root = document.getElementById('cbt-anti-cheat-root');
      const gate = document.getElementById('anti-cheat-fullscreen-gate');
      return {
        attr: root?.getAttribute('data-is-exambrowser'),
        gateHiddenClass: gate?.classList.contains('hidden'),
        gateDisplay: gate?.style.display
      };
    });

    expect(state.attr).toBe('1');
    expect(state.gateHiddenClass).toBe(true);
    expect(state.gateDisplay).toBe('none');
  });

  test('stubs fullscreen APIs and dispatches fullscreenchange', async ({ page }) => {
    await page.goto('/exam/workspace/session-contract-3');

    const result = await page.evaluate(async () => {
      let fired = false;
      document.addEventListener('fullscreenchange', () => {
        fired = true;
      });
      await document.documentElement.requestFullscreen();
      return {
        fsElemMatchesRoot: document.fullscreenElement === document.documentElement,
        webkitMatchesRoot: document.webkitFullscreenElement === document.documentElement,
        fired
      };
    });

    expect(result.fsElemMatchesRoot).toBe(true);
    expect(result.webkitMatchesRoot).toBe(true);
    expect(result.fired).toBe(true);
  });

  test('overrides window.close() to notify CBT_FINISHED', async ({ page }) => {
    await page.goto('/exam/workspace/session-contract-4');

    const messages = await page.evaluate(() => {
      window.close();
      return window.__postedMessages;
    });

    expect(messages).toContainEqual({ handler: 'CBT_FINISHED' });
  });

  test('suppresses copy/paste/cut violations locally without hitting server', async ({ page, request }) => {
    await page.goto('/exam/workspace/session-contract-5');

    const responses = await page.evaluate(async () => {
      const out = [];
      for (const vt of ['copy', 'paste', 'cut', 'clipboard']) {
        const res = await fetch('/api/exam/sessions/session-contract-5/violation', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ violation_type: vt })
        });
        out.push(await res.json());
      }
      return out;
    });

    for (const r of responses) {
      expect(r.action).toBe('ignored');
      expect(r.is_locked).toBe(false);
    }

    const serverState = await (await request.get('/_test/state')).json();
    expect(serverState.violations).toHaveLength(0);
  });

  test('suppresses non-severe violations during unstable network while forwarding severe_unpin', async ({
    page,
    request
  }) => {
    await page.goto('/exam/workspace/session-contract-6');

    const results = await page.evaluate(async () => {
      window.__louisvox_network_unstable = true;

      const normalRes = await fetch('/api/exam/sessions/session-contract-6/violation', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ violation_type: 'window_blur' })
      });
      const normalJson = await normalRes.json();

      const severeRes = await fetch('/api/exam/sessions/session-contract-6/violation', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ violation_type: 'severe_unpin', metadata: { is_unpin: true } })
      });
      const severeJson = await severeRes.json();

      return { normalJson, severeJson, alarmActive: window.LouisVoxExamApp.isAlarmOverlayActive() };
    });

    expect(results.alarmActive).toBe(true);
    expect(results.normalJson.action).toBe('ignored');
    expect(results.severeJson.action).toBe('recorded');

    const serverState = await (await request.get('/_test/state')).json();
    expect(serverState.violations).toHaveLength(1);
    expect(serverState.violations[0].body.violation_type).toBe('severe_unpin');
  });

  test('auto-retries failed /answer submissions after transient network error', async ({ page, request }) => {
    let attempts = 0;
    await page.route('**/api/exam/sessions/*/answer', async (route) => {
      attempts++;
      if (attempts === 1) {
        await route.abort('failed');
      } else {
        await route.continue();
      }
    });

    await page.goto('/exam/workspace/session-contract-7');

    const initialThrew = await page.evaluate(async () => {
      try {
        await fetch('/api/exam/sessions/session-contract-7/answer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ question_id: 42, answer: 'B' })
        });
        return false;
      } catch {
        return window.__louisvox_network_unstable === true;
      }
    });

    expect(initialThrew).toBe(true);

    // Retry fires after 1000ms timer + 1000ms backoff (approx 2s)
    await expect
      .poll(
        async () => {
          const serverState = await (await request.get('/_test/state')).json();
          return serverState.answers.length;
        },
        { timeout: 10_000 }
      )
      .toBe(1);

    const serverState = await (await request.get('/_test/state')).json();
    expect(serverState.answers[0].body).toEqual({ question_id: 42, answer: 'B' });
  });

  test('cancels forbidden keyboard and clipboard events', async ({ page }) => {
    await page.goto('/exam/workspace/session-contract-8');

    const prevented = await page.evaluate(() => {
      const dispatchKey = (key, keyCode, opts = {}) => {
        const ev = new KeyboardEvent('keydown', { key, keyCode, bubbles: true, cancelable: true, ...opts });
        window.dispatchEvent(ev);
        return ev.defaultPrevented;
      };
      const dispatchClip = (type) => {
        const ev = new Event(type, { bubbles: true, cancelable: true });
        window.dispatchEvent(ev);
        return ev.defaultPrevented;
      };

      return {
        f10: dispatchKey('F10', 121),
        f12: dispatchKey('F12', 123),
        ctrlC: dispatchKey('c', 67, { ctrlKey: true }),
        ctrlV: dispatchKey('v', 86, { ctrlKey: true }),
        shiftIns: dispatchKey('Insert', 45, { shiftKey: true }),
        copy: dispatchClip('copy'),
        cut: dispatchClip('cut'),
        paste: dispatchClip('paste')
      };
    });

    expect(prevented).toEqual({
      f10: true,
      f12: true,
      ctrlC: true,
      ctrlV: true,
      shiftIns: true,
      copy: true,
      cut: true,
      paste: true
    });
  });

  test('preserves invariant physical viewport across 50%-150% DPI zoom overrides', async ({ page, context }) => {
    await page.goto('/exam/workspace/session-contract-9');
    const cdp = await context.newCDPSession(page);

    const baseWidth = 1280;
    const baseHeight = 720;
    const scales = [1.0, 0.5, 1.25, 1.5];

    for (const scale of scales) {
      const cssW = Math.round(baseWidth / scale);
      const cssH = Math.round(baseHeight / scale);
      await cdp.send('Emulation.setDeviceMetricsOverride', {
        width: cssW,
        height: cssH,
        deviceScaleFactor: scale,
        mobile: false
      });

      const phys = await page.evaluate(() => ({
        w: window.innerWidth * window.devicePixelRatio,
        h: window.innerHeight * window.devicePixelRatio
      }));

      expect(Math.abs(phys.w - baseWidth)).toBeLessThanOrEqual(2);
      expect(Math.abs(phys.h - baseHeight)).toBeLessThanOrEqual(2);
    }
  });
});
