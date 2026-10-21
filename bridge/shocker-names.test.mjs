import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import dgram from 'node:dgram';
import { once } from 'node:events';
import WebSocket from 'ws';
import { renameShocker, readShockerName, validShockerName } from './shocker-names.mjs';
import { startBridge, normalizeConfig } from './bridge.mjs';
import base from './config.example.json' with { type: 'json' };

const uuid = '11111111-1111-1111-1111-111111111111';
const hub = '22222222-2222-2222-2222-222222222222';
const requestId = '33333333-3333-3333-3333-333333333333';
const metadata = { id: uuid, device: hub, rfId: 12345, model: 'CaiXianlin', name: 'Cloud name', isPaused: true };
const tick = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function until(condition, timeout = 5000) {
  const deadline = Date.now() + timeout;
  while (!condition()) { if (Date.now() > deadline) throw new Error('Name fixture timed out'); await tick(5); }
}
async function port() {
  const socket = dgram.createSocket('udp4'); socket.bind(0, '127.0.0.1'); await once(socket, 'listening');
  const value = socket.address().port; socket.close(); return value;
}

test('rename reads current pairing metadata and preserves RF ID, model, hub and pause', async () => {
  const calls = [];
  const result = await renameShocker(uuid, 'New name', async (method, endpoint, body) => {
    calls.push({ method, endpoint, body }); return { data: structuredClone(metadata) };
  });
  assert.equal(result.name, 'New name');
  assert.deepEqual(calls.map(call => call.method), ['GET', 'PATCH']);
  assert.deepEqual(calls[1].body, { name: 'New name', device: hub, rfId: 12345, model: 'CaiXianlin' });
  assert(calls.every(call => call.endpoint === `/1/shockers/${uuid}`));
  assert(!('isPaused' in calls[1].body));
  calls.length = 0;
  await renameShocker(uuid, metadata.name, async method => { calls.push(method); return { data: metadata }; });
  assert.deepEqual(calls, ['GET'], 'already identical names need no write');
});

test('invalid, stale or unknown metadata cannot produce a rename write; failures do not retry', async () => {
  assert(validShockerName('🦊 Toy')); assert(validShockerName('x'.repeat(64)));
  assert(!validShockerName('x'.repeat(65))); assert(!validShockerName('  ')); assert(!validShockerName('a\nb'));
  assert.equal(readShockerName({ data: metadata }, uuid).name, metadata.name);
  assert.equal(readShockerName({ data: metadata }, hub), null);
  for (const change of [{ rfId: null }, { model: 'unknown' }, { device: 'bad' }, { id: hub }]) {
    const calls = [];
    await assert.rejects(renameShocker(uuid, 'New', async method => {
      calls.push(method); return { data: { ...metadata, ...change } };
    }), /metadata/);
    assert.deepEqual(calls, ['GET']);
  }
  const calls = [];
  await assert.rejects(renameShocker(uuid, 'New', async method => {
    calls.push(method); return { data: metadata };
  }, () => false), /canceled/);
  assert.deepEqual(calls, ['GET']);
  let writes = 0;
  await assert.rejects(renameShocker(uuid, 'New', async method => {
    if (method === 'PATCH') { writes++; throw Object.assign(new Error('Forbidden'), { statusCode: 403 }); }
    return { data: metadata };
  }), /Forbidden/);
  assert.equal(writes, 1);
});

test('actual live bridge syncs initial, external and reconnected names without polling or control commands', async () => {
  const calls = [], lines = [];
  let cloud = structuredClone(metadata), socket, connections = 0;
  const server = http.createServer(async (request, response) => {
    let text = ''; for await (const part of request) text += part;
    calls.push({ method: request.method, endpoint: request.url, body: text ? JSON.parse(text) : null });
    if (request.method === 'PATCH') Object.assign(cloud, JSON.parse(text));
    response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify({ data: cloud }));
  });
  const ws = new WebSocket.Server({ server, path: '/1/hubs/user' });
  ws.on('connection', client => {
    socket = client; connections++;
    client.on('message', data => {
      for (const part of data.toString().split('\u001e').filter(Boolean))
        if (JSON.parse(part).protocol === 'json') client.send('{}\u001e');
    });
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const config = normalizeConfig(base);
  Object.assign(config, { dryRun: false, pollIntervalMs: 500, apiBaseUrl: `http://127.0.0.1:${server.address().port}` });
  config.osc = { ...config.osc, listenPort: await port() };
  Object.assign(config.shockers[0], { uuid, name: 'Local placeholder' });
  const bridge = await startBridge(config, 'fake-name-token', { log: line => lines.push(line) });
  const names = () => lines.filter(line => line.startsWith('@name ')).map(line => JSON.parse(line.slice(6)));
  const update = (device = hub, type = 2) => socket.send(JSON.stringify({ type: 1, target: 'DeviceUpdate', arguments: [device, type] }) + '\u001e');
  try {
    await until(() => socket && names().length === 1 && lines.some(line => line.startsWith('@api ') && JSON.parse(line.slice(5)).shockers.every(s => s.state === 'Connected')));
    assert.equal(names()[0].name, 'Cloud name');
    assert.equal(calls.length, 1, 'initial verification supplies the name');
    cloud.name = 'Renamed in app';
    for (let i = 0; i < 25; i++) update();
    await until(() => names().at(-1).name === cloud.name);
    assert.equal(calls.length, 2, 'many live events combine into one metadata read');
    update(uuid); update(hub, 1); await tick(750);
    assert.equal(calls.length, 2, 'unrelated hub or notification type does not refresh');
    config.shockers[0].name = 'Renamed in panel'; config.shockers[0].nameSyncPending = true;
    bridge.configure(config);
    await bridge.rename(config.shockers[0].key, 'Renamed in panel', requestId);
    const result = JSON.parse(lines.find(line => line.startsWith('@rename ')).slice(8));
    assert.equal(result.success, true); assert.equal(result.requestId, requestId);
    assert.equal(cloud.name, 'Renamed in panel'); assert.equal(cloud.rfId, metadata.rfId);
    assert.equal(cloud.model, metadata.model); assert.equal(cloud.device, metadata.device); assert.equal(cloud.isPaused, true);
    config.shockers[0].nameSyncPending = false; bridge.configure(config);
    const count = calls.length;
    await tick(1100); assert.equal(calls.length, count, 'no repeating metadata/name poll');
    socket.close(1011, 'fixture reconnect');
    cloud.name = 'Changed while disconnected';
    await until(() => connections === 2 && names().at(-1).name === cloud.name, 7000);
    assert.equal(calls.length, count + 1, 'reconnect only refreshes metadata once');
    assert(calls.every(call => !call.endpoint.includes('/logs') && !call.endpoint.includes('/control') && call.method !== 'POST'));
  } finally {
    await bridge(); for (const client of ws.clients) client.terminate(); await new Promise(resolve => ws.close(resolve));
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});

test('dry run blocks rename writes and reports the result', async () => {
  const calls = [], lines = [];
  const server = http.createServer((request, response) => {
    calls.push(request.method); response.setHeader('Content-Type', 'application/json'); response.end('{"data":[]}');
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const config = normalizeConfig(base);
  Object.assign(config, { dryRun: true, apiBaseUrl: `http://127.0.0.1:${server.address().port}` });
  config.osc = { ...config.osc, listenPort: await port() };
  Object.assign(config.shockers[0], { uuid, name: 'Pending', nameSyncPending: true });
  const bridge = await startBridge(config, 'fake-token', { liveEvents: false, log: line => lines.push(line) });
  try {
    const count = calls.length; await bridge.rename(config.shockers[0].key, 'Pending', requestId);
    assert.equal(calls.length, count);
    assert.equal(JSON.parse(lines.find(line => line.startsWith('@rename ')).slice(8)).success, false);
  } finally { await bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

test('runtime cancels an outdated rename before PATCH and exposes permission failures without retry', async () => {
  const calls = [], lines = [];
  let release, waiting = false;
  const gate = new Promise(resolve => { release = resolve; });
  const server = http.createServer(async (request, response) => {
    calls.push(request.method + ' ' + request.url);
    response.setHeader('Content-Type', 'application/json');
    if (request.url.includes('/logs')) { response.end('{"data":[]}'); return; }
    if (request.method === 'PATCH') { response.statusCode = 403; response.end('{}'); return; }
    waiting = true; await gate; response.end(JSON.stringify({ data: metadata }));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const config = normalizeConfig(base);
  Object.assign(config, { dryRun: false, pollIntervalMs: 60000, apiBaseUrl: `http://127.0.0.1:${server.address().port}` });
  config.osc = { ...config.osc, listenPort: await port() };
  Object.assign(config.shockers[0], { uuid, name: 'First edit', nameSyncPending: true });
  const bridge = await startBridge(config, 'fake-token', { liveEvents: false, log: line => lines.push(line) });
  try {
    const pending = bridge.rename(config.shockers[0].key, 'First edit', requestId);
    await until(() => waiting);
    config.shockers[0].name = 'Latest edit'; bridge.configure(config); release(); await pending;
    assert(!calls.some(call => call.startsWith('PATCH')), 'changed intent cancels the old write');
    assert.equal(JSON.parse(lines.filter(line => line.startsWith('@rename ')).at(-1).slice(8)).success, false);
    await bridge.rename(config.shockers[0].key, 'Latest edit', requestId);
    const result = JSON.parse(lines.filter(line => line.startsWith('@rename ')).at(-1).slice(8));
    assert.equal(result.success, false); assert.match(result.detail, /Shockers Edit/);
    await tick(800);
    assert.equal(calls.filter(call => call.startsWith('PATCH')).length, 1, 'failed rename is never retried automatically');
    await bridge();
    const count = calls.length; await bridge.rename(config.shockers[0].key, 'Latest edit', requestId);
    assert.equal(calls.length, count, 'stopped bridge cannot start a name request');
  } finally { release(); await bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});
