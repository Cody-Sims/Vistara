# Vistara performance validation

The harness separates deterministic local smoke/BenchmarkDotNet checks from
reference-host k6 and Chromium measurements. Reports are written beneath
`tests/Vistara.PerformanceTests/artifacts/`, which is ignored by Git.

## Local checks

```bash
dotnet run -c Release --project tests/Vistara.PerformanceTests
dotnet run -c Release --project tests/Vistara.PerformanceTests -- \
  --mode benchmark
```

Smoke mode exercises the production gallery query store, NetVips transform
adapter when native codecs are available, concurrent SQLite upload/job
persistence, and built frontend bundle sizes. Missing native codecs or a
production web build are reported as unavailable instead of being substituted
with synthetic success. Smoke mode permits unavailable reference-only budgets
so contributors can run it without external infrastructure. The production
bundle remains in `src/Vistara.Web/dist` when the separate Pages preview is
built into `src/Vistara.Web/dist-pages`.

## Reference host

Populate an isolated test tenant with at least 5,000 assets and a ready public
immutable derivative. Supply authorization through environment variables, not
command-line arguments or committed files.

```bash
VISTARA_BASE_URL=https://reference.example.test \
VISTARA_AUTHORIZATION="$VISTARA_TEST_AUTHORIZATION" \
VISTARA_DERIVATIVE_URL=/media/pipeline/source/recipe.webp \
VISTARA_RUN_UPLOADS=true \
VISTARA_UPLOAD_RUN_ID="$CI_RUN_ID" \
k6 run tests/Vistara.PerformanceTests/k6/reference-host.js
```

The management scenario requires a populated asset page and a next cursor. The
derivative scenario prewarms and validates a non-empty immutable image before
measuring TTFB. Upload load follows the issued proxy or direct upload plan and
then aborts each staging session, so it does not create persistent assets.
`VISTARA_UPLOAD_RUN_ID` must be a unique, stable CI run identifier.

Build and serve the production web application, then run the Chromium scenario
against its populated `/library` route:

```bash
VISTARA_GALLERY_URL=https://reference.example.test/library \
VISTARA_BROWSER_AUTHORIZATION="$VISTARA_TEST_AUTHORIZATION" \
k6 run tests/Vistara.PerformanceTests/k6/frontend-browser.js
```

The browser scenario fails if the route does not render library asset cards and
image bytes. It measures initial asset thumbnails before scrolling, generates a
real view interaction for INP, and scrolls the library timeline rather than the
window.

## Release gate

Import both k6 summaries and require every required budget and prerequisite:

```bash
dotnet run -c Release --project tests/Vistara.PerformanceTests -- \
  --mode evaluate \
  --measurements tests/Vistara.PerformanceTests/artifacts/k6-reference-summary.json \
  --frontend-observations tests/Vistara.PerformanceTests/artifacts/k6-browser-summary.json \
  --require-reference
```

CI should archive the three JSON reports. A release-gate run exits nonzero for
over-budget measurements, failed checks, missing required measurements, or
unavailable/skipped prerequisites.
