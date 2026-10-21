import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import dgram from 'node:dgram';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodeOsc, encodeOsc } from './osc.mjs';
import { OscActionCapture } from './capture.mjs';
import { actionRequest, feedbackFromLog, feedbackFromLiveLog, validateConfig, normalizeConfig, startBridge as startBridgeRuntime, apiRequest } from './bridge.mjs';
import sample from './config.example.json' with { type: 'json' };
import WebSocket from 'ws';

const startBridge = (settings, token, options = {}) => startBridgeRuntime(settings, token, { liveEvents: false, ...options });
const config = { ...sample, dryRun: true }; // Tests explicitly opt into preview.

test('fresh packaged settings have Test mode off with no configured hardware', () => {
  assert.equal(sample.dryRun, false);
  assert.equal(sample.shockers.some(s => s.uuid), false);
});

test('OSC encodes and decodes the types VRChat uses', () => {
  for (const value of [true, false, 42, 0.25, 'hello']) {
    const [message] = decodeOsc(encodeOsc('/avatar/parameters/Test', value));
    assert.equal(message.address, '/avatar/parameters/Test');
    assert.deepEqual(message.args, [value]);
  }
  const floatZero = encodeOsc('/avatar/parameters/Test', 0, 'float');
  assert.equal(floatZero.toString('ascii').includes(',f'), true);
});

test('sender values are captured once on an idle-to-action transition', async () => {
  const actions = [];
  const capture = new OscActionCapture(action => actions.push(action), 5);
  capture.setIntensity(25);
  capture.setDuration(40);
  capture.setAction(1); // Ignore a nonzero startup value until idle is seen.
  capture.setAction(0);
  capture.setAction(1);
  capture.setAction(1); // Repeated OSC value must not duplicate a command.
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.deepEqual(actions, [{ type: 'Shock', intensity: 25, duration: 40 }]);
  capture.setAction(0);
  capture.setAction(2);
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(actions[1].type, 'Vibrate');
  capture.setAction(0);
  capture.setAction(3);
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(actions[2].type, 'Sound');
  capture.close();
});

test('100 duration units become 15 seconds and intensity stays 0–100', () => {
  validateConfig(config);
  assert.deepEqual(actionRequest(config, { type: 'Shock', intensity: 100, duration: 100 }), {
    shocks: [{ id: '', type: 'Shock', intensity: 100, duration: 15000, exclusive: false }],
    customName: 'PiCtrl OSC Bridge',
  });
  assert.equal(actionRequest(config, { type: 'Vibrate', intensity: 10, duration: 20 }).shocks[0].duration, 3000);
  assert.equal(actionRequest(config, { type: 'Shock', intensity: 10, duration: 0 }), null);
});

test('local-only live settings need no OpenShock token; configured OpenShock output still does', () => {
  assert.doesNotThrow(() => validateConfig({ ...config, dryRun: false, shockers: [] }, ''));
  assert.doesNotThrow(() => validateConfig({ ...config, dryRun: false }, ''));
  assert.throws(() => validateConfig({ ...multipleShockers(), dryRun: false }, ''), /OPENSHOCK_TOKEN/);
});

test('log feedback includes only recognized actions for the target shocker', () => {
  const id = '11111111-1111-1111-1111-111111111111';
  const entry = { id: 'log-1', shockerId: id, type: 'Vibrate', intensity: 20, duration: 500 };
  assert.equal(feedbackFromLog(entry, id).type, 'Vibrate');
  assert.equal(feedbackFromLog(entry, '22222222-2222-2222-2222-222222222222'), null);
});

function multipleShockers() {
  const base = normalizeConfig(config);
  return { ...base, shockers: [
    { ...base.shockers[0], key: 'left', name: 'Left', uuid: '11111111-1111-1111-1111-111111111111',
      manualMinIntensity: 10, manualMaxIntensity: 40, manualMinDurationMs: 500, manualMaxDurationMs: 2000,
      minIntensity: 10, maxIntensity: 40, minDurationMs: 500, maxDurationMs: 2000 },
    { ...base.shockers[0], key: 'right', name: 'Right', uuid: '22222222-2222-2222-2222-222222222222',
      avatarGroup: 'PiCtrl_2', intensityAddress: '/avatar/parameters/PiCtrl_2_Intensity',
      durationAddress: '/avatar/parameters/PiCtrl_2_Duration', actionAddress: '/avatar/parameters/PiCtrl_2_Action',
      feedbackEventAddress: '/avatar/parameters/PiCtrl_2_FeedbackEvent',
      feedbackActiveAddress: '/avatar/parameters/PiCtrl_2_FeedbackActive',
      feedbackIntensityAddress: '/avatar/parameters/PiCtrl_2_FeedbackIntensity',
      feedbackModeAddress: '/avatar/parameters/PiCtrl_2_FeedbackMode',
      cooldownMs: 0, maxIntensity: 75, manualMaxIntensity: 75 },
  ] };
}

test('closing activity tracks pending/accepted/rejected commands without retry, secrets or Test mode output', async () => {
  const messages = [], pendingResponses = [];
  let posts = 0;
  const server = http.createServer(async (request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.method === 'POST') {
      for await (const chunk of request) { /* consume fixture command */ }
      posts++; pendingResponses.push(response);
    } else response.end(JSON.stringify({ data: [] }));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const settings = multipleShockers();
  Object.assign(settings, { dryRun: false, apiBaseUrl: `http://127.0.0.1:${server.address().port}`, pollIntervalMs: 60000 });
  settings.shockers[0].cooldownMs = 0;
  const bridge = await startBridge(settings, 'fixture-token', { externalOsc: true, log: line => messages.push(line) });
  const activity = () => messages.filter(line => line.startsWith('@activity ')).map(line => JSON.parse(line.slice(10)));
  try {
    assert.equal(activity().length, 0, 'connection checks alone report no output');
    const first = bridge.control('left', 'Vibrate');
    await waitFor(() => pendingResponses.length === 1, 'in-flight fixture command');
    assert.equal(activity()[0].phase, 'pending');
    assert.equal(activity()[0].remainingMs, 10000);
    pendingResponses.shift().end('{}'); assert.equal(await first, true);
    assert.equal(activity()[1].phase, 'accepted'); assert.equal(activity()[1].remainingMs, 2000);
    assert.equal(activity()[1].id, activity()[0].id);
    const second = bridge.control('left', 'Shock');
    await waitFor(() => pendingResponses.length === 1, 'rejected fixture command');
    const rejected = pendingResponses.shift(); rejected.statusCode = 403; rejected.end('{}');
    assert.equal(await second, false); assert.equal(activity().at(-1).phase, 'rejected');
    const third = bridge.control('left', 'Sound');
    await waitFor(() => pendingResponses.length === 1, 'uncertain fixture command');
    const uncertain = pendingResponses.shift(); uncertain.statusCode = 500; uncertain.end('{}');
    assert.equal(await third, false); assert.equal(activity().at(-1).phase, 'uncertain');
    assert.equal(activity().at(-1).remainingMs, 2000);
    await new Promise(resolve => setTimeout(resolve, 100)); assert.equal(posts, 3, 'failed commands are not retried');
    assert.equal(JSON.stringify(activity()).includes(settings.shockers[0].uuid), false, 'activity contains no hardware UUID');
    assert.equal(JSON.stringify(activity()).includes('fixture-token'), false);
  } finally { await bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
  const testMessages = [];
  const testBridge = await startBridge({ ...settings, dryRun: true }, '', { externalOsc: true, log: line => testMessages.push(line) });
  try {
    await testBridge.control('left', 'Vibrate');
    assert.equal(testMessages.some(line => line.startsWith('@activity ')), false, 'Test mode never reports physical output');
  } finally { await testBridge(); }
});

test('closing activity uses remaining feedback duration, skips expired history and respects Stop ordering', async () => {
  const messages = []; let history = [];
  const server = http.createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify({ data: history }));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const settings = multipleShockers();
  Object.assign(settings, { dryRun: true, apiBaseUrl: `http://127.0.0.1:${server.address().port}`, pollIntervalMs: 60000 });
  const now = Date.now();
  const bridge = await startBridge(settings, 'fixture-token', { externalOsc: true, now: () => now, log: line => messages.push(line) });
  const entry = (id, at, type = 'Vibrate', duration = 2000) => ({ id, shockerId: settings.shockers[0].uuid, type,
    intensity: 30, duration, createdOn: new Date(at).toISOString() });
  const activity = () => messages.filter(line => line.startsWith('@activity ')).map(line => JSON.parse(line.slice(10)));
  try {
    history = [entry('expired', now - 3000)]; await bridge.poll();
    assert.equal(activity().filter(event => event.remainingMs > 0).length, 0);
    history.push(entry('fresh', now - 500)); await bridge.poll();
    assert.equal(activity().at(-1).phase, 'feedback'); assert.equal(activity().at(-1).remainingMs, 1500);
    history.push(entry('old-stop', now - 600, 'Stop', 0)); await bridge.poll();
    assert.equal(activity().at(-1).phase, 'feedback', 'late older Stop cannot erase newer feedback');
    history.push(entry('fresh-stop', now, 'Stop', 0)); await bridge.poll();
    assert.equal(activity().at(-1).phase, 'stop');
    const count = activity().length; await bridge.poll(); assert.equal(activity().length, count, 'duplicate feedback is not replayed');
  } finally { await bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

test('each shocker clips values to its own range without scaling intensity', () => {
  const settings = multipleShockers();
  const left = actionRequest(settings, { type: 'Shock', intensity: 80, duration: 100 }, 'left').shocks[0];
  assert.deepEqual(left, { id: settings.shockers[0].uuid, type: 'Shock', intensity: 40, duration: 2000, exclusive: false });
  const low = actionRequest(settings, { type: 'Vibrate', intensity: 1, durationMs: 300 }, 'left').shocks[0];
  assert.equal(low.intensity, 10);
  assert.equal(low.duration, 500);
  assert.equal(actionRequest(settings, { type: 'Sound', intensity: 80, durationMs: 1000 }, 'right').shocks[0].intensity, 75);
  assert.equal(actionRequest(settings, { type: 'Shock', intensity: 0, duration: 20 }, 'left'), null);
  assert.equal(actionRequest(settings, { type: 'Shock', intensity: 80, duration: 20 }, 'unknown'), null);
  settings.shockers[0].paused = true;
  assert.equal(actionRequest(settings, { type: 'Shock', intensity: 80, duration: 20 }, 'left'), null);
});

test('legacy settings migrate and duplicate UUIDs or invalid ranges are rejected', () => {
  const legacy = { ...config, shockers: undefined, shockerId: '11111111-1111-1111-1111-111111111111', maxIntensity: 35,
    maxDurationMs: 2500, minCommandGapMs: 900, durationInputMax: 100, durationAtInputMaxMs: 15000 };
  const migrated = validateConfig(legacy);
  assert.equal(migrated.shockers[0].uuid, legacy.shockerId);
  assert.equal(migrated.shockers[0].maxIntensity, 35);
  assert.equal(migrated.shockers[0].cooldownMs, 900);
  const duplicate = multipleShockers();
  duplicate.shockers[1].uuid = duplicate.shockers[0].uuid.toUpperCase();
  assert.throws(() => validateConfig(duplicate), /Duplicate shocker UUID/);
  const invalid = multipleShockers();
  invalid.shockers[0].minIntensity = 80;
  assert.throws(() => validateConfig(invalid), /Intensity range/);
});

async function freeUdpPort() {
  const socket = dgram.createSocket('udp4');
  socket.bind(0, '127.0.0.1');
  await once(socket, 'listening');
  const port = socket.address().port;
  socket.close();
  await once(socket, 'close');
  return port;
}

async function waitFor(condition, description, waitMs = 3000) {
  const timeout = Date.now() + waitMs;
  while (!condition()) {
    if (Date.now() > timeout) throw new Error('Timed out: ' + description);
    await new Promise(resolve => setTimeout(resolve, 20));
  }
}

test('runtime routes manual and OSC actions, and preserves independent cooldown and pause', async () => {
  const received = [];
  const server = http.createServer(async (request, response) => {
    if (request.method === 'POST') {
      let body = '';
      for await (const chunk of request) body += chunk;
      received.push(JSON.parse(body));
    }
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ data: [] }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const settings = multipleShockers();
  settings.dryRun = false;
  settings.apiBaseUrl = `http://127.0.0.1:${server.address().port}`;
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort() };
  const messages = [];
  const bridge = await startBridge(settings, 'local-test-token', { log: message => messages.push(message) });
  const sender = dgram.createSocket('udp4');
  try {
    assert.equal(await bridge.control('left', 'Shock'), true);
    assert.equal(await bridge.control('left', 'Vibrate'), false);
    assert.equal(await bridge.control('right', 'Sound'), true);
    assert.equal(received[0].shocks[0].id, settings.shockers[0].uuid);
    assert.equal(received[0].shocks[0].intensity, 40);
    assert.equal(received[1].shocks[0].id, settings.shockers[1].uuid);
    assert.equal(received[1].shocks[0].type, 'Sound');
    const updated = structuredClone(settings);
    updated.shockers[0].paused = true;
    updated.shockers[1].maxIntensity = 20;
    updated.shockers[1].manualMaxIntensity = 30;
    bridge.configure(updated);
    assert.equal(await bridge.control('left', 'Sound'), false);
    assert.equal(await bridge.control('right', 'Vibrate'), true);
    assert.equal(received[2].shocks[0].intensity, 30);
    updated.shockers[0].paused = false;
    bridge.configure(updated);
    assert.equal(await bridge.control('left', 'Shock'), false, 'saving settings must not reset cooldown');
    async function osc(address, value) {
      await new Promise((resolve, reject) => sender.send(encodeOsc(address, value), settings.osc.listenPort,
        '127.0.0.1', error => error ? reject(error) : resolve()));
    }
    await osc('/avatar/parameters/PiCtrl_2_Intensity', 80);
    await osc('/avatar/parameters/PiCtrl_2_Duration', 20);
    await osc('/avatar/parameters/PiCtrl_2_Action', 0);
    await osc('/avatar/parameters/PiCtrl_2_Action', 2);
    await waitFor(() => received.length === 4, 'right avatar action');
    assert.deepEqual(received[3].shocks[0], { id: settings.shockers[1].uuid, type: 'Vibrate', intensity: 20,
      duration: 3000, exclusive: false });
    updated.shockers = updated.shockers.filter(s => s.key === 'left');
    bridge.configure(updated);
    assert.equal(await bridge.control('right', 'Shock'), false);
    assert(messages.some(line => line.includes('cooldown')));
    assert(messages.some(line => line.includes('paused in overlay')));
  } finally {
    bridge();
    try { sender.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});

test('test mode logs manual Shock, Vibrate, and Sound without API requests', async () => {
  const settings = multipleShockers();
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort() };
  settings.shockers[0].cooldownMs = 0;
  const messages = [];
  const bridge = await startBridge(settings, '', { log: message => messages.push(message) });
  try {
    for (const type of ['Shock', 'Vibrate', 'Sound']) assert.equal(await bridge.control('left', type), true);
    assert.equal(messages.filter(line => line.includes('TEST ONLY')).length, 3);
    assert(messages.some(line => line.includes('TEST ONLY: Shock 40% for 2000 ms')));
  } finally { bridge(); }
});

test('self test validates selected devices and keeps old configurations in normal routing', () => {
  const old = multipleShockers();
  delete old.selfTestEnabled; delete old.selfTestShockerKeys; delete old.selfTestAvatarGroup;
  assert.equal(validateConfig(old).selfTestEnabled, false);
  const settings = multipleShockers();
  settings.selfTestEnabled = true;
  assert.throws(() => validateConfig(settings), /Select at least one/);
  settings.selfTestShockerKeys = ['missing'];
  assert.throws(() => validateConfig(settings), /existing devices/);
  settings.selfTestShockerKeys = ['right', 'right'];
  assert.throws(() => validateConfig(settings), /existing devices/);
  settings.selfTestShockerKeys = ['right'];
  settings.selfTestAvatarGroup = 'bad/group';
  assert.throws(() => validateConfig(settings), /VRChat test group/);
  settings.selfTestAvatarGroup = 'PiCtrl';
  settings.dryRun = false;
  settings.shockers[1].uuid = '';
  assert.throws(() => validateConfig(settings, 'mock-token'), /own UUID/);
});

test('self test sends avatar OSC only to selected devices, with limits, pause, cooldown, and API feedback', async () => {
  const commands = [], logs = [], messages = [], feedbackMessages = [];
  const server = http.createServer(async (request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.method === 'POST') {
      let body = ''; for await (const chunk of request) body += chunk;
      commands.push(JSON.parse(body)); response.end('{}');
    } else response.end(JSON.stringify({ data: logs }));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const feedback = dgram.createSocket('udp4');
  feedback.bind(0, '127.0.0.1'); await once(feedback, 'listening');
  feedback.on('message', packet => feedbackMessages.push(...decodeOsc(packet)));
  const settings = multipleShockers();
  Object.assign(settings, { dryRun: false, selfTestEnabled: true, selfTestShockerKeys: ['right'],
    apiBaseUrl: `http://127.0.0.1:${server.address().port}`, pollIntervalMs: 60000 });
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort(), sendPort: feedback.address().port };
  settings.shockers[1].maxDurationMs = 3000;
  settings.shockers[1].manualMaxDurationMs = 3000;
  settings.shockers[1].maxIntensity = 25;
  settings.shockers[1].cooldownMs = 1000;
  const bridge = await startBridge(settings, 'mock-token', { log: line => messages.push(line) });
  const sender = dgram.createSocket('udp4');
  const osc = (suffix, value, group = 'PiCtrl') => new Promise((resolve, reject) =>
    sender.send(encodeOsc(`/avatar/parameters/${group}_${suffix}`, value), settings.osc.listenPort,
      '127.0.0.1', error => error ? reject(error) : resolve()));
  const action = async (group = 'PiCtrl') => {
    await osc('Intensity', 80, group); await osc('Duration', 100, group);
    await osc('Action', 0, group); await osc('Action', 2, group);
  };
  try {
    await action();
    await waitFor(() => commands.length === 1, 'selected self-test device');
    assert.deepEqual(commands[0].shocks[0], { id: settings.shockers[1].uuid, type: 'Vibrate',
      intensity: 75, duration: 3000, exclusive: false });
    await osc('Action', 2); await action();
    await waitFor(() => messages.some(line => line.includes('cooldown')), 'self-test cooldown');
    await action('PiCtrl_2');
    await new Promise(resolve => setTimeout(resolve, 130));
    assert.equal(commands.length, 1, 'normal groups and repeated actions add no commands');
    logs.push({ id: 'self-test-log', shockerId: settings.shockers[1].uuid, type: 'Vibrate',
      intensity: 75, duration: 3000, createdOn: new Date().toISOString() });
    await bridge.poll();
    await waitFor(() => feedbackMessages.some(m => m.address.endsWith('PiCtrl_2_FeedbackActive') && m.args[0] === true), 'API feedback');
    for (const message of feedbackMessages)
      await new Promise(resolve => sender.send(encodeOsc(message.address, message.args[0]), settings.osc.listenPort, '127.0.0.1', resolve));
    await new Promise(resolve => setTimeout(resolve, 130));
    assert.equal(commands.length, 1, 'API feedback does not become another action');
    const updated = structuredClone(settings);
    updated.shockers[1].paused = true;
    bridge.configure(updated); await action();
    await waitFor(() => messages.some(line => line.includes('paused in overlay')), 'self-test pause');
    assert.equal(commands.length, 1);
    updated.shockers.forEach(s => { s.paused = false; s.cooldownMs = 0; });
    updated.selfTestShockerKeys = ['left', 'right']; updated.selfTestAvatarGroup = 'OwnTest';
    bridge.configure(updated); await action('OwnTest');
    await waitFor(() => commands.length === 3, 'multiple self-test devices');
    assert.deepEqual(commands.slice(1).map(c => c.shocks[0].id).sort(), updated.shockers.map(s => s.uuid).sort());
    assert.equal(commands.find((c, i) => i > 0 && c.shocks[0].id === settings.shockers[0].uuid).shocks[0].intensity, 40);
    updated.selfTestEnabled = false;
    bridge.configure(updated); await action();
    await waitFor(() => commands.length === 4, 'normal routing restored');
    assert.equal(commands[3].shocks[0].id, settings.shockers[0].uuid);
  } finally {
    bridge(); sender.close(); feedback.close();
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});

test('self test retains dry run and cancels pending actions when the selected route changes', async () => {
  const settings = multipleShockers();
  Object.assign(settings, { selfTestEnabled: true, selfTestShockerKeys: ['right'] });
  settings.osc.listenPort = await freeUdpPort();
  const messages = [];
  const bridge = await startBridge(settings, '', { log: line => messages.push(line) });
  const sender = dgram.createSocket('udp4');
  const osc = (suffix, value) => new Promise((resolve, reject) => sender.send(
    encodeOsc(`/avatar/parameters/PiCtrl_${suffix}`, value), settings.osc.listenPort,
    '127.0.0.1', error => error ? reject(error) : resolve()));
  try {
    await osc('Intensity', 80); await osc('Duration', 100);
    await osc('Action', 0); await osc('Action', 1);
    await new Promise(resolve => setTimeout(resolve, 30));
    const changed = structuredClone(settings); changed.selfTestShockerKeys = ['left'];
    bridge.configure(changed);
    await new Promise(resolve => setTimeout(resolve, 150));
    assert(!messages.some(line => line.includes('TEST ONLY')), 'pending action is cancelled on route change');
    await osc('Action', 1);
    await new Promise(resolve => setTimeout(resolve, 130));
    assert(!messages.some(line => line.includes('TEST ONLY')), 'new route must observe idle');
    await osc('Intensity', 80); await osc('Duration', 100);
    await osc('Action', 0); await osc('Action', 1);
    await waitFor(() => messages.some(line => line.includes('[Left] TEST ONLY')), 'self-test dry run');
    assert(messages.some(line => line.includes('Self-test OSC \u2192 test log')));
    assert(!messages.some(line => line.includes('[Right] TEST ONLY')));
    assert(!messages.some(line => line.includes('] Sent ')));
  } finally { bridge(); sender.close(); }
});

test('packaged bridge receives overlay button and configuration messages through stdin', async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'shockwave-bridge-test-'));
  const configFile = path.join(directory, 'config.json');
  const settings = multipleShockers();
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort() };
  settings.shockers[0].cooldownMs = 0;
  await fs.writeFile(configFile, JSON.stringify(settings));
  const child = spawn(process.execPath, [fileURLToPath(new URL('./bridge.mjs', import.meta.url)), configFile],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, OPENSHOCK_TOKEN: '' } });
  let output = '';
  child.stdout.on('data', data => { output += data; });
  child.stderr.on('data', data => { output += data; });
  const exited = once(child, 'exit');
  try {
    await waitFor(() => output.includes('Listening for VRChat OSC'), 'CLI bridge startup');
    child.stdin.write(JSON.stringify({ kind: 'control', key: 'left', type: 'Sound' }) + '\n');
    await waitFor(() => output.includes('TEST ONLY: Sound 40% for 2000 ms'), 'manual button message');
    settings.shockers[0].paused = true;
    child.stdin.write(JSON.stringify({ kind: 'configure', config: settings }) + '\n');
    child.stdin.write(JSON.stringify({ kind: 'control', key: 'left', type: 'Shock' }) + '\n');
    await waitFor(() => output.includes('Ignored action: paused in overlay'), 'pause configuration message');
    assert(!output.includes('TEST ONLY: Shock'));
  } finally {
    child.kill();
    await exited;
    await fs.unlink(configFile);
    await fs.rmdir(directory);
  }
});

test('saving a UUID connects immediately when the bridge started without one', async () => {
  const reads = [];
  const server = http.createServer((request, response) => {
    reads.push(request.url);
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ data: [] }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const settings = multipleShockers();
  const originalId = settings.shockers[0].uuid;
  settings.shockers = [settings.shockers[0]];
  settings.shockers[0].uuid = '';
  settings.apiBaseUrl = `http://127.0.0.1:${server.address().port}`;
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort() };
  settings.pollIntervalMs = 60000;
  const messages = [];
  const bridge = await startBridge(settings, 'local-test-token', { log: message => messages.push(message) });
  try {
    assert.equal(reads.length, 0);
    assert(messages.some(line => line.includes('API setup: shocker UUID missing')));
    settings.shockers[0].uuid = originalId;
    bridge.configure(settings);
    await waitFor(() => messages.some(line => line.includes('OpenShock logs connected')), 'immediate UUID connection');
    assert.equal(reads.length, 1);
    assert(reads[0].includes(originalId));
  } finally {
    bridge();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});

test('API failures explain rejected tokens, missing permissions, and missing UUIDs', async () => {
  let code = 401;
  const server = http.createServer((request, response) => {
    response.statusCode = code;
    response.end();
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const settings = { ...multipleShockers(), apiBaseUrl: `http://127.0.0.1:${server.address().port}` };
  try {
    await assert.rejects(() => apiRequest(settings, 'GET', '/test', undefined, 'test-token'), /401: API token rejected/);
    code = 403;
    await assert.rejects(() => apiRequest(settings, 'GET', '/test', undefined, 'test-token'), /403: token lacks permission/);
    code = 404;
    await assert.rejects(() => apiRequest(settings, 'GET', '/test', undefined, 'test-token'), /404: shocker UUID was not found/);
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});

test('two shockers retain separate API errors while healthy polls succeed, with backoff and recovery', async () => {
  let now = 0;
  let rightCode = 503;
  let rightReads = 0;
  const messages = [];
  const server = http.createServer((request, response) => {
    if (request.url.includes('22222222-')) { rightReads++; response.statusCode = rightCode; }
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ data: [] }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const settings = multipleShockers();
  settings.pollIntervalMs = 500;
  settings.apiBaseUrl = `http://127.0.0.1:${server.address().port}`;
  settings.osc.listenPort = await freeUdpPort();
  const bridge = await startBridge(settings, 'local-test-token', { now: () => now, log: line => messages.push(line) });
  const latest = () => JSON.parse(messages.filter(line => line.startsWith('@api ')).at(-1).slice(5));
  try {
    assert.deepEqual(latest().shockers.map(s => s.state), ['Connected', 'Error']);
    assert.equal(latest().shockers[1].statusCode, 503);
    await bridge.poll();
    assert.deepEqual(latest().shockers.map(s => s.state), ['Connected', 'Error'], 'healthy poll must not erase the other error');
    assert.equal(rightReads, 1, 'failed target backs off');
    now = 2000;
    await bridge.poll();
    assert.equal(rightReads, 2);
    assert.equal(messages.filter(line => line.includes('log poll failed')).length, 1, 'unchanged errors do not flood the log');
    now = 5999;
    await bridge.poll();
    assert.equal(rightReads, 2, 'second failure doubles the delay');
    rightCode = 200;
    now = 6000;
    await bridge.poll();
    assert.deepEqual(latest().shockers.map(s => s.state), ['Connected', 'Connected']);
    assert.equal(rightReads, 3);
    assert(messages.some(line => line.includes('[Right] OpenShock logs connected')));
  } finally {
    bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});

test('HTTP 429 pauses all log polling for Retry-After, then recovers without retrying commands', async () => {
  let now = 0;
  let limited = true;
  let reads = 0;
  let commands = 0;
  const messages = [];
  const server = http.createServer((request, response) => {
    if (request.method === 'POST') { commands++; response.statusCode = 503; response.end(); return; }
    reads++;
    if (limited) { response.statusCode = 429; response.setHeader('Retry-After', '7'); response.end(); return; }
    response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify({ data: [] }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const settings = multipleShockers();
  settings.dryRun = false;
  settings.pollIntervalMs = 500;
  settings.apiBaseUrl = `http://127.0.0.1:${server.address().port}`;
  settings.osc.listenPort = await freeUdpPort();
  const bridge = await startBridge(settings, 'local-test-token', { now: () => now, log: line => messages.push(line) });
  const latest = () => JSON.parse(messages.filter(line => line.startsWith('@api ')).at(-1).slice(5));
  try {
    assert.equal(latest().retryAt, 7000);
    const initialReads = reads;
    now = 6999;
    await bridge.poll();
    assert.equal(reads, initialReads, 'all targets honor the shared retry deadline');
    limited = false;
    now = 7000;
    await bridge.poll();
    assert(latest().shockers.every(s => s.state === 'Connected'));
    assert.equal(await bridge.control('right', 'Vibrate'), false);
    assert.equal(commands, 1, 'a failed device command is never retried automatically');
    await bridge.poll();
    assert.equal(latest().shockers[1].state, 'Error', 'successful log checks do not hide a failed command');
    assert.equal(commands, 1);
    settings.shockers = [settings.shockers[0]];
    bridge.configure(settings);
    assert.deepEqual(latest().shockers.map(s => s.key), ['left'], 'removing a target also removes its error');
  } finally {
    bridge(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});

test('live log events route by UUID, skip duplicate events, and reject malformed events', () => {
  const entry = { shocker: { id: '22222222-2222-2222-2222-222222222222' }, type: 2,
    intensity: 32, duration: 2300, executedAt: '2026-10-07T15:00:00.1234567Z' };
  const result = feedbackFromLiveLog(entry);
  assert.equal(result.shockerId, entry.shocker.id);
  assert.equal(result.event.type, 'Vibrate');
  assert.equal(result.event.intensity, 32);
  assert.equal(feedbackFromLiveLog({ ...entry, type: 'Sound' }).event.type, 'Sound');
  assert.equal(feedbackFromLiveLog({ ...entry, type: 99 }), null);
  assert.equal(feedbackFromLiveLog({ ...entry, executedAt: null }), null);
});

test('live connection replaces history polling, survives slider saves, delivers feedback, and reconnects', async () => {
  const reads = [];
  const commands = [];
  const messages = [];
  let socket;
  let connections = 0;
  let headerToken;
  const server = http.createServer(async (request, response) => {
    if (request.method === 'POST') {
      let body = ''; for await (const data of request) body += data;
      commands.push(JSON.parse(body));
    } else reads.push(request.url);
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ data: { id: 'verified-shocker' } }));
  });
  const wsServer = new WebSocket.Server({ server, path: '/1/hubs/user' });
  wsServer.on('connection', (client, request) => {
    socket = client; connections++;
    headerToken = request.headers.openshocktoken;
    client.on('message', data => {
      for (const part of data.toString().split('\u001e').filter(Boolean)) {
        const message = JSON.parse(part);
        if (message.protocol === 'json') client.send('{}\u001e');
      }
    });
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const feedback = dgram.createSocket('udp4');
  feedback.bind(0, '127.0.0.1'); await once(feedback, 'listening');
  const oscMessages = [];
  feedback.on('message', packet => oscMessages.push(...decodeOsc(packet)));
  const settings = multipleShockers();
  settings.dryRun = false;
  settings.pollIntervalMs = 500;
  settings.apiBaseUrl = `http://127.0.0.1:${server.address().port}`;
  settings.osc = { ...settings.osc, listenPort: await freeUdpPort(), sendPort: feedback.address().port };
  const bridge = await startBridge(settings, 'local-test-token', { liveEvents: true, log: line => messages.push(line) });
  const latest = () => JSON.parse(messages.filter(line => line.startsWith('@api ')).at(-1).slice(5));
  const sendLog = entry => socket.send(JSON.stringify({ type: 1, target: 'Log', arguments: [{ name: 'Test user' }, [entry]] }) + '\u001e');
  try {
    await waitFor(() => latest().shockers.every(s => s.state === 'Connected'), 'live API connection');
    assert.equal(headerToken, 'local-test-token', 'API token travels in a header');
    assert.equal(reads.length, 2, 'each UUID is checked once');
    assert(reads.every(url => !url.includes('/logs')), 'there are no history polling requests');
    for (let i = 0; i < 50; i++) {
      const next = structuredClone(settings);
      next.shockers[1].maxIntensity = 40 + i;
      next.shockers[1].maxDurationMs = 1000 + i * 100;
      bridge.configure(next);
    }
    await bridge.poll();
    assert.equal(reads.length, 2, 'slider saves and connection ticks add no reads');
    assert.equal(commands.length, 0, 'sliders never activate a device');
    const entry = { shocker: { id: settings.shockers[1].uuid }, type: 'Vibrate', intensity: 32,
      duration: 2300, executedAt: new Date().toISOString() };
    sendLog(entry);
    await waitFor(() => messages.some(line => line.includes('[Right] API log')), 'live shocker log');
    await waitFor(() => oscMessages.some(m => m.address.endsWith('PiCtrl_2_FeedbackActive') && m.args[0] === true), 'live VRChat feedback');
    sendLog(entry);
    sendLog({ ...entry, shocker: { id: '33333333-3333-3333-3333-333333333333' } });
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(messages.filter(line => line.includes('API log → VRChat')).length, 1, 'duplicates and unrelated UUIDs are ignored');
    assert.equal(await bridge.control('right', 'Vibrate'), true);
    assert.equal(commands.length, 1);
    assert.equal(commands[0].shocks[0].id, settings.shockers[1].uuid);
    socket.close(1011, 'local reconnection test');
    await waitFor(() => latest().shockers.some(s => s.state === 'Checking'), 'stream disconnected state');
    await waitFor(() => connections === 2 && latest().shockers.every(s => s.state === 'Connected'), 'stream reconnect', 7000);
    await waitFor(() => reads.length === 4, 'one metadata refresh per card after reconnect');
    assert(reads.every(url => !url.includes('/logs')), 'reconnecting refreshes names without history polling');
    assert.equal(commands.length, 1, 'reconnecting does not resend device commands');
  } finally {
    await bridge(); feedback.close();
    for (const client of wsServer.clients) client.terminate();
    await new Promise(resolve => wsServer.close(resolve));
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
});
