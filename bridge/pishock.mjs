// PiShock cloud integration. Credentials and share codes stay inside this process.
// A control is sent once on an authenticated socket; there is no command queue,
// transport fallback, retry, or recovery replay. Metadata reads may reconnect.
import dgram from 'node:dgram';
import { randomUUID } from 'node:crypto';
import { decodeOsc, encodeOsc } from './osc.mjs';
import { OscActionCapture } from './capture.mjs';

const targetPattern = /^[1-9][0-9]{0,9}\/[1-9][0-9]{0,9}(?:\/[1-9][0-9]{0,9})?$/;
export const validPiShockTarget = value => typeof value === 'string' && targetPattern.test(value) && value.split('/').every(v => Number.isSafeInteger(Number(v)) && Number(v) <= 2147483647);
const positiveId = value => Number.isInteger(value) && value > 0 && value <= 2147483647;
const modes = { Shock: 's', Vibrate: 'v', Sound: 'b' };
const safeName = value => typeof value === 'string' ? value.replace(/[\x00-\x1f\x7f]/g, '').slice(0, 64).trim() : '';
const failure = (message, statusCode = 0) => Object.assign(new Error(message), { statusCode });
const defaultUrls = { account: 'https://api.pishock.com', api: 'https://ps.pishock.com', broker: 'wss://broker.pishock.com/v2' };

export function readPiShockDevices(owned, shared) {
  if (!Array.isArray(owned) || !shared || typeof shared !== 'object' || Array.isArray(shared)) throw failure('Unexpected PiShock device list');
  const devices = new Map();
  for (const hub of owned) {
    if (!positiveId(hub?.clientId) || !Array.isArray(hub.shockers)) throw failure('Invalid PiShock hub metadata');
    for (const item of hub.shockers) {
      if (!positiveId(item?.shockerId) || typeof item.isPaused !== 'boolean') throw failure('Invalid PiShock shocker metadata');
      const uuid = `${hub.clientId}/${item.shockerId}`;
      if (devices.has(uuid)) throw failure('Duplicate PiShock target');
      devices.set(uuid, { uuid, clientId: hub.clientId, shockerId: item.shockerId,
        name: safeName(item.name) || 'PiShock shocker', hubName: safeName(hub.name), shared: false,
        paused: item.isPaused, maxIntensity: 100, maxDurationMs: 15000,
        canShock: true, canVibrate: true, canBeep: true, canLog: true });
    }
  }
  for (const items of Object.values(shared)) {
    if (!Array.isArray(items)) throw failure('Invalid PiShock shared devices');
    for (const item of items) {
      if (!positiveId(item?.clientId) || !positiveId(item.shockerId) || !positiveId(item.shareId) ||
          typeof item.isPaused !== 'boolean' || !Number.isInteger(item.maxIntensity) || item.maxIntensity < 0 || item.maxIntensity > 100 ||
          !['canShock', 'canVibrate', 'canBeep', 'canLog'].every(key => typeof item[key] === 'boolean') ||
          typeof item.shareCode !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(item.shareCode)) throw failure('Invalid PiShock share permissions');
      const uuid = `${item.clientId}/${item.shockerId}/${item.shareId}`;
      if (devices.has(uuid)) throw failure('Duplicate PiShock share');
      // Duration is not present in the documented modern share response. If it
      // is returned, retain its tighter cap. The server remains authoritative.
      const duration = item.maxDuration === undefined ? 15000 : Number(item.maxDuration) * 1000;
      if (!Number.isFinite(duration) || duration < 0) throw failure('Invalid PiShock share duration');
      devices.set(uuid, { uuid, clientId: item.clientId, shockerId: item.shockerId, shareId: item.shareId,
        shareCode: item.shareCode, name: safeName(item.shockerName) || 'Shared PiShock shocker', hubName: '', shared: true,
        paused: item.isPaused, maxIntensity: item.maxIntensity, maxDurationMs: Math.min(15000, Math.floor(duration)),
        canShock: item.canShock, canVibrate: item.canVibrate, canBeep: item.canBeep, canLog: item.canLog });
    }
  }
  if (devices.size > 2000) throw failure('Too many PiShock devices');
  return devices;
}

export function piShockPublish(device, command, userId, operationId) {
  if (!device || device.paused || !positiveId(userId) || !modes[command.type] ||
      !device[{ Shock: 'canShock', Vibrate: 'canVibrate', Sound: 'canBeep' }[command.type]]) throw failure('PiShock access is paused or this operation is not permitted', 403);
  const intensity = command.type === 'Sound' ? 0 : Math.min(command.intensity, device.maxIntensity);
  const duration = Math.min(command.duration, device.maxDurationMs, 15000);
  if (!Number.isInteger(intensity) || (command.type !== 'Sound' && intensity < 1) || !Number.isInteger(duration) || duration < 1) throw failure('PiShock limit blocks this operation', 403);
  return { Operation: 'PUBLISH', PublishCommands: [{
    Target: device.shared ? `c${device.clientId}-sops-${device.shareCode}` : `c${device.clientId}-ops`,
    Body: { id: device.shockerId, m: modes[command.type], i: command.type === 'Sound' ? 0 : intensity, d: duration, r: true,
      l: { u: userId, ty: device.shared ? 'sc' : 'api', w: false, h: false, o: `ShockWave ${operationId}` } },
  }] };
}

export async function startPiShockBridge(input, apiKey, options, common) {
  let config = common.validateConfig(input, apiKey);
  const writeLog = options.log ?? (message => console.log(`[${new Date().toLocaleTimeString()}] ${message}`));
  const log = message => {
    if (apiKey) message = message.replaceAll(apiKey, '[redacted]');
    for (const device of devices.values()) if (device.shareCode) message = message.replaceAll(device.shareCode, '[redacted]');
    writeLog(message);
  };
  const now = options.now ?? Date.now;
  const fetcher = options.fetch ?? fetch;
  const Socket = options.WebSocket ?? globalThis.WebSocket;
  const urls = { ...defaultUrls, ...options.piShockUrls }; // dependency injection for loopback fixtures only
  const receiver = dgram.createSocket('udp4'), sender = dgram.createSocket('udp4');
  let stopped = false, socket, authenticated = false, userId = null, accountUsername = null, devices = new Map();
  let inventoryAt = 0, readInFlight = false, readError = null, socketError = null, retryAt = 0, reconnectAt = 0;
  let pingDeadline = 0, nextPingAt = 0, lastStatus = '', unknownMessageReported = false;
  const runtimes = new Map(), pending = new Map(), feedbackTimers = new Set();
  let selfTestCapture;
  const timeoutMs = options.commandTimeoutMs ?? 8000;

  function resetCaptures() {
    selfTestCapture?.close();
    selfTestCapture = new OscActionCapture(action => {
      if (config.selfTestEnabled) for (const key of config.selfTestShockerKeys) void control(key, action, 'self-test OSC');
    });
    for (const [key, runtime] of runtimes) {
      runtime.capture?.close();
      runtime.capture = new OscActionCapture(action => void control(key, action));
    }
  }

  function publishStatus() {
    if (stopped) return;
    const fresh = authenticated && now() - inventoryAt < 90000 && !readError;
    const problem = readError ?? socketError;
    const credentials = apiKey && config.piShockUsername?.trim();
    const targets = [...runtimes.values()].filter(runtime => runtime.settings.uuid);
    const hubs = new Set(targets.map(runtime => devices.get(runtime.settings.uuid)?.clientId).filter(Boolean));
    const status = { kind: 'api-status', provider: 'PiShock', retryAt,
      connection: { provider: 'PiShock', state: !credentials ? 'TokenNeeded' : problem ? 'Error' : fresh ? 'Connected' : 'Checking', statusCode: problem?.statusCode ?? 0,
        detail: problem?.message ?? (!credentials ? 'Enter your PiShock username and API key in Settings.' : fresh ? 'PiShock account connected. Add shockers from your device list.' : 'Connecting to PiShock.') },
      hub: { state: !credentials ? 'TokenNeeded' : !targets.length ? 'UuidNeeded' : 'Unknown', total: hubs.size, online: 0,
        detail: 'PiShock API connection and hub availability are separate. Hub status is unknown until a supported hub report is available; a broker PONG does not confirm hub or shocker power.',
        shockers: targets.map(runtime => ({ key: runtime.settings.key, state: 'Unknown' })) },
      shockers: [...runtimes.values()].map(runtime => {
        const target = devices.get(runtime.settings.uuid), error = runtime.error ?? problem;
        const state = !credentials ? 'TokenNeeded' : !runtime.settings.uuid ? 'UuidNeeded'
          : error ? 'Error' : fresh && !target ? 'Error' : fresh ? 'Connected' : 'Checking';
        return { key: runtime.settings.key, provider: 'PiShock', state, statusCode: error?.statusCode ?? (fresh && !target ? 404 : 0),
          detail: error?.message ?? (fresh && !target ? 'This PiShock target is no longer available to this account. Refresh devices or choose another target.'
            : target?.paused ? 'PiShock reports this shocker/share paused; commands are blocked.'
            : state === 'Connected' ? 'PiShock target verified; commands use your local limits and PiShock permissions.' : 'Waiting for PiShock connection and device access.') };
      }) };
    const serialized = JSON.stringify(status);
    if (serialized !== lastStatus) { lastStatus = serialized; log('@api ' + serialized); }
  }

  function sendOsc(address, value, type = 'auto') {
    if (!stopped) sender.send(encodeOsc(address, value, type), config.osc.sendPort, config.osc.sendHost ?? '127.0.0.1');
  }
  sender.on('error', () => { if (!stopped) log('OSC feedback error'); });
  function feedback(runtime, command) {
    const s = runtime.settings;
    sendOsc(s.feedbackModeAddress, command.type === 'Shock' ? 2 : command.type === 'Vibrate' ? 1 : 3);
    sendOsc(s.feedbackIntensityAddress, command.intensity / 100, 'float');
    sendOsc(s.feedbackEventAddress, true); sendOsc(s.feedbackActiveAddress, true);
    for (const [address, delay] of [[s.feedbackEventAddress, 250], [s.feedbackActiveAddress, command.duration]]) {
      const timer = setTimeout(() => { feedbackTimers.delete(timer); sendOsc(address, false); }, delay);
      feedbackTimers.add(timer);
    }
  }
  function activity(key, id, phase, remainingMs) {
    if (!stopped) log('@activity ' + JSON.stringify({ kind: 'shocker-activity', key, id, phase, remainingMs }));
  }

  async function readJson(base, endpoint, params = [], request = {}) {
    const url = new URL(endpoint, base);
    for (const [key, value] of params) url.searchParams.append(key, String(value));
    const stage = request.stage ?? 'API request';
    let response;
    try { response = await fetcher(url, { redirect: 'error', signal: AbortSignal.timeout(8000), headers: { 'Accept': 'application/json', 'User-Agent': 'ShockWave-Bridge/PiShock', ...request.headers } }); }
    catch { throw failure(`PiShock ${stage} failed or timed out`); }
    if (!response.ok) {
      const reason = stage === 'account lookup' && response.status === 401 ? ' Check that you pasted the API key value from your PiShock account.' : '';
      const error = failure(`PiShock ${stage} failed (HTTP ${response.status}).${reason}`, response.status);
      if (response.status === 429) {
        const value = response.headers.get('retry-after'), seconds = value ? Number(value) : NaN;
        error.retryMs = Number.isFinite(seconds) ? Math.max(1000, seconds * 1000) : Math.max(1000, Date.parse(value) - now()) || 60000;
      }
      throw error;
    }
    try {
      const body = await response.text();
      if (body.length > 2097152) throw new Error();
      return JSON.parse(body);
    } catch { throw failure(`Unexpected PiShock ${stage} response`); }
  }

  async function refreshDevices() {
    if (stopped || readInFlight || now() < retryAt) return;
    if (!apiKey || !config.piShockUsername?.trim()) { publishStatus(); return; }
    readInFlight = true;
    try {
      // The old auth.pishock.com/Auth/GetUserIfAPIKeyValid route is removed.
      // Current official OpenAPI: GET api.pishock.com/Account, API key in the
      // X-PiShock-Api-Key header; response fields are UserId and Username.
      const account = await readJson(urls.account, '/Account', [], { stage: 'account lookup', headers: { 'X-PiShock-Api-Key': apiKey } });
      const accountId = account?.UserId ?? account?.userId;
      const canonicalName = account?.Username ?? account?.username;
      if ((account?.UserId !== undefined && account?.userId !== undefined && account.UserId !== account.userId) ||
          (account?.Username !== undefined && account?.username !== undefined && account.Username !== account.username) ||
          !positiveId(accountId) || typeof canonicalName !== 'string' || !canonicalName.trim() ||
          canonicalName.length > 256 || /[\x00-\x1f\x7f]/.test(canonicalName)) throw failure('Unexpected PiShock account lookup response');
      const credentials = [['UserId', accountId], ['Token', apiKey], ['api', true]];
      const [owned, shares] = await Promise.all([
        readJson(urls.api, '/PiShock/GetUserDevices', credentials, { stage: 'owned-device lookup' }),
        readJson(urls.api, '/PiShock/GetShareCodesByOwner', credentials, { stage: 'shared-device lookup' }),
      ]);
      if (!shares || typeof shares !== 'object' || Array.isArray(shares) || Object.values(shares).some(ids => !Array.isArray(ids) || ids.some(id => !positiveId(id)))) throw failure('Unexpected PiShock share list');
      const ids = [...new Set(Object.values(shares).flat())];
      if (ids.length > 2000) throw failure('Too many PiShock shares');
      const shared = Object.create(null);
      for (let index = 0; index < ids.length; index += 50) {
        const chunk = await readJson(urls.api, '/PiShock/GetShockersByShareIds', [...credentials, ...ids.slice(index, index + 50).map(id => ['shareIds', id])], { stage: 'share-details lookup' });
        if (!chunk || typeof chunk !== 'object' || Array.isArray(chunk)) throw failure('Unexpected PiShock share details');
        for (const [owner, values] of Object.entries(chunk)) {
          if (!Array.isArray(values)) throw failure('Unexpected PiShock share details');
          (shared[owner] ??= []).push(...values);
        }
      }
      const next = readPiShockDevices(owned, shared);
      if (stopped) return;
      if (readError || !inventoryAt || now() - inventoryAt >= 90000 || JSON.stringify([...devices.values()]) !== JSON.stringify([...next.values()])) resetCaptures();
      if (accountUsername !== null && accountUsername !== canonicalName && socket && socket.readyState !== Socket.CLOSED)
        disconnect(socket, 'PiShock account name changed; reconnecting with fresh account information');
      devices = next; userId = accountId; accountUsername = canonicalName; inventoryAt = now(); readError = null; retryAt = 0;
      for (const runtime of runtimes.values()) runtime.error = null;
      // Explicit projection: no share code, API key, username, or raw server response in IPC/logs.
      log('@devices ' + JSON.stringify({ kind: 'pishock-devices', devices: [...devices.values()].map(d => ({ uuid: d.uuid, name: d.name, hubName: d.hubName, shared: d.shared })) }));
      log(`PiShock device list refreshed (${devices.size} available)`);
      if (!socket || socket.readyState === Socket.CLOSED) connect();
      else if (authenticated) subscribe();
    } catch (error) {
      if (!stopped) {
        readError = failure(error.message, error.statusCode); inventoryAt = 0; resetCaptures();
        retryAt = now() + (error.retryMs ?? 60000);
        log(readError.message); log('@devices ' + JSON.stringify({ kind: 'pishock-devices', devices: [] }));
      }
    } finally { readInFlight = false; publishStatus(); }
  }

  function subscribe() {
    if (!authenticated || socket?.readyState !== Socket.OPEN) return;
    const targets = new Set();
    for (const runtime of runtimes.values()) {
      const target = devices.get(runtime.settings.uuid);
      if (!target) continue;
      targets.add(`${target.clientId}-ping`);
      if (target.canLog) targets.add(`${target.clientId}-log`);
    }
    if (targets.size) socket.send(JSON.stringify({ Operation: 'SUBSCRIBE', Targets: [...targets] }));
  }

  function disconnect(current, reason) {
    if (current !== socket) return;
    authenticated = false; pingDeadline = 0; socketError = failure(reason); resetCaptures();
    for (const operation of pending.values()) operation.finish(false, 'uncertain', 'PiShock connection was interrupted; command outcome is unknown.');
    pending.clear();
    reconnectAt = now() + Math.max(config.pollIntervalMs, 5000);
    try { current.close(); } catch { }
    publishStatus();
  }

  function connect() {
    if (stopped || !Socket || readError || !userId || !apiKey || (socket && socket.readyState !== Socket.CLOSED)) return;
    const url = new URL(urls.broker);
    url.searchParams.set('Username', accountUsername); url.searchParams.set('ApiKey', apiKey);
    const current = new Socket(url);
    socket = current; authenticated = false; socketError = null; unknownMessageReported = false;
    pingDeadline = now() + 10000;
    current.addEventListener('open', () => {
      if (stopped || socket !== current) return;
      current.send(JSON.stringify({ Operation: 'PING' })); pingDeadline = now() + 10000;
    });
    current.addEventListener('message', event => {
      if (stopped || socket !== current || typeof event.data !== 'string' || event.data.length > 262144) return;
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (!message || typeof message !== 'object') return;
      if (message.IsError === false && message.Message === 'PONG' && message.OriginalCommand === 'PING') {
        const first = !authenticated;
        authenticated = true; socketError = null; pingDeadline = 0; nextPingAt = now() + 15000;
        if (first) { resetCaptures(); subscribe(); log('PiShock API connected'); }
        publishStatus(); return;
      }
      let original;
      try { original = typeof message.OriginalCommand === 'string' ? JSON.parse(message.OriginalCommand) : message.OriginalCommand; } catch { }
      if (original?.Operation === 'PUBLISH' && Array.isArray(original.PublishCommands) && original.PublishCommands.length === 1) {
        const origin = original.PublishCommands[0]?.Body?.l?.o;
        const operation = pending.get(origin);
        if (!operation || JSON.stringify(original) !== operation.serialized) return;
        if (message.IsError === false && message.ErrorCode === null && message.Message === 'Publish successful.')
          operation.finish(true, 'accepted', 'PiShock accepted the command.');
        else if (message.IsError === true) operation.finish(false, 'rejected', 'PiShock rejected the command. Check device pause, permissions and limits on PiShock.');
        return;
      }
      if (message.IsError === true) {
        // Never print Message/OriginalCommand: they can echo credentials/share codes.
        disconnect(current, 'PiShock broker rejected the connection or subscription. Check your API key and permissions.'); return;
      }
      if (!unknownMessageReported && message.OriginalCommand === undefined) {
        unknownMessageReported = true;
        log('PiShock live message received; unrecognized hub/log format ignored. API availability does not confirm hub power.');
      }
    });
    current.addEventListener('error', () => { if (!stopped) disconnect(current, 'PiShock live connection failed'); });
    current.addEventListener('close', () => { if (!stopped) disconnect(current, 'PiShock live connection closed'); });
    publishStatus();
  }

  async function control(key, action, source = 'OSC') {
    const runtime = runtimes.get(key);
    if (!runtime || stopped) return false;
    const request = common.actionRequest(config, action, key, source === 'manual' || source === 'self-test OSC');
    if (!request) { log(`[${runtime.settings.name}] Ignored action: paused, missing setup, or invalid/zero input`); return false; }
    if (runtime.lastCommandAt !== null && now() - runtime.lastCommandAt < runtime.settings.cooldownMs) { log(`[${runtime.settings.name}] Ignored action: cooldown`); return false; }
    const command = request.shocks[0];
    if (config.dryRun) {
      runtime.lastCommandAt = now();
      log(`[${runtime.settings.name}] TEST ONLY: ${command.type} ${command.intensity}% for ${Math.min(command.duration, 15000)} ms`); return true;
    }
    if (!authenticated || readError || !inventoryAt || now() - inventoryAt >= 90000 || socket?.readyState !== Socket.OPEN || socket.bufferedAmount > 65536) {
      log(`[${runtime.settings.name}] Ignored action: PiShock connection/device access is not ready`); return false;
    }
    if ([...pending.values()].some(operation => operation.key === key) || pending.size >= 16) { log('Ignored action: PiShock command still pending'); return false; }
    const id = randomUUID();
    let publish;
    try { publish = piShockPublish(devices.get(runtime.settings.uuid), command, userId, id); }
    catch (error) { runtime.error = error; publishStatus(); log(`[${runtime.settings.name}] ${error.message}`); return false; }
    const serialized = JSON.stringify(publish), origin = publish.PublishCommands[0].Body.l.o;
    const sent = publish.PublishCommands[0].Body;
    runtime.lastCommandAt = now();
    activity(key, id, 'pending', sent.d + timeoutMs);
    return new Promise(resolve => {
      const timer = setTimeout(() => finish(false, 'uncertain', 'PiShock command acknowledgement timed out; outcome is unknown.'), timeoutMs);
      function finish(success, phase, detail) {
        if (!pending.delete(origin)) return;
        clearTimeout(timer);
        activity(key, id, phase, sent.d);
        if (!stopped && runtimes.get(key) === runtime && runtime.settings.uuid === command.id) {
          runtime.error = success ? null : failure(detail);
          if (success) feedback(runtime, { type: command.type, intensity: sent.i, duration: sent.d });
          log(`[${runtime.settings.name}] ${detail}${success ? ` ${command.type} ${sent.i}% for ${sent.d} ms` : ' No automatic retry.'}`);
          publishStatus();
        }
        resolve(success);
      }
      pending.set(origin, { key, serialized, finish });
      try { socket.send(serialized); }
      catch { finish(false, 'uncertain', 'PiShock command send failed; outcome is unknown.'); }
    });
  }

  function configure(nextInput) {
    const next = common.validateConfig(nextInput, apiKey);
    if (next.provider !== 'PiShock' || next.piShockUsername !== config.piShockUsername || next.dryRun !== config.dryRun ||
        next.pollIntervalMs !== config.pollIntervalMs || JSON.stringify(next.osc) !== JSON.stringify(config.osc)) throw failure('Restart bridge to change connection settings');
    const routeSignature = value => JSON.stringify([value.selfTestEnabled, value.selfTestShockerKeys, value.selfTestAvatarGroup,
      value.shockers.map(s => [s.key, s.uuid, s.paused, s.intensityAddress, s.durationAddress, s.actionAddress])]);
    const changed = routeSignature(next) !== routeSignature(config);
    config = next;
    for (const [key, runtime] of runtimes) if (!config.shockers.some(s => s.key === key)) { runtime.capture.close(); runtimes.delete(key); }
    for (const settings of config.shockers) {
      let runtime = runtimes.get(settings.key);
      if (!runtime) { runtime = { settings, lastCommandAt: null, error: null }; runtimes.set(settings.key, runtime); }
      if (runtime.settings.uuid !== settings.uuid) runtime.error = null;
      runtime.settings = settings;
    }
    if (changed || !selfTestCapture) { resetCaptures(); subscribe(); }
    publishStatus();
  }
  configure(input);

  function receiveOsc(packet) {
    if (stopped) return;
    try {
      for (const { address, args } of decodeOsc(packet)) {
        if (config.selfTestEnabled) {
          const prefix = `/avatar/parameters/${config.selfTestAvatarGroup}_`;
          if (address === prefix + 'Intensity') selfTestCapture.setIntensity(args[0]);
          else if (address === prefix + 'Duration') selfTestCapture.setDuration(args[0]);
          else if (address === prefix + 'Action') selfTestCapture.setAction(args[0]);
        } else for (const runtime of runtimes.values()) {
          const s = runtime.settings;
          if (address === s.intensityAddress) runtime.capture.setIntensity(args[0]);
          else if (address === s.durationAddress) runtime.capture.setDuration(args[0]);
          else if (address === s.actionAddress) runtime.capture.setAction(args[0]);
        }
      }
    } catch { log('Invalid OSC packet ignored'); }
  }
  receiver.on('message', receiveOsc);
  receiver.on('error', () => { if (!stopped) log('OSC listener error'); });
  if (!options.externalOsc) {
    try { await new Promise((resolve, reject) => {
      receiver.once('error', reject);
      receiver.bind(config.osc.listenPort, config.osc.listenHost ?? '127.0.0.1', () => { receiver.off('error', reject); resolve(); });
    }); } catch (error) { selfTestCapture.close(); for (const runtime of runtimes.values()) runtime.capture.close(); receiver.close(); throw error; }
  }
  log(`Listening for VRChat OSC ${options.externalOsc ? 'via overlay on' : 'on'} ${config.osc.listenHost ?? '127.0.0.1'}:${config.osc.listenPort}`);
  log(config.dryRun ? 'Test mode: device commands disabled' : 'Live PiShock control enabled');
  if (!Socket) socketError = failure('PiShock requires the bundled Node.js 22 or newer runtime');
  const interval = setInterval(() => {
    if (stopped) return;
    if (pingDeadline && now() > pingDeadline && socket) disconnect(socket, 'PiShock broker did not answer its connection check');
    if (authenticated && now() >= nextPingAt && !pingDeadline) {
      try { socket.send(JSON.stringify({ Operation: 'PING' })); pingDeadline = now() + 10000; }
      catch { disconnect(socket, 'PiShock connection check failed'); }
    }
    if (now() >= retryAt && now() - inventoryAt >= 60000) void refreshDevices();
    if (!authenticated && now() >= reconnectAt && !readError && Socket) connect();
    publishStatus();
  }, 1000);
  interval.unref();
  await refreshDevices();

  function stop() {
    if (stopped) return;
    stopped = true; clearInterval(interval); resetCaptures(); selfTestCapture.close();
    for (const runtime of runtimes.values()) runtime.capture.close();
    for (const timer of feedbackTimers) clearTimeout(timer);
    for (const operation of pending.values()) operation.finish(false, 'uncertain', 'Bridge stopped');
    try { socket?.close(); } catch { }
    for (const channel of [receiver, sender]) try { channel.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    devices.clear();
  }
  return Object.assign(stop, { configure, refreshDevices, poll: refreshDevices,
    control: (key, type) => {
      const s = runtimes.get(key)?.settings;
      return s ? control(key, { type, intensity: s.manualMaxIntensity, durationMs: s.manualMaxDurationMs }, 'manual') : Promise.resolve(false);
    },
    controlRemote: (key, type, intensity, durationMs, expiresAt) => {
      const settings = runtimes.get(key)?.settings;
      if (!settings || settings.minDurationMs > 15000 || !Number.isInteger(durationMs) || durationMs < 1 || durationMs > 15000 ||
          !Number.isSafeInteger(expiresAt) || expiresAt <= Date.now() || expiresAt > Date.now() + 300) return Promise.resolve(false);
      return control(key, { type, intensity, durationMs }, 'remote');
    },
    controlPanel: (key, type, intensity, durationMs, expiresAt) => {
      const settings = runtimes.get(key)?.settings;
      if (!settings || settings.manualMinDurationMs > 15000 || !Number.isInteger(durationMs) || durationMs < 300 || durationMs > 15000 ||
          !Number.isSafeInteger(expiresAt) || expiresAt <= Date.now() || expiresAt > Date.now() + 300) return Promise.resolve(false);
      return control(key, { type, intensity, durationMs }, 'manual');
    },
    rename: (key, name, requestId) => log('@rename ' + JSON.stringify({ kind: 'shocker-rename', key, name, requestId, success: false, detail: 'PiShock names are saved locally. Manage account names on PiShock.' })),
    applyLimits: (key, requestId) => log('@limits ' + JSON.stringify({ kind: 'share-limits', key, requestId, success: false, detail: 'Local limits apply to this app. Manage PiShock share limits on its website.' })),
    osc: packet => { if (options.externalOsc && Buffer.isBuffer(packet) && packet.length > 0 && packet.length <= 65507) receiveOsc(packet); },
  });
}
