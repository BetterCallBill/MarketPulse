import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// tests/e2e's package.json declares "type": "module", so this file runs as ESM under
// Playwright's TS loader — __dirname is not defined here. Derive the equivalent from
// import.meta.url instead (brief originally used __dirname, written for a CJS context).
const __dirname = path.dirname(fileURLToPath(import.meta.url));

export const WORKER_PID_FILE = path.join(__dirname, '.alerts-worker.pid');

/**
 * The Alerts worker exposes no HTTP port, so it cannot join webServer[]. It is spawned
 * detached (its own process group, so teardown can kill `dotnet run`'s whole tree) and
 * given no readiness probe: the spec's first live assertion — a rule flipping to
 * Triggered — is the worker's readiness, with the same generous timeout the API gets.
 */
export default async function globalSetup() {
  const worker = spawn('dotnet', ['run', '--project', '../../src/MarketPulse.Alerts'], {
    cwd: __dirname,
    stdio: 'ignore',
    detached: true,
  });
  worker.unref();

  if (worker.pid === undefined) {
    throw new Error('Failed to spawn the Alerts worker.');
  }

  await writeFile(WORKER_PID_FILE, String(worker.pid));
}
