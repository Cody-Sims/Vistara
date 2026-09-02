import path from 'node:path';
import { fileURLToPath } from 'node:url';

const supportDirectory = path.dirname(fileURLToPath(import.meta.url));

export const e2eDirectory = path.resolve(supportDirectory, '..');
export const repositoryRoot = path.resolve(e2eDirectory, '../..');
export const artifactsDirectory = path.join(e2eDirectory, '.artifacts');
export const statePath = path.join(artifactsDirectory, 'state.json');
export const fixturePath = path.join(artifactsDirectory, 'tiny.png');
export const databasePath = path.join(artifactsDirectory, 'vistara-e2e.db');
export const mediaRoot = path.join(artifactsDirectory, 'media');
/**
 * Where the gallery API publishes the public half of the certificate it
 * generated for this run, so probes can pin it. The private key stays in the
 * host process and the artifacts folder is ignored by Git.
 */
export const apiCertificatePath = path.join(
  artifactsDirectory,
  'vistara-e2e-api.cer',
);
export const hostProject = path.join(
  e2eDirectory,
  'host',
  'Vistara.E2E.Host.csproj',
);
export const hostAssembly = path.join(
  e2eDirectory,
  'host',
  'bin',
  'Release',
  'net10.0',
  'Vistara.E2E.Host.dll',
);
export const webDirectory = path.join(repositoryRoot, 'src', 'Vistara.Web');
export const webDist = path.join(webDirectory, 'dist');
