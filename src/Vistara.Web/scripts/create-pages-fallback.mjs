import { copyFile } from 'node:fs/promises';
import { URL } from 'node:url';

await copyFile(
  new URL('../dist-pages/index.html', import.meta.url),
  new URL('../dist-pages/404.html', import.meta.url),
);
