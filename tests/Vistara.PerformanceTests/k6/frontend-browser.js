import { browser } from 'k6/browser';
import { Trend } from 'k6/metrics';

const galleryUrl = __ENV.VISTARA_GALLERY_URL || '';
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
    browser_web_vital_lcp: ['p(75)<2500'],
    browser_web_vital_inp: ['p(75)<200'],
    browser_web_vital_cls: ['p(75)<0.1'],
    vistara_initial_image_kib: ['max<500'],
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
    await page.goto(galleryUrl, { waitUntil: 'networkidle' });
    const observation = await page.evaluate(async () => {
      const tasks = [];
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          tasks.push(entry.duration);
        }
      });
      observer.observe({ type: 'longtask', buffered: true });
      window.scrollTo(0, document.body.scrollHeight);
      await new Promise((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(resolve)));
      observer.disconnect();

      const resources = performance.getEntriesByType('resource');
      const visibleImageBytes = resources
        .filter((entry) => entry.initiatorType === 'img')
        .reduce((total, entry) => total + (entry.transferSize || 0), 0);
      const memory = performance.memory && performance.memory.usedJSHeapSize;
      return {
        visibleImageKiB: visibleImageBytes / 1024,
        thumbnailNodes: document.querySelectorAll('img').length,
        highPriorityImages: document.querySelectorAll('img[fetchpriority="high"]').length,
        longestTaskMs: tasks.length ? Math.max(...tasks) : 0,
        jsHeapMiB: memory ? memory / 1024 / 1024 : null,
      };
    });
    imageKiB.add(observation.visibleImageKiB);
    thumbnailNodes.add(observation.thumbnailNodes);
    highPriorityImages.add(observation.highPriorityImages);
    scrollLongTask.add(observation.longestTaskMs);
    if (observation.jsHeapMiB !== null) {
      jsHeapMiB.add(observation.jsHeapMiB);
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
      detail: `Measured ${galleryUrl} with browser performance entries.`,
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
