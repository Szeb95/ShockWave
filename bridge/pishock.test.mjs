import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import WebSocket from 'ws';
import { startBridge, normalizeConfig, validateConfig } from './bridge.mjs';
import { readPiShockDevices, piShockPublish } from './pishock.mjs';
import { encodeOsc } from './osc.mjs';
import base from './config.example.json' with { type: 'json' };

const tick = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(condition) {
  const deadline = Date.now() + 4000;
  while (!condition()) { if (Date.now() > deadline) throw new Error('PiShock fixture timed out'); await tick(5); }
}
function settings() {
  return { ...structuredClone(base), provider: 'PiShock', piShockUsername: 'fixture-user', piShockSelfTestEnabled: false, piShockSelfTestShockerKeys: [],
    piShockShockers: [
      { key: 'pi-own', name: 'Own fixture', uuid: '42/7', cooldownMs: 0, manualMaxIntensity: 21, manualMaxDurationMs: 400, maxIntensity: 12, maxDurationMs: 700 },
      { key: 'pi-shared', name: 'Shared fixture', uuid: '43/8/30', avatarGroup: 'PiCtrl_2', cooldownMs: 0, manualMaxIntensity: 80, manualMaxDurationMs: 5000 },
    ] };
}
const owned = [{ clientId: 42, name: 'Fixture hub', shockers: [{ shockerId: 7, name: 'Own fixture', isPaused: false }] }];
const shared = { fixtureOwner: [{ clientId: 43, shockerId: 8, shareId: 30, shockerName: 'Shared fixture', isPaused: false,
  maxIntensity: 25, maxDuration: 2, canShock: false, canVibrate: true, canBeep: true, canLog: false, shareCode: 'fixture-share-secret' }] };

async function fixture(input = settings(), extra = {}) {
  const logs = [], requests = [], commands = [], subscriptions = [], connections = [];
  let mode = 'success', readStatus = 200, authValid = true, ownData = structuredClone(owned), shareData = structuredClone(shared);
  let bridge, clock = Date.now();
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost'); requests.push(url.pathname);
    res.setHeader('Content-Type', 'application/json');
    if (readStatus !== 200) { res.writeHead(readStatus, { 'Retry-After': '2' }); res.end('{"private":"fixture-key fixture-share-secret"}'); return; }
    const response = url.pathname.endsWith('GetUserIfAPIKeyValid') ? authValid ? { UserID: 11 } : false
      : url.pathname.endsWith('GetUserDevices') ? ownData
      : url.pathname.endsWith('GetShareCodesByOwner') ? { fixtureOwner: [30] }
      : url.pathname.endsWith('GetShockersByShareIds') ? shareData : null;
    res.end(JSON.stringify(response));
  });
  const ws = new WebSocket.Server({ server });
  ws.on('connection', socket => {
    connections.push(socket);
    socket.on('message', data => {
      const command = JSON.parse(data.toString());
      if (command.Operation === 'PING') { if (mode !== 'no-pong') socket.send(JSON.stringify({ ErrorCode: null, IsError: false, Message: 'PONG', OriginalCommand: 'PING' })); }
      else if (command.Operation === 'SUBSCRIBE') subscriptions.push(command);
      else if (command.Operation === 'PUBLISH') {
        commands.push(command);
        if (mode === 'success') socket.send(JSON.stringify({ ErrorCode: null, IsError: false, Message: 'Publish successful.', OriginalCommand: JSON.stringify(command) }));
        else if (mode === 'reject') socket.send(JSON.stringify({ ErrorCode: 403, IsError: true, Message: 'fixture-key fixture-share-secret', OriginalCommand: JSON.stringify(command) }));
        else if (mode === 'wrong-ack') socket.send(JSON.stringify({ ErrorCode: null, IsError: false, Message: 'Publish successful.', OriginalCommand: '{"Operation":"PUBLISH","PublishCommands":[]}' }));
        else if (mode === 'disconnect') socket.close();
      }
    });
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const address = `http://127.0.0.1:${server.address().port}`;
  bridge = await startBridge(input, 'fixture-key', { log: line => logs.push(line), externalOsc: true, now: () => clock,
    commandTimeoutMs: 100, piShockUrls: { auth: address, api: address, broker: address.replace('http:', 'ws:') + '/v2' }, ...extra });
  const status = () => JSON.parse(logs.findLast(line => line.startsWith('@api ')).slice(5));
  return { bridge, logs, requests, commands, subscriptions, connections, status,
    mode: value => { mode = value; }, advance: ms => { clock += ms; }, httpStatus: value => { readStatus = value; }, auth: value => { authValid = value; },
    shares: value => { shareData = value; }, own: value => { ownData = value; },
    ready: () => until(() => status().connection.state === 'Connected'),
    close: async () => { bridge(); for (const socket of connections) socket.terminate(); await new Promise(resolve => ws.close(resolve)); await new Promise(resolve => server.close(resolve)); },
  };
}

test('PiShock selection preserves OpenShock cards and validates distinct hardware targets', () => {
  const config = settings();
  const openCards = structuredClone(config.shockers);
  const normalized = normalizeConfig(config);
  assert.equal(normalized.shockers[0].key, 'pi-own'); assert.deepEqual(config.shockers, openCards);
  assert.deepEqual(normalizeConfig(normalized).shockers, normalized.shockers);
  assert.throws(() => validateConfig({ ...config, provider: 'unknown' }, 'fixture-key'));
  for (const uuid of ['https://elsewhere', '0/1', '42/7/secret', '2147483648/1'])
    assert.throws(() => validateConfig({ ...config, piShockShockers: [{ ...config.piShockShockers[0], uuid }] }, 'fixture-key'));
  config.piShockShockers[1].uuid = '42/7/99';
  assert.throws(() => validateConfig(config, 'fixture-key'), /Duplicate/);
});

test('remote PiShock commands use owner limits and freshness, never the manual maximums', async () => {
  const f = await fixture();
  try {
    await f.ready();
    assert.equal(await f.bridge.controlRemote('pi-own', 'Shock', 100, 1200, Date.now() - 1), false);
    assert.equal(f.commands.length, 0);
    assert.equal(await f.bridge.controlRemote('pi-own', 'Shock', 100, 1200, Date.now() + 200), true);
    const command = f.commands.at(-1).PublishCommands[0].Body;
    assert.equal(command.i, 12); assert.equal(command.d, 700);
    assert.equal(await f.bridge.controlRemote('pi-shared', 'Shock', 100, 1000, Date.now() + 200), false);
    assert.equal(await f.bridge.controlRemote('pi-own', 'Vibrate', 100, 20000, Date.now() + 200), false);
    assert.equal(f.commands.length, 1);
  } finally { await f.close(); }
});

test('PiShock permissions, pause, maximums and discrete operation format are enforced', () => {
  const devices = readPiShockDevices(owned, shared), device = devices.get('43/8/30');
  assert.throws(() => piShockPublish(device, { type: 'Shock', intensity: 40, duration: 9000 }, 11, 'fixture'), /not permitted/);
  const publish = piShockPublish(device, { type: 'Vibrate', intensity: 80, duration: 9000 }, 11, 'fixture');
  assert.equal(publish.PublishCommands[0].Target, 'c43-sops-fixture-share-secret');
  assert.deepEqual(publish.PublishCommands[0].Body, { id: 8, m: 'v', i: 25, d: 2000, r: true, l: { u: 11, ty: 'sc', w: false, h: false, o: 'ShockWave fixture' } });
  assert.equal(piShockPublish(device, { type: 'Sound', intensity: 30, duration: 400 }, 11, 'fixture').PublishCommands[0].Body.i, 0);
  assert.equal(piShockPublish({ ...device, maxIntensity: 0 }, { type: 'Sound', intensity: 30, duration: 400 }, 11, 'fixture').PublishCommands[0].Body.m, 'b', 'beep permission is independent of a shock/vibration intensity cap');
  assert.throws(() => piShockPublish({ ...device, paused: true }, { type: 'Vibrate', intensity: 20, duration: 400 }, 11, 'fixture'));
  assert.throws(() => readPiShockDevices(owned, { owner: [{ ...shared.fixtureOwner[0], canShock: undefined }] }));
});

test('cloud discovery, correlated acceptance and feedback never expose keys/share secrets', async () => {
  const f = await fixture();
  try {
    await f.ready();
    assert.equal(f.commands.length, 0);
    assert.equal(f.status().hub.state, 'Unknown', 'broker PONG must not invent hub online state');
    const inventory = JSON.parse(f.logs.findLast(line => line.startsWith('@devices ')).slice(9));
    assert.equal(inventory.devices.length, 2);
    assert.ok(!f.logs.join('\n').includes('fixture-key'));
    assert.ok(!f.logs.join('\n').includes('fixture-share-secret'));
    assert.equal(await f.bridge.control('pi-own', 'Shock'), true);
    assert.equal(f.commands.length, 1); assert.equal(f.commands[0].PublishCommands[0].Target, 'c42-ops');
    assert.equal(f.commands[0].PublishCommands[0].Body.i, 21); assert.equal(f.commands[0].PublishCommands[0].Body.d, 400);
    assert.equal(await f.bridge.control('pi-shared', 'Vibrate'), true);
    assert.equal(f.commands[1].PublishCommands[0].Body.i, 25); assert.equal(f.commands[1].PublishCommands[0].Body.d, 2000);
    assert.equal(await f.bridge.control('pi-shared', 'Shock'), false); assert.equal(f.commands.length, 2);
    assert.ok(f.subscriptions.every(command => !command.Targets.includes('43-log')), 'share without log permission does not subscribe to logs');
    assert.ok(f.logs.some(line => line.includes('"phase":"accepted"')));
  } finally { await f.close(); }
});

test('PiShock rejects, lost acknowledgements, unrelated acknowledgements and disconnects never retry', async () => {
  for (const mode of ['reject', 'drop', 'wrong-ack', 'disconnect']) {
    const f = await fixture();
    try {
      await f.ready(); f.mode(mode);
      assert.equal(await f.bridge.control('pi-own', 'Vibrate'), false, mode);
      await tick(130); assert.equal(f.commands.length, 1, mode);
      assert.ok(f.logs.some(line => line.includes(`"phase":"${mode === 'reject' ? 'rejected' : 'uncertain'}"`)));
      assert.ok(!f.logs.join('\n').includes('fixture-key')); assert.ok(!f.logs.join('\n').includes('fixture-share-secret'));
    } finally { await f.close(); }
  }
});

test('pending commands do not queue more operations and ignore late acknowledgements', async () => {
  const f = await fixture();
  try {
    await f.ready(); f.mode('drop');
    const pending = f.bridge.control('pi-own', 'Vibrate');
    assert.equal(await f.bridge.control('pi-own', 'Shock'), false);
    assert.equal(await pending, false); assert.equal(f.commands.length, 1);
    const before = f.logs.filter(line => line.includes('"phase":"accepted"')).length;
    f.connections[0].send(JSON.stringify({ ErrorCode: null, IsError: false, Message: 'Publish successful.', OriginalCommand: JSON.stringify(f.commands[0]) }));
    await tick(30); assert.equal(f.logs.filter(line => line.includes('"phase":"accepted"')).length, before);
  } finally { await f.close(); }
});

test('Test mode previews without sends, activity notices or OpenShock requests', async () => {
  const f = await fixture({ ...settings(), dryRun: true });
  try {
    await f.ready();
    assert.equal(await f.bridge.control('pi-own', 'Shock'), true);
    assert.equal(f.commands.length, 0); assert.ok(f.logs.some(line => line.includes('TEST ONLY')));
    assert.ok(!f.logs.some(line => line.startsWith('@activity ')));
    assert.ok(f.requests.every(path => path.startsWith('/Auth/') || path.startsWith('/PiShock/')));
  } finally { await f.close(); }
});

test('OSC uses avatar limits, needs a fresh idle edge and cannot target inactive OpenShock devices', async () => {
  const config = settings(), f = await fixture(config);
  const osc = (suffix, value) => f.bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_' + suffix, value));
  try {
    await f.ready();
    osc('Intensity', 90); osc('Duration', 100); osc('Action', 1); await tick(120); assert.equal(f.commands.length, 0);
    osc('Action', 0); osc('Action', 2); osc('Action', 2); await until(() => f.commands.length === 1);
    assert.equal(f.commands[0].PublishCommands[0].Body.i, 12); assert.equal(f.commands[0].PublishCommands[0].Body.d, 700);
    assert.equal(await f.bridge.control('legacy-shocker', 'Shock'), false);
    const next = structuredClone(config); next.piShockShockers[0].paused = true; f.bridge.configure(next);
    assert.equal(await f.bridge.control('pi-own', 'Shock'), false); assert.equal(f.commands.length, 1);
    assert.throws(() => f.bridge.configure({ ...next, provider: 'OpenShock' }));
  } finally { await f.close(); }
});

test('disconnect cancels captured input; reconnection never replays an action', async () => {
  const f = await fixture();
  try {
    await f.ready();
    for (const [suffix, value] of [['Intensity', 40], ['Duration', 20], ['Action', 0], ['Action', 2]])
      f.bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_' + suffix, value));
    f.connections[0].close(); await until(() => f.status().connection.state !== 'Connected');
    await tick(140); assert.equal(f.commands.length, 0);
    assert.equal(await f.bridge.control('pi-own', 'Shock'), false);
    f.advance(6000); await until(() => f.connections.length > 1); await f.ready();
    await tick(120); assert.equal(f.commands.length, 0);
    f.bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 1)); await tick(120); assert.equal(f.commands.length, 0);
  } finally { await f.close(); }
});

test('fresh share revocation/paused state blocks output and stale lists fail closed', async () => {
  const f = await fixture();
  try {
    await f.ready();
    const paused = structuredClone(shared); paused.fixtureOwner[0].isPaused = true; f.shares(paused);
    await f.bridge.refreshDevices(); assert.equal(await f.bridge.control('pi-shared', 'Vibrate'), false);
    f.shares({}); await f.bridge.refreshDevices(); assert.equal(await f.bridge.control('pi-shared', 'Vibrate'), false);
    f.advance(91000); assert.equal(await f.bridge.control('pi-own', 'Vibrate'), false);
    assert.equal(f.commands.length, 0);
  } finally { await f.close(); }
});

test('HTTP throttling respects Retry-After and metadata recovery never sends commands', async () => {
  const f = await fixture();
  try {
    await f.ready(); f.httpStatus(429); await f.bridge.refreshDevices();
    const count = f.requests.length; await f.bridge.refreshDevices(); assert.equal(f.requests.length, count);
    assert.equal(await f.bridge.control('pi-own', 'Shock'), false);
    for (const [suffix, value] of [['Intensity', 40], ['Duration', 20], ['Action', 0], ['Action', 2]])
      f.bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_' + suffix, value));
    f.httpStatus(200); f.advance(2001); await f.bridge.refreshDevices(); await f.ready(); await tick(130);
    assert.equal(f.commands.length, 0);
  } finally { await f.close(); }
});

test('invalid credentials and malformed permissions clear access without leaking response bodies', async () => {
  const f = await fixture();
  try {
    await f.ready(); f.auth(false); await f.bridge.refreshDevices();
    assert.equal(f.status().connection.statusCode, 401); assert.equal(await f.bridge.control('pi-own', 'Shock'), false);
    f.auth(true); f.advance(61000); f.shares({ fixtureOwner: [{ ...shared.fixtureOwner[0], maxIntensity: '100' }] });
    await f.bridge.refreshDevices(); assert.equal(f.status().connection.state, 'Error'); assert.equal(f.commands.length, 0);
  } finally { await f.close(); }
});
