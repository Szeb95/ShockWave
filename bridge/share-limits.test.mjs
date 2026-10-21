import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import dgram from 'node:dgram';
import { once } from 'node:events';
import { applyShareLimits } from './share-limits.mjs';
import { normalizeConfig, validateConfig, actionRequest, startBridge } from './bridge.mjs';
import base from './config.example.json' with { type: 'json' };

const shockerId = '11111111-1111-1111-1111-111111111111';
const userId = '22222222-2222-2222-2222-222222222222';
const linkId = '33333333-3333-3333-3333-333333333333';
const requestId = '44444444-4444-4444-4444-444444444444';
const permissions = { shock: false, vibrate: true, sound: true, live: false };
const settings = { uuid: shockerId, maxIntensity: 40, maxDurationMs: 2000, cooldownMs: 1000 };
const user = { sharedWith: { id: userId }, permissions, limits: { intensity: 25, duration: null }, paused: true };
const linkShocker = { id: shockerId, permissions, limits: { intensity: null, duration: 1000 }, paused: true };
function fixture(method, endpoint) {
  if (method === 'PATCH') return null;
  if (endpoint.endsWith('/shares')) return { data: [structuredClone(user)] };
  if (endpoint === '/1/shares/links') return { data: [{ id: linkId }] };
  if (endpoint.endsWith(linkId)) return { data: { devices: [{ shockers: [structuredClone(linkShocker)] }] } };
  if (endpoint.includes('/logs')) return { data: [] };
  throw new Error('Unexpected request: ' + method + ' ' + endpoint);
}

test('legacy card ranges seed saved owner ranges without changing other-controller limits', () => {
  const legacy = { ...base, shockers: undefined, maxIntensity: 35, maxDurationMs: 2400 };
  const migrated = normalizeConfig(legacy).shockers[0];
  assert.equal(migrated.manualMaxIntensity, 35);
  assert.equal(migrated.manualMaxDurationMs, 2400);
  const config = normalizeConfig({ ...base, dryRun: true });
  Object.assign(config.shockers[0], settings, { manualMinIntensity: 0, manualMaxIntensity: 70,
    manualMinDurationMs: 300, manualMaxDurationMs: 4500 });
  validateConfig(config);
  const action = { type: 'Vibrate', intensity: 80, durationMs: 6000 };
  assert.equal(actionRequest(config, action).shocks[0].intensity, 40);
  assert.equal(actionRequest(config, action).shocks[0].duration, 2000);
  assert.equal(actionRequest(config, action, undefined, true).shocks[0].intensity, 70);
  assert.equal(actionRequest(config, action, undefined, true).shocks[0].duration, 4500);
  config.shockers[0].manualMaxIntensity = 101;
  assert.throws(() => validateConfig(config), /Manual intensity/);
});

test('OpenShock share caps preserve tighter limits, permissions and pause without device commands', async () => {
  const calls = [];
  const result = await applyShareLimits(settings, async (method, endpoint, body) => {
    calls.push({ method, endpoint, body }); return fixture(method, endpoint);
  });
  assert.deepEqual(result, { applied: 2, total: 2 });
  const writes = calls.filter(call => call.method === 'PATCH');
  assert.deepEqual(writes[0].body, { permissions, limits: { intensity: 25, duration: 2000 } });
  assert.deepEqual(writes[1].body, { permissions, limits: { intensity: 40, duration: 1000 }, cooldown: 1000 });
  assert.equal(writes[0].endpoint, `/1/shockers/${shockerId}/shares/${userId}`);
  assert.equal(writes[1].endpoint, `/1/shares/links/${linkId}/${shockerId}`);
  assert(calls.every(call => call.method !== 'POST' && !call.endpoint.includes('/control')));
  assert(writes.every(call => !('paused' in call.body)));
});

test('malformed public permissions fail before any write; share write failures are partial and never retried', async () => {
  const calls = [];
  await assert.rejects(applyShareLimits(settings, async (method, endpoint, body) => {
    calls.push({ method, endpoint, body });
    const data = fixture(method, endpoint);
    if (endpoint.endsWith(linkId)) delete data.data.devices[0].shockers[0].permissions.live;
    return data;
  }), /permissions/);
  assert(!calls.some(call => call.method === 'PATCH'));
  let writes = 0;
  await assert.rejects(applyShareLimits(settings, async (method, endpoint) => {
    if (method === 'PATCH' && ++writes === 2) throw Object.assign(new Error('Rate limited'), { statusCode: 429 });
    return fixture(method, endpoint);
  }), error => error.appliedShares === 1 && error.totalShares === 2 && error.statusCode === 429);
  assert.equal(writes, 2);
});

async function port() {
  const socket = dgram.createSocket('udp4'); socket.bind(0, '127.0.0.1'); await once(socket, 'listening');
  const value = socket.address().port; socket.close(); return value;
}
test('actual bridge reports explicit Apply result over stdout and dry run blocks every sharing write', async () => {
  const calls = [], lines = [];
  const server = http.createServer(async (request, response) => {
    let text = ''; for await (const part of request) text += part;
    calls.push({ method: request.method, endpoint: request.url, body: text ? JSON.parse(text) : null });
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify(fixture(request.method, request.url)));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const config = normalizeConfig(base);
  Object.assign(config, { dryRun: false, apiBaseUrl: `http://127.0.0.1:${server.address().port}`, pollIntervalMs: 60000 });
  config.osc = { ...config.osc, listenPort: await port() };
  Object.assign(config.shockers[0], settings);
  let bridge;
  try {
    bridge = await startBridge(config, 'fake-token', { liveEvents: false, log: line => lines.push(line) });
    await bridge.applyLimits(config.shockers[0].key, requestId);
    const result = JSON.parse(lines.find(line => line.startsWith('@limits ')).slice(8));
    assert.equal(result.requestId, requestId); assert.equal(result.success, true);
    assert.match(result.detail, /2 existing shares/);
    assert.equal(calls.filter(call => call.method === 'PATCH').length, 2);
    assert(!calls.some(call => call.method === 'POST'));
    await bridge();
    config.dryRun = true; config.osc.listenPort = await port();
    bridge = await startBridge(config, 'fake-token', { liveEvents: false, log: line => lines.push(line) });
    const count = calls.length;
    await bridge.applyLimits(config.shockers[0].key, requestId);
    assert.equal(calls.length, count, 'dry run adds no requests for Apply');
    assert.equal(JSON.parse(lines.filter(line => line.startsWith('@limits ')).at(-1).slice(8)).success, false);
  } finally {
    if (bridge) await bridge();
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});
