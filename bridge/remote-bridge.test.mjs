import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import sample from './config.example.json' with { type: 'json' };

test('remote bridge IPC requires current session and expiry, applies other-controller limits, and revocation blocks later commands', { timeout: 10000 }, async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'shockwave-remote-'));
  const config = structuredClone(sample); config.dryRun = true;
  const s = config.shockers[0]; s.key = 'fixture'; s.cooldownMs = 0; s.minIntensity = 0; s.maxIntensity = 17; s.maxDurationMs = 900; s.manualMaxIntensity = 80; s.manualMaxDurationMs = 5000;
  const file = path.join(directory, 'config.json'); await writeFile(file, JSON.stringify(config));
  const env = { ...process.env, SHOCKWAVE_EXTERNAL_OSC: '1' }; delete env.OPENSHOCK_TOKEN; delete env.PISHOCK_API_KEY;
  const child = spawn(process.execPath, ['bridge/bridge.mjs', file], { env, windowsHide: true });
  let output = ''; child.stdout.on('data', data => { output += data; }); child.stderr.on('data', () => {});
  const send = value => child.stdin.write(JSON.stringify(value) + '\n');
  const wait = async condition => { const until = Date.now() + 5000; while (!condition()) { if (Date.now() > until) throw new Error('IPC fixture timeout'); await new Promise(r => setTimeout(r, 10)); } };
  try {
    await wait(() => output.includes('@api '));
    const session = 'a'.repeat(64), request = { kind: 'remoteControl', session, key: 'fixture', type: 'Vibrate', intensity: 95, durationMs: 1200 };
    send({ ...request, expiresAt: Date.now() + 200 });
    send({ kind: 'remoteGate', session });
    send({ ...request, session: 'b'.repeat(64), expiresAt: Date.now() + 200 });
    // This crosses a process boundary: avoid a one-millisecond clock-resolution race on Windows.
    send({ ...request, expiresAt: Date.now() - 1000 });
    send({ ...request, expiresAt: Date.now() + 200 });
    await wait(() => output.includes('TEST ONLY:'));
    send({ kind: 'remoteGate', session: '' }); send({ ...request, expiresAt: Date.now() + 200 });
    await new Promise(r => setTimeout(r, 100));
    assert.equal(output.match(/TEST ONLY:/g)?.length, 1);
    assert.match(output, /17%.*900/);
    config.shockers[0].minDurationMs = 16000; config.shockers[0].maxDurationMs = 20000; config.shockers[0].manualMinDurationMs = 300;
    send({ kind: 'configure', config }); send({ kind: 'remoteGate', session });
    send({ ...request, expiresAt: Date.now() + 200 });
    send({ ...request, durationMs: 20000, expiresAt: Date.now() + 200 });
    await new Promise(r => setTimeout(r, 100));
    assert.equal(output.match(/TEST ONLY:/g)?.length, 1, 'owner minimums cannot enlarge remote operations beyond 15 seconds');
  } finally { child.kill(); await new Promise(r => child.once('exit', r)); await rm(directory, { recursive: true, force: true }); }
});
