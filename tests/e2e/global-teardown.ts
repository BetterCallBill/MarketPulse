import { readFile, rm } from 'node:fs/promises';
import { WORKER_PID_FILE } from './global-setup';

export default async function globalTeardown() {
  try {
    const pid = Number(await readFile(WORKER_PID_FILE, 'utf8'));
    // Negative pid: the detached process group, so the built app dies with `dotnet run`.
    process.kill(-pid, 'SIGTERM');
  } catch {
    // Already gone (or never started) — nothing to clean up.
  } finally {
    await rm(WORKER_PID_FILE, { force: true });
  }
}
