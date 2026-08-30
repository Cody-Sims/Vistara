import crypto from 'k6/crypto';
import encoding from 'k6/encoding';
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const baseUrl = (__ENV.VISTARA_BASE_URL || '').replace(/\/$/, '');
const authorization = __ENV.VISTARA_AUTHORIZATION || '';
const derivativeUrl = __ENV.VISTARA_DERIVATIVE_URL || '';
const derivativeAuthorization = __ENV.VISTARA_DERIVATIVE_AUTHORIZATION || '';
const runUploads = (__ENV.VISTARA_RUN_UPLOADS || '').toLowerCase() === 'true';
const managementPath = __ENV.VISTARA_MANAGEMENT_PATH || '/api/v1/assets?limit=60';
const summaryPath = __ENV.VISTARA_K6_SUMMARY ||
  'tests/Vistara.PerformanceTests/artifacts/k6-reference-summary.json';

const managementDuration = new Trend('vistara_management_duration', true);
const derivativeWaiting = new Trend('vistara_derivative_waiting', true);
const uploadFlowDuration = new Trend('vistara_upload_flow_duration', true);
const uploadErrors = new Counter('vistara_upload_errors');
const referenceErrors = new Counter('vistara_reference_errors');

const scenarios = {
  management_reads: {
    executor: 'constant-arrival-rate',
    exec: 'managementRead',
    rate: 20,
    timeUnit: '1s',
    duration: __ENV.VISTARA_K6_DURATION || '30s',
    preAllocatedVUs: 8,
    maxVUs: 32,
  },
};

if (derivativeUrl) {
  scenarios.warm_derivative = {
    executor: 'constant-arrival-rate',
    exec: 'warmDerivative',
    rate: 30,
    timeUnit: '1s',
    duration: __ENV.VISTARA_K6_DURATION || '30s',
    preAllocatedVUs: 8,
    maxVUs: 32,
  };
}

if (runUploads) {
  scenarios.upload_job_concurrency = {
    executor: 'constant-vus',
    exec: 'uploadAndCommit',
    vus: 4,
    duration: __ENV.VISTARA_K6_DURATION || '30s',
  };
}

const thresholds = {
  vistara_management_duration: ['p(95)<200'],
  vistara_reference_errors: ['count==0'],
  http_req_failed: ['rate<0.01'],
};
if (derivativeUrl) {
  thresholds.vistara_derivative_waiting = ['p(95)<100'];
}
if (runUploads) {
  thresholds.vistara_upload_errors = ['count==0'];
}

export const options = {
  discardResponseBodies: false,
  scenarios,
  thresholds,
};

export function setup() {
  if (!baseUrl || !authorization) {
    throw new Error(
      'VISTARA_BASE_URL and VISTARA_AUTHORIZATION are required; no fallback host is used.');
  }

  return { baseUrl, authorization };
}

export function managementRead(data) {
  referenceErrors.add(0);
  const response = http.get(`${data.baseUrl}${managementPath}`, {
    headers: { Authorization: data.authorization, Accept: 'application/json' },
    tags: { budget: 'warm-management-get' },
  });
  managementDuration.add(response.timings.duration);
  const valid = check(response, {
    'management read is 200': (value) => value.status === 200,
    'management read has JSON': (value) =>
      (value.headers['Content-Type'] || '').includes('application/json'),
  });
  if (!valid) {
    referenceErrors.add(1);
  }
}

export function warmDerivative(data) {
  const target = derivativeUrl.startsWith('http')
    ? derivativeUrl
    : `${data.baseUrl}${derivativeUrl}`;
  const response = http.get(target, {
    headers: derivativeAuthorization
      ? { Authorization: derivativeAuthorization }
      : {},
    tags: { budget: 'warm-derivative' },
  });
  derivativeWaiting.add(response.timings.waiting);
  const valid = check(response, {
    'warm derivative is 200': (value) => value.status === 200,
    'warm derivative is immutable': (value) =>
      (value.headers['Cache-Control'] || '').includes('immutable'),
  });
  if (!valid) {
    referenceErrors.add(1);
  }
}

export function uploadAndCommit(data) {
  uploadErrors.add(0);
  const started = Date.now();
  const image = encoding.b64decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    'std');
  const key = `perf-${__VU}-${__ITER}-${Date.now()}`;
  const create = http.post(
    `${data.baseUrl}/api/v1/uploads`,
    JSON.stringify({
      fileName: `${key}.png`,
      sizeBytes: image.byteLength,
      contentType: 'image/png',
      sha256: crypto.sha256(image, 'hex'),
    }),
    {
      headers: {
        Authorization: data.authorization,
        'Content-Type': 'application/json',
        'Idempotency-Key': `${key}-create`,
      },
      tags: { budget: 'upload-create' },
    });
  if (!check(create, { 'upload reservation succeeds': (value) => [200, 201].includes(value.status) })) {
    uploadErrors.add(1);
    return;
  }

  const session = create.json();
  const contentUrl = session.plan && session.plan.contentUrl;
  if (!contentUrl) {
    uploadErrors.add(1);
    return;
  }

  const content = http.put(
    contentUrl.startsWith('http') ? contentUrl : `${data.baseUrl}${contentUrl}`,
    image,
    {
      headers: {
        Authorization: data.authorization,
        'Content-Type': 'image/png',
      },
      tags: { budget: 'upload-content' },
    });
  if (!check(content, { 'proxy content succeeds': (value) => [200, 204].includes(value.status) })) {
    uploadErrors.add(1);
    return;
  }

  const commit = http.post(
    `${data.baseUrl}/api/v1/uploads/${session.uploadId}/commit`,
    null,
    {
      headers: {
        Authorization: data.authorization,
        'Idempotency-Key': `${key}-commit`,
        'If-Match': header(content, 'etag') || header(create, 'etag'),
      },
      tags: { budget: 'upload-commit' },
    });
  if (!check(commit, { 'upload commit queues work': (value) => [200, 202].includes(value.status) })) {
    uploadErrors.add(1);
  }

  uploadFlowDuration.add(Date.now() - started);
}

export function handleSummary(data) {
  const measurements = {
    'warm-management-get-p95-ms': metric(data, 'vistara_management_duration', 'p(95)'),
    'warm-derivative-ttfb-p95-ms': derivativeUrl
      ? metric(data, 'vistara_derivative_waiting', 'p(95)')
      : unavailable('VISTARA_DERIVATIVE_URL was not configured.'),
    'upload-job-errors': runUploads
      ? metric(data, 'vistara_upload_errors', 'count')
      : unavailable('VISTARA_RUN_UPLOADS=true was not configured.'),
  };
  const document = {
    measurements,
    prerequisites: [{
      name: 'reference host',
      status: metricValue(data, 'vistara_reference_errors', 'count') === 0
        ? 'passed'
        : 'failed',
      detail: `Measured ${baseUrl} without substituting a fallback host; invalid responses are counted.`,
    }],
  };
  return {
    stdout: `${JSON.stringify(document, null, 2)}\n`,
    [summaryPath]: JSON.stringify(document, null, 2),
  };
}

function metric(data, name, statistic) {
  const value = metricValue(data, name, statistic);
  return Number.isFinite(value)
    ? { value }
    : unavailable(`k6 did not produce ${statistic} for ${name}.`);
}

function unavailable(reason) {
  return { unavailableReason: reason };
}

function metricValue(data, name, statistic) {
  return data.metrics[name] && data.metrics[name].values[statistic];
}

function header(response, name) {
  const target = name.toLowerCase();
  const key = Object.keys(response.headers).find((value) => value.toLowerCase() === target);
  return key ? response.headers[key] : '';
}
