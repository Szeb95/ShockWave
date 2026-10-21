import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import dgram from 'node:dgram';
import { once } from 'node:events';
import WebSocket from 'ws';
import { startBridge, normalizeConfig } from './bridge.mjs';
import base from './config.example.json' with { type: 'json' };

const hubA = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const hubB = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const ids = [1, 2, 3].map(i => `${i}`.repeat(8) + '-' + `${i}`.repeat(4) + '-' + `${i}`.repeat(4) + '-' + `${i}`.repeat(4) + '-' + `${i}`.repeat(12));
const tick = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(condition) {
  const deadline = Date.now() + 6000;
  while (!condition()) { if (Date.now() > deadline) throw new Error('Hub fixture timed out'); await tick(5); }
}

async function fixture(initialStates) {
  const lines = [], reads = [];
  let socket, connections = 0, snapshot = initialStates;
  const metadata = new Map(ids.map((id, i) => [id, { id, device: i === 2 ? hubB : hubA, name: `Toy ${i + 1}` }]));
  const server = http.createServer((request, response) => {
    reads.push({ method: request.method, path: request.url });
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ data: metadata.get(request.url.split('/').at(-1)) }));
  });
  const ws = new WebSocket.Server({ server, path: '/1/hubs/user' });
  ws.on('connection', client => {
    socket = client; connections++;
    client.on('message', data => {
      for (const part of data.toString().split('\u001e').filter(Boolean)) {
        if (JSON.parse(part).protocol !== 'json') continue;
        client.send('{}\u001e');
        client.send(JSON.stringify({ type: 1, target: 'Welcome', arguments: ['fixture-connection'] }) + '\u001e');
        if (snapshot !== undefined)
          client.send(JSON.stringify({ type: 1, target: 'DeviceStatus', arguments: [snapshot] }) + '\u001e');
      }
    });
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const udp = dgram.createSocket('udp4'); udp.bind(0, '127.0.0.1'); await once(udp, 'listening');
  const listenPort = udp.address().port; udp.close();
  const config = normalizeConfig({ ...structuredClone(base), shockers: ids.map((uuid, i) => ({
    ...base.shockers[0], key: `toy-${i}`, uuid, name: `Toy ${i + 1}`, avatarGroup: `Fixture_${i}`,
  })) });
  Object.assign(config, { apiBaseUrl: `http://127.0.0.1:${server.address().port}`, pollIntervalMs: 500 });
  config.osc.listenPort = listenPort;
  const bridge = await startBridge(config, 'fake-hub-token', { log: line => lines.push(line) });
  const latest = () => JSON.parse(lines.filter(line => line.startsWith('@api ')).at(-1).slice(5));
  await until(() => socket && latest().shockers.every(s => s.state === 'Connected'));
  return { config, bridge, lines, reads, latest, metadata,
    send: states => socket.send(JSON.stringify({ type: 1, target: 'DeviceStatus', arguments: [states] }) + '\u001e'),
    reconnect: async states => { snapshot = states; const previous = connections; socket.terminate(); await until(() => connections > previous && latest().shockers.every(s => s.state === 'Connected')); },
    disconnect: () => socket.terminate(),
    close: async () => { await bridge(); for (const client of ws.clients) client.terminate(); ws.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); },
  };
}

test('initial hub snapshot deduplicates shared hubs and incremental updates remain separate from API health', async () => {
  const f = await fixture([{ device: hubA.toUpperCase(), online: true, firmwareVersion: '1.0.0' }]);
  try {
    await until(() => f.latest().hub.state === 'Mixed');
    assert.equal(f.latest().hub.total, 2); assert.equal(f.latest().hub.online, 1);
    assert.deepEqual(f.latest().hub.shockers.map(s => s.state), ['Online', 'Online', 'Offline']);
    f.send([{ device: hubB, online: true, firmwareVersion: null }]);
    await until(() => f.latest().hub.state === 'Online');
    assert.equal(f.latest().hub.online, 2, 'partial update does not erase the first hub');
    f.send([{ device: hubA, online: false, firmwareVersion: null }]);
    await until(() => f.latest().hub.state === 'Mixed' && f.latest().hub.shockers[0].state === 'Offline');
    assert(f.latest().shockers.every(s => s.state === 'Connected'), 'offline hardware does not claim an API error');
    f.send([{ device: hubB, online: false }]);
    await until(() => f.latest().hub.state === 'Offline');
    assert(f.lines.some(line => line === 'OpenShock hub status: Offline (2 hubs)'), 'hub changes reach the activity log');
    const before = f.latest().hub;
    for (const bad of [null, {}, [{ device: 'invalid', online: true }], [{ device: hubA, online: 'true' }]]) f.send(bad);
    await tick(70); assert.deepEqual(f.latest().hub, before, 'malformed events cannot claim online or offline');
    f.send([]); await tick(20); assert.deepEqual(f.latest().hub, before, 'later empty lists do not replace incremental state');
    await tick(700); const count = f.reads.length;
    f.config.shockers[0].manualMaxIntensity = 20; f.bridge.configure(f.config);
    await tick(750); assert.equal(f.reads.length, count, 'status and slider changes do not start metadata polling');
    f.config.shockers = f.config.shockers.slice(0, 2); f.bridge.configure(f.config);
    assert.equal(f.latest().hub.total, 1, 'removed shockers no longer count their hub');
    assert(f.reads.every(r => r.method === 'GET' && r.path.startsWith('/1/shockers/')), 'only existing metadata reads, no activation or history requests');
    assert(!f.lines.filter(line => line.startsWith('@api ')).some(line => line.includes(hubA) || line.includes(hubB)), 'raw hub identifiers stay inside the bridge');
  } finally { await f.close(); }
});

test('missing status remains checking; an empty initial snapshot correctly reports offline hubs', async () => {
  const f = await fixture(undefined);
  try {
    assert.equal(f.latest().hub.state, 'Checking');
    assert(f.latest().hub.shockers.every(s => s.state === 'Unknown'));
    f.send([{ device: hubA, online: null }]); await tick(30);
    assert.equal(f.latest().hub.state, 'Checking');
    f.send([]); await until(() => f.latest().hub.state === 'Offline');
    assert.equal(f.latest().hub.total, 2); assert.equal(f.latest().hub.online, 0);
  } finally { await f.close(); }
});

test('losing the live connection clears online state; reconnect requires a fresh snapshot', async () => {
  const f = await fixture([{ device: hubA, online: true }, { device: hubB, online: true }]);
  try {
    await until(() => f.latest().hub.state === 'Online');
    const reconnected = f.reconnect([{ device: hubB, online: true }]);
    await until(() => f.latest().hub.state === 'Unknown');
    assert(f.latest().hub.shockers.every(s => s.state === 'Unknown'));
    await reconnected;
    await until(() => f.latest().hub.state === 'Mixed');
    assert.deepEqual(f.latest().hub.shockers.map(s => s.state), ['Offline', 'Offline', 'Online']);
    const missingSnapshot = f.reconnect(undefined);
    await missingSnapshot;
    assert.equal(f.latest().hub.state, 'Checking', 'connected stream alone cannot restore stale green');
    f.send([]); await until(() => f.latest().hub.state === 'Offline');
  } finally { await f.close(); }
});
