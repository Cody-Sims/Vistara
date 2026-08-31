import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

// Several suites render the whole application; the default one second is not
// enough for those to settle when files run in parallel on a loaded machine.
configure({ asyncUtilTimeout: 5_000 });

afterEach(() => {
  cleanup();
});
