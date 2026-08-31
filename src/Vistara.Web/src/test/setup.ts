import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach, beforeEach } from 'vitest';
import { sessionCredentials } from '../api/credentials';

// Several suites render the whole application; the default one second is not
// enough for those to settle when files run in parallel on a loaded machine.
configure({ asyncUtilTimeout: 10_000 });

// The credential of a browser session outlives no test: each one starts with
// the session unknown, exactly as a freshly opened tab does.
beforeEach(() => {
  sessionCredentials.reset();
});

afterEach(() => {
  cleanup();
});
