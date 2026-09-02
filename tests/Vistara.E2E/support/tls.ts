import { X509Certificate } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { request as httpsRequest } from 'node:https';
import { request as httpRequest } from 'node:http';

/**
 * The transport the suite runs over.
 *
 * Every host this run starts serves HTTPS on the loopback interface with a
 * certificate it generated for itself, because Vistara's session cookie and
 * hosted sign-in login handle are `__Host-` cookies and WebKit will not keep a
 * `Secure` cookie that arrived over `http://127.0.0.1`. The harness therefore
 * has to speak TLS, and it does so by pinning exactly the certificate the run
 * published: no probe here relaxes validation, and nothing is trusted beyond
 * the single certificate handed to it.
 */

/**
 * Reads the DER certificate a host published for this run and returns it PEM
 * encoded, which is the form Node's TLS stack takes as a trust anchor.
 */
export function readPinnedCertificate(certificatePath: string): string {
  return new X509Certificate(readFileSync(certificatePath)).toString();
}

export interface ProbeResponse {
  readonly status: number;
  readonly body: string;
}

/**
 * A GET against a host this run started, trusting that host's certificate and
 * nothing else. Validation stays on: an unexpected certificate, or the wrong
 * name on the expected one, fails the probe rather than passing it.
 */
export function pinnedGet(
  url: string,
  certificate: string,
): Promise<ProbeResponse> {
  return new Promise<ProbeResponse>((resolve, reject) => {
    const probe = httpsRequest(url, { ca: [certificate] }, (response) => {
      const chunks: Buffer[] = [];
      response.on('data', (chunk: Buffer) => chunks.push(chunk));
      response.on('end', () =>
        resolve({
          status: response.statusCode ?? 0,
          body: Buffer.concat(chunks).toString('utf8'),
        }),
      );
      response.on('error', reject);
    });
    probe.on('error', reject);
    probe.end();
  });
}

/** The same probe, parsed as the JSON document the endpoint publishes. */
export async function pinnedGetJson<T>(
  url: string,
  certificate: string,
): Promise<{ readonly status: number; readonly body: T }> {
  const response = await pinnedGet(url, certificate);
  return { status: response.status, body: JSON.parse(response.body) as T };
}

/**
 * What a plain-HTTP caller gets from a port this run bound. It is used to
 * prove there is no HTTP listener left: a host that answered here would be one
 * a browser could still be sent to without TLS.
 */
export function plainHttpAttempt(
  url: string,
): Promise<{ readonly status: number } | { readonly error: string }> {
  return new Promise((resolve) => {
    const probe = httpRequest(url, { timeout: 5_000 }, (response) => {
      response.resume();
      resolve({ status: response.statusCode ?? 0 });
    });
    probe.on('error', (error: NodeJS.ErrnoException) =>
      resolve({ error: error.code ?? error.message }),
    );
    probe.on('timeout', () => {
      probe.destroy();
      resolve({ error: 'ETIMEDOUT' });
    });
    probe.end();
  });
}

/** Whether a probe was answered successfully. */
export function isSuccess(status: number): boolean {
  return status >= 200 && status < 300;
}
