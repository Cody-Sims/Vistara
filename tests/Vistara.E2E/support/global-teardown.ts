import { existsSync } from 'node:fs';
import type { FullConfig } from '@playwright/test';
import { readRuntimeState } from './state.js';
import { statePath } from './paths.js';

export default async function globalTeardown(_config: FullConfig) {
  if (!existsSync(statePath)) {
    return;
  }

  const state = readRuntimeState();
  for (const pid of [
    state.apiPid,
    state.workerPid,
    state.oidc?.apiPid,
    state.oidc?.identityProviderPid,
  ]) {
    if (typeof pid !== 'number' || !Number.isInteger(pid) || pid <= 0) {
      continue;
    }

    try {
      process.kill(pid, 'SIGTERM');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ESRCH') {
        throw error;
      }
    }
  }
}
