import { browser } from 'k6/browser';
import { Trend } from 'k6/metrics';

const galleryUrl = __ENV.VISTARA_GALLERY_URL || '';
const browserAuthorization = __ENV.VISTARA_BROWSER_AUTHORIZATION || '';
const summaryPath = __ENV.VISTARA_K6_BROWSER_SUMMARY ||
  'tests/Vistara.PerformanceTests/artifacts/k6-browser-summary.json';

const imageKiB = new Trend('vistara_initial_image_kib');
const thumbnailNodes = new Trend('vistara_thumbnail_nodes');
const highPriorityImages = new Trend('vistara_high_priority_images');
const scrollLongTask = new Trend('vistara_scroll_long_task_ms');
const jsHeapMiB = new Trend('vistara_js_heap_mib');

export const options = {
  scenarios: {
    gallery_browser: {
      executor: 'shared-iterations',
      vus: 1,
      iterations: 5,
      options: { browser: { type: 'chromium' } },
    },
  },
  thresholds: {
    browser_web_vital_lcp: ['p(75)<=2500'],
    browser_web_vital_inp: ['p(75)<=200'],
    browser_web_vital_cls: ['p(75)<=0.1'],
    vistara_initial_image_kib: ['max<=500'],
    vistara_thumbnail_nodes: ['max<=400'],
    vistara_high_priority_images: ['max<=1'],
    vistara_scroll_long_task_ms: ['max<=50'],
    vistara_js_heap_mib: ['max<=256'],
  },
};

export default async function () {
  if (!galleryUrl) {
    throw new Error('VISTARA_GALLERY_URL is required; no fallback page is used.');
  }

  const page = await browser.newPage();
  try {
    if (browserAuthorization) {
      await page.setExtraHTTPHeaders({
        Authorization: browserAuthorization,
      });
    }

    await page.goto(galleryUrl, { waitUntil: 'networkidle' });
    await page.waitForSelector('[aria-label="Library timeline"]', {
      state: 'visible',
    });
    await page.waitForSelector('[data-asset-id] img', { state: 'visible' });

    const initial = await page.evaluate(() => {
      const resources = performance.getEntriesByType('resource');
      const visibleImageBytes = resources
        .filter((entry) => entry.initiatorType === 'img')
        .reduce(
          (total, entry) =>
            total + (entry.transferSize || entry.encodedBodySize || 0),
          0);
      const memory = performance.memory && performance.memory.usedJSHeapSize;
      return {
        visibleImageKiB: visibleImageBytes / 1024,
        assetNodes: document.querySelectorAll('[data-asset-id]').length,
        thumbnailNodes: document.querySelectorAll('[data-asset-id] img').length,
        highPriorityImages:
          document.querySelectorAll('[data-asset-id] img[fetchpriority="high"]').length,
        jsHeapMiB: memory ? memory / 1024 / 1024 : null,
      };
    });
    if (
      initial.assetNodes === 0 ||
      initial.thumbnailNodes === 0 ||
      initial.visibleImageKiB <= 0
    ) {
      throw new Error(
        'The gallery did not render populated asset cards with transferred image bytes.');
    }

    await page.locator('button[aria-label="List view"]').click();
    await page.locator('button[aria-label="Grid view"]').click();
    const longestTaskMs = await page.evaluate(async () => {
      const tasks = [];
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          tasks.push(entry.duration);
        }
      });
      observer.observe({ type: 'longtask' });
      const scroller = document.querySelector('[aria-label="Library timeline"]');
      if (!(scroller instanceof HTMLElement) || scroller.scrollHeight <= scroller.clientHeight) {
        observer.disconnect();
        throw new Error('The populated library timeline is not scrollable.');
      }

      scroller.scrollTo(0, scroller.scrollHeight);
      await new Promise((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(resolve)));
      await new Promise((resolve) => setTimeout(resolve, 100));
      observer.disconnect();
      return tasks.length ? Math.max(...tasks) : 0;
    });
    imageKiB.add(initial.visibleImageKiB);
    thumbnailNodes.add(initial.thumbnailNodes);
    highPriorityImages.add(initial.highPriorityImages);
    scrollLongTask.add(longestTaskMs);
    if (initial.jsHeapMiB !== null) {
      jsHeapMiB.add(initial.jsHeapMiB);
    }
  } finally {
    await page.close();
  }
}

export function handleSummary(data) {
  const document = {
    measurements: {
      'initial-visible-image-kib': metric(data, 'vistara_initial_image_kib', 'max'),
      'thumbnail-dom-nodes': metric(data, 'vistara_thumbnail_nodes', 'max'),
      'high-priority-images': metric(data, 'vistara_high_priority_images', 'max'),
      'scroll-long-task-ms': metric(data, 'vistara_scroll_long_task_ms', 'max'),
      'lcp-p75-ms': metric(data, 'browser_web_vital_lcp', 'p(75)'),
      'inp-p75-ms': metric(data, 'browser_web_vital_inp', 'p(75)'),
      'cls-p75': metric(data, 'browser_web_vital_cls', 'p(75)'),
      'browser-js-heap-mib': metric(data, 'vistara_js_heap_mib', 'max'),
    },
    prerequisites: [{
      name: 'k6 Chromium browser',
      status: 'passed',
      detail: `Measured populated library asset cards, initial image resources, view interactions, and timeline scrolling at ${galleryUrl}.`,
    }],
  };
  return {
    stdout: `${JSON.stringify(document, null, 2)}\n`,
    [summaryPath]: JSON.stringify(document, null, 2),
  };
}

function metric(data, name, statistic) {
  const value = data.metrics[name] && data.metrics[name].values[statistic];
  return Number.isFinite(value)
    ? { value }
    : { unavailableReason: `k6 did not produce ${statistic} for ${name}.` };
}
