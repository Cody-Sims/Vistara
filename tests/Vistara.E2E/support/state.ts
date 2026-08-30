import { readFileSync } from 'node:fs';
import { statePath } from './paths.js';

export interface BrowserState {
  readonly tenantId: string;
  readonly userId: string;
  readonly primaryAssetId: string;
  readonly trashAssetId: string;
  readonly apiKey: string;
}

export interface RuntimeState {
  readonly baseUrl: string;
  readonly apiPid: number;
  readonly workerPid: number;
  readonly browsers: Readonly<Record<string, BrowserState>>;
}

export function readRuntimeState(): RuntimeState {
  return JSON.parse(readFileSync(statePath, 'utf8')) as RuntimeState;
}
