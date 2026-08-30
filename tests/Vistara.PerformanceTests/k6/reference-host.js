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
const uploadRunId = __ENV.VISTARA_UPLOAD_RUN_ID || '';
const managementPath = __ENV.VISTARA_MANAGEMENT_PATH || '/api/v1/assets?limit=60';
const managementMinimumItems = Number.parseInt(
  __ENV.VISTARA_MANAGEMENT_MIN_ITEMS || '60',
  10);
const summaryPath = __ENV.VISTARA_K6_SUMMARY ||
  'tests/Vistara.PerformanceTests/artifacts/k6-reference-summary.json';

const managementDuration = new Trend('vistara_management_duration', true);
const derivativeWaiting = new Trend('vistara_derivative_waiting', true);
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
  scenarios.upload_cleanup_concurrency = {
    executor: 'constant-vus',
    exec: 'uploadAndAbort',
    vus: 4,
    duration: __ENV.VISTARA_K6_DURATION || '30s',
  };
}

const thresholds = {
  vistara_management_duration: ['p(95)<=200'],
  vistara_reference_errors: ['count==0'],
  http_req_failed: ['rate<0.01'],
};
if (derivativeUrl) {
  thresholds.vistara_derivative_waiting = ['p(95)<=100'];
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

  if (!Number.isInteger(managementMinimumItems) || managementMinimumItems < 1) {
    throw new Error('VISTARA_MANAGEMENT_MIN_ITEMS must be a positive integer.');
  }

  if (runUploads && !/^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$/.test(uploadRunId)) {
    throw new Error(
      'VISTARA_UPLOAD_RUN_ID is required for uploads and must be a stable 1-40 character identifier.');
  }

  let derivativeTarget = '';
  if (derivativeUrl) {
    derivativeTarget = absoluteUrl(baseUrl, derivativeUrl);
    const response = http.get(derivativeTarget, {
      headers: derivativeAuthorization
        ? { Authorization: derivativeAuthorization }
        : {},
      tags: { budget: 'warm-derivative-preflight' },
    });
    if (!validDerivative(response)) {
      throw new Error(
        `VISTARA_DERIVATIVE_URL did not return a non-empty immutable image (status ${response.status}).`);
    }
  }

  return { baseUrl, authorization, derivativeTarget };
}

export function managementRead(data) {
  referenceErrors.add(0);
  const response = http.get(`${data.baseUrl}${managementPath}`, {
    headers: { Authorization: data.authorization, Accept: 'application/json' },
    tags: { budget: 'warm-management-get' },
  });
  managementDuration.add(response.timings.duration);
  const document = responseJson(response);
  const valid = check(response, {
    'management read is 200': (value) => value.status === 200,
    'management read has JSON': (value) =>
      header(value, 'content-type').includes('application/json'),
    'management read returns the populated asset page': () =>
      Array.isArray(document && document.items) &&
      document.items.length >= managementMinimumItems,
    'management read has another page on the reference dataset': () =>
      typeof (document && document.nextCursor) === 'string' &&
      document.nextCursor.length > 0,
  });
  if (!valid) {
    referenceErrors.add(1);
  }
}

export function warmDerivative(data) {
  const response = http.get(data.derivativeTarget, {
    headers: derivativeAuthorization
      ? { Authorization: derivativeAuthorization }
      : {},
    tags: { budget: 'warm-derivative' },
  });
  derivativeWaiting.add(response.timings.waiting);
  const valid = check(response, {
    'warm derivative is a non-empty immutable image': validDerivative,
  });
  if (!valid) {
    referenceErrors.add(1);
  }
}

export function uploadAndAbort(data) {
  uploadErrors.add(0);
  const image = encoding.b64decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    'std');
  const key = `perf-${uploadRunId}-${__VU}-${__ITER}`;
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
  const uploadId = session && session.uploadId;
  const plan = session && session.plan;
  let expectedVersion = header(create, 'etag');
  if (!uploadId || !plan || !expectedVersion) {
    uploadErrors.add(1);
    return;
  }

  const request = uploadRequest(data, plan, expectedVersion);
  if (!request) {
    uploadErrors.add(1);
  } else {
    const content = http.request(request.method, request.url, image, {
      headers: request.headers,
      tags: { budget: 'upload-content' },
    });
    if (!check(content, {
      'declared image bytes reach the issued upload target': (value) =>
        [200, 201, 204].includes(value.status),
    })) {
      uploadErrors.add(1);
    } else if (request.updatesSessionVersion) {
      expectedVersion = header(content, 'etag') || expectedVersion;
    }
  }

  const cleanup = http.del(
    `${data.baseUrl}/api/v1/uploads/${uploadId}`,
    null,
    {
      headers: {
        Authorization: data.authorization,
        'If-Match': expectedVersion,
      },
      tags: { budget: 'upload-cleanup' },
    });
  if (!check(cleanup, {
    'performance upload staging is aborted': (value) => value.status === 204,
  })) {
    uploadErrors.add(1);
  }
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
      detail: `Measured a populated asset page and immutable derivative on ${baseUrl}; upload runs abort their staging sessions.`,
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

function absoluteUrl(origin, path) {
  return path.startsWith('http') ? path : `${origin}${path}`;
}

function responseJson(response) {
  try {
    return response.json();
  } catch (_) {
    return null;
  }
}

function validDerivative(response) {
  return response.status === 200 &&
    header(response, 'content-type').toLowerCase().startsWith('image/') &&
    header(response, 'cache-control').toLowerCase().includes('immutable') &&
    response.body &&
    response.body.length > 0;
}

function uploadRequest(data, plan, expectedVersion) {
  if (plan.contentUrl) {
    return {
      method: 'PUT',
      url: absoluteUrl(data.baseUrl, plan.contentUrl),
      headers: {
        Authorization: data.authorization,
        'Content-Type': 'image/png',
        'If-Match': expectedVersion,
      },
      updatesSessionVersion: true,
    };
  }

  if (plan.request && plan.request.method && plan.request.url) {
    return {
      method: plan.request.method,
      url: absoluteUrl(data.baseUrl, plan.request.url),
      headers: plan.request.headers || {},
      updatesSessionVersion: false,
    };
  }

  return null;
}
