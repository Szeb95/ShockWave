import dgram from 'node:dgram';
import fs from 'node:fs/promises';
import path from 'node:path';
import readline from 'node:readline';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { decodeOsc, encodeOsc } from './osc.mjs';
import { OscActionCapture } from './capture.mjs';
import { createLiveLogs } from './live-logs.mjs';
import { applyShareLimits } from './share-limits.mjs';
import { readShockerName, renameShocker, validShockerName } from './shocker-names.mjs';
import { startPiShockBridge, validPiShockTarget } from './pishock.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const token = process.env.OPENSHOCK_TOKEN?.trim() ?? '';
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const addressFields = ['intensityAddress', 'durationAddress', 'actionAddress',
  'feedbackEventAddress', 'feedbackActiveAddress', 'feedbackIntensityAddress', 'feedbackModeAddress'];
const suffixes = ['Intensity', 'Duration', 'Action', 'FeedbackEvent', 'FeedbackActive', 'FeedbackIntensity', 'FeedbackMode'];
const defaultLog = message => console.log(`[${new Date().toLocaleTimeString()}] ${message}`);

export function normalizeConfig(config) {
  const isPiShock = config.provider === 'PiShock';
  const shockers = (isPiShock ? config.piShockShockers : config.shockers) ?? (isPiShock ? [] : [{
    key: 'legacy-shocker', name: 'My shocker', uuid: config.shockerId ?? '',
    maxIntensity: config.maxIntensity ?? 100, maxDurationMs: config.maxDurationMs ?? 15000,
    cooldownMs: config.minCommandGapMs ?? 1000,
    durationInputMax: config.durationInputMax ?? 100,
    durationAtInputMaxMs: config.durationAtInputMaxMs ?? 15000,
    ...Object.fromEntries(addressFields.map(key => [key, config.osc?.[key]])),
  }]);
  if (!Array.isArray(shockers)) throw new Error('shockers must be an array');
  return { selfTestEnabled: false, selfTestShockerKeys: [], selfTestAvatarGroup: 'PiCtrl',
    ...config, ...(isPiShock ? { selfTestEnabled: config.piShockSelfTestEnabled ?? false, selfTestShockerKeys: config.piShockSelfTestShockerKeys ?? [] } : {}), shockers: shockers.map(item => {
    const shocker = { name: 'My shocker', nameSyncPending: false, model: '', uuid: '', paused: false, minIntensity: 0,
      maxIntensity: 100, minDurationMs: 300, maxDurationMs: 15000, cooldownMs: 1000,
      durationInputMax: 100, durationAtInputMaxMs: 15000, avatarGroup: 'PiCtrl', ...item };
    return { ...shocker,
      manualMinIntensity: shocker.manualMinIntensity ?? shocker.minIntensity,
      manualMaxIntensity: shocker.manualMaxIntensity ?? shocker.maxIntensity,
      manualMinDurationMs: shocker.manualMinDurationMs ?? shocker.minDurationMs,
      manualMaxDurationMs: shocker.manualMaxDurationMs ?? shocker.maxDurationMs,
      ...Object.fromEntries(addressFields.map((key, index) =>
      [key, shocker[key] ?? `/avatar/parameters/${shocker.avatarGroup}_${suffixes[index]}`])) };
  }) };
}

export function validateConfig(input, apiToken = token) {
  const config = normalizeConfig(input);
  if (config.provider !== undefined && !['OpenShock', 'PiShock'].includes(config.provider)) throw new Error('Unknown shocker provider');
  if (config.provider === 'PiShock' && (typeof config.piShockUsername !== 'string' || config.piShockUsername.length > 256 || /[\x00-\x1f]/.test(config.piShockUsername))) throw new Error('Enter a valid PiShock username');
  const o = config.osc;
  if (!o || ![o.listenPort, o.sendPort].every(value => Number.isInteger(value) && value >= 1 && value <= 65535))
    throw new Error('OSC ports must be 1–65535');
  if (typeof config.dryRun !== 'boolean') throw new Error('dryRun must be true or false');
  if (typeof config.selfTestEnabled !== 'boolean') throw new Error('selfTestEnabled must be true or false');
  if (typeof config.selfTestAvatarGroup !== 'string' || !/^[A-Za-z0-9_]+$/.test(config.selfTestAvatarGroup))
    throw new Error('Invalid VRChat test group');
  if (!Array.isArray(config.selfTestShockerKeys) || config.selfTestShockerKeys.some(key => typeof key !== 'string'))
    throw new Error('Choose existing devices for the self test');
  if (!Number.isInteger(config.pollIntervalMs) || config.pollIntervalMs < 500)
    throw new Error('pollIntervalMs must be at least 500');
  const base = new URL(config.apiBaseUrl);
  if (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(base.hostname)))
    throw new Error('apiBaseUrl must use HTTPS, except for a local test server');
  if (!config.dryRun && config.shockers.some(s => s.uuid) && !apiToken) throw new Error(config.provider === 'PiShock' ? 'Live PiShock control requires PISHOCK_API_KEY' : 'Live OpenShock control requires OPENSHOCK_TOKEN');
  const keys = new Set();
  const ids = new Set();
  for (const s of config.shockers) {
    if (typeof s.key !== 'string' || !s.key || keys.has(s.key)) throw new Error('Each shocker needs a unique key');
    keys.add(s.key);
    if (typeof s.name !== 'string' || !s.name.trim()) throw new Error('Enter a toy name');
    if (typeof s.nameSyncPending !== 'boolean' || (s.nameSyncPending && !validShockerName(s.name)))
      throw new Error('Synced name must contain 1–64 characters without line breaks');
    if (typeof s.paused !== 'boolean') throw new Error('paused must be true or false');
    if (typeof s.uuid !== 'string' || (s.uuid && !(config.provider === 'PiShock' ? validPiShockTarget(s.uuid) : uuidPattern.test(s.uuid)))) throw new Error('Invalid shocker identifier');
    const identity = config.provider === 'PiShock' ? s.uuid.split('/').slice(0, 2).join('/') : s.uuid.toLowerCase();
    if (s.uuid && ids.has(identity)) throw new Error('Duplicate shocker UUID/target');
    if (s.uuid) ids.add(identity);
    if (![s.minIntensity, s.maxIntensity].every(Number.isInteger) || s.minIntensity < 0 || s.maxIntensity > 100 || s.minIntensity > s.maxIntensity)
      throw new Error('Intensity range must be within 0–100');
    if (![s.minDurationMs, s.maxDurationMs].every(Number.isInteger) || s.minDurationMs < 300 || s.maxDurationMs > 65535 || s.minDurationMs > s.maxDurationMs)
      throw new Error('Duration range must be within 300–65535 ms');
    if (![s.manualMinIntensity, s.manualMaxIntensity].every(Number.isInteger) || s.manualMinIntensity < 0 || s.manualMaxIntensity > 100 || s.manualMinIntensity > s.manualMaxIntensity)
      throw new Error('Manual intensity range must be within 0–100');
    if (![s.manualMinDurationMs, s.manualMaxDurationMs].every(Number.isInteger) || s.manualMinDurationMs < 300 || s.manualMaxDurationMs > 65535 || s.manualMinDurationMs > s.manualMaxDurationMs)
      throw new Error('Manual duration range must be within 300–65535 ms');
    if (!Number.isInteger(s.cooldownMs) || s.cooldownMs < 0 || s.cooldownMs > 60000) throw new Error('Invalid cooldown');
    if (!Number.isInteger(s.durationInputMax) || s.durationInputMax < 1 || s.durationInputMax > 100 ||
        !Number.isInteger(s.durationAtInputMaxMs) || s.durationAtInputMaxMs < 300 || s.durationAtInputMaxMs > 65535)
      throw new Error('Invalid avatar duration mapping');
    if (typeof s.avatarGroup !== 'string' || !/^[A-Za-z0-9_]+$/.test(s.avatarGroup)) throw new Error('Invalid avatar group');
    for (const key of addressFields) if (typeof s[key] !== 'string' || !s[key].startsWith('/')) throw new Error(`Invalid ${key}`);
  }
  if (new Set(config.selfTestShockerKeys).size !== config.selfTestShockerKeys.length ||
      config.selfTestShockerKeys.some(key => !keys.has(key))) throw new Error('Choose existing devices for the self test');
  if (config.selfTestEnabled && config.selfTestShockerKeys.length === 0)
    throw new Error('Select at least one of your own test devices under Debug');
  if (config.selfTestEnabled && !config.dryRun &&
      config.shockers.some(s => config.selfTestShockerKeys.includes(s.key) && !s.uuid))
    throw new Error('Each selected test device needs its own UUID');
  return config;
}

export function actionRequest(input, action, shockerKey, ownerControl = false) {
  const config = normalizeConfig(input);
  const s = shockerKey === undefined ? config.shockers[0] : config.shockers.find(item => item.key === shockerKey);
  if (!s || s.paused || (!config.dryRun && !s.uuid) || !['Shock', 'Vibrate', 'Sound'].includes(action.type) ||
      !Number.isInteger(action.intensity) || action.intensity < 1 || action.intensity > 100) return null;
  let duration;
  if (action.durationMs !== undefined) {
    if (!Number.isInteger(action.durationMs) || action.durationMs < 1 || action.durationMs > 65535) return null;
    duration = action.durationMs;
  } else {
    if (!Number.isInteger(action.duration) || action.duration < 1 || action.duration > s.durationInputMax) return null;
    duration = Math.round(action.duration / s.durationInputMax * s.durationAtInputMaxMs);
  }
  const minIntensity = ownerControl ? s.manualMinIntensity : s.minIntensity;
  const maxIntensity = ownerControl ? s.manualMaxIntensity : s.maxIntensity;
  const minDuration = ownerControl ? s.manualMinDurationMs : s.minDurationMs;
  const maxDuration = ownerControl ? s.manualMaxDurationMs : s.maxDurationMs;
  const intensity = Math.max(minIntensity, Math.min(action.intensity, maxIntensity));
  if (intensity === 0) return null;
  return { shocks: [{ id: s.uuid, type: action.type, intensity,
    duration: Math.max(minDuration, Math.min(duration, maxDuration)), exclusive: false }],
    customName: 'PiCtrl OSC Bridge' };
}

export async function apiRequest(config, method, endpoint, body, apiToken = token) {
  const response = await fetch(new URL(endpoint, config.apiBaseUrl), {
    method, headers: { 'User-Agent': 'ShockWave-Bridge/0.2', 'OpenShockToken': apiToken,
      ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(8000),
  });
  if (!response.ok) {
    const detail = response.status === 401 ? 'API token rejected; enter the generated token value'
      : response.status === 403 ? 'token lacks permission for this shocker'
      : response.status === 404 ? 'shocker UUID was not found or is not accessible'
      : response.status === 429 ? 'too many API requests; connection checks will wait before retrying'
      : response.status >= 500 ? 'OpenShock server error' : response.statusText;
    const error = new Error(`OpenShock HTTP ${response.status}: ${detail}`);
    error.statusCode = response.status;
    if (response.status === 429) {
      const retry = response.headers.get('retry-after');
      const seconds = retry?.trim() ? Number(retry) : NaN;
      error.retryAfterMs = Number.isFinite(seconds) ? Math.max(1000, seconds * 1000)
        : Math.max(1000, Date.parse(retry ?? '') - Date.now()) || 60000;
    }
    throw error;
  }
  if (response.status === 204) return null;
  return (response.headers.get('content-type') ?? '').includes('json') ? response.json() : response.text();
}

function errorDetail(error) {
  if (error.name === 'TimeoutError') return 'API request timed out after 8 seconds';
  if (error.message === 'fetch failed') {
    const cause = error.cause;
    const reason = cause?.code ?? cause?.errors?.map(item => item.code).filter(Boolean).join(', ');
    return 'Could not reach OpenShock' + (reason ? ` (${reason})` : ': network connection failed');
  }
  return error.message;
}

export function feedbackFromLog(entry, shockerId) {
  if (!entry || typeof entry !== 'object') return null;
  const id = String(entry.shockerId ?? entry.shocker?.id ?? '').toLowerCase();
  if (id && id !== shockerId.toLowerCase()) return null;
  if (!['Shock', 'Vibrate', 'Sound', 'Stop'].includes(entry.type)) return null;
  return { id: String(entry.id ?? ''), type: entry.type, intensity: Number(entry.intensity) || 0,
    durationMs: Number(entry.duration) || 0, createdOn: entry.createdOn };
}

export function feedbackFromLiveLog(entry) {
  const shockerId = String(entry?.shocker?.id ?? entry?.shockerId ?? '').toLowerCase();
  const type = typeof entry?.type === 'number' ? ['Stop', 'Shock', 'Vibrate', 'Sound'][entry.type]
    : ['Stop', 'Shock', 'Vibrate', 'Sound'].find(type => type.toLowerCase() === String(entry?.type).toLowerCase());
  const time = entry?.executedAt ?? entry?.createdOn;
  if (!uuidPattern.test(shockerId) || !type || !Number.isFinite(Date.parse(time))) return null;
  const event = feedbackFromLog({ ...entry, type, shockerId, createdOn: time,
    id: `${shockerId}|${time}|${type}|${entry.intensity}|${entry.duration}` }, shockerId);
  return { shockerId, event };
}

export async function startBridge(input, apiToken = token, options = {}) {
  if (input.provider === 'PiShock') return startPiShockBridge(input, apiToken, options, { validateConfig, actionRequest });
  let config = validateConfig(input, apiToken);
  const log = options.log ?? defaultLog;
  const receiver = dgram.createSocket('udp4');
  const sender = dgram.createSocket('udp4');
  const runtimes = new Map();
  let stopped = false;
  let listening = false;
  let poller;
  let retryAt = 0;
  let lastStatus = '';
  const clock = options.now ?? Date.now;
  const useLiveEvents = options.liveEvents !== false;
  let liveConnected = !useLiveEvents;
  let liveDetail = 'Connecting to live OpenShock logs';
  let liveLogs;
  const hubStates = new Map();
  let hubSnapshotReceived = false;
  let lastHubState = '';
  const shareUpdates = new Set();
  const nameUpdates = new Set();

  function observeName(runtime, response) {
    const s = runtime.settings;
    const metadata = readShockerName(response, s.uuid);
    if (!metadata) return;
    const hubChanged = runtime.deviceId !== metadata.device;
    runtime.deviceId = metadata.device;
    if (hubChanged) publishStatus();
    if (s.nameSyncPending || runtime.sentName === metadata.name) return;
    runtime.sentName = metadata.name;
    log('@name ' + JSON.stringify({ kind: 'shocker-name', key: s.key, uuid: s.uuid, name: metadata.name }));
  }

  function scheduleNameRefresh(runtime) {
    if (stopped || !apiToken || !runtime.settings.uuid) return;
    if (runtime.nameReadInFlight) { runtime.nameReadAgain = true; return; }
    clearTimeout(runtime.nameReadTimer);
    runtime.nameReadTimer = setTimeout(() => void refreshName(runtime), 650);
    runtime.nameReadTimer.unref();
  }

  async function refreshName(runtime) {
    const s = runtime.settings;
    const generation = runtime.generation;
    if (stopped || runtimes.get(s.key) !== runtime || nameUpdates.has(s.key) ||
        clock() < Math.max(retryAt, runtime.nextNameReadAt ?? 0)) return;
    runtime.nameReadInFlight = true;
    try {
      const response = await apiRequest(config, 'GET', `/1/shockers/${s.uuid}`, undefined, apiToken);
      if (!stopped && runtimes.get(s.key) === runtime && runtime.generation === generation) observeName(runtime, response);
      runtime.nextNameReadAt = 0;
    } catch (error) {
      runtime.nameReadAgain = false;
      runtime.nextNameReadAt = clock() + (error.statusCode === 429 ? error.retryAfterMs : 60000);
      if (!stopped) log(`[${s.name}] OpenShock name refresh failed: ${errorDetail(error)}. No automatic name refresh retry.`);
    } finally {
      runtime.nameReadInFlight = false;
      if (runtime.nameReadAgain) { runtime.nameReadAgain = false; scheduleNameRefresh(runtime); }
    }
  }

  async function rename(key, name, requestId) {
    if (stopped) return;
    const runtime = runtimes.get(key);
    const s = runtime?.settings;
    const report = (success, detail) => {
      if (!stopped) log('@rename ' + JSON.stringify({ kind: 'shocker-rename', key, uuid: s?.uuid, name, requestId, success, detail }));
    };
    if (!s || typeof requestId !== 'string' || !uuidPattern.test(requestId)) return;
    if (config.dryRun || !apiToken || !s.uuid) { report(false, 'Name saved locally; a live bridge, token and UUID are needed.'); return; }
    if (nameUpdates.has(key)) { report(false, 'Another name update is still running.'); return; }
    const generation = runtime.generation;
    const current = () => !stopped && runtimes.get(key) === runtime && runtime.generation === generation && runtime.settings.name === name;
    nameUpdates.add(key);
    try {
      const result = await renameShocker(s.uuid, name, (method, endpoint, body) => apiRequest(config, method, endpoint, body, apiToken), current);
      if (current()) { runtime.deviceId = result.device; runtime.sentName = name; publishStatus(); }
      report(true, 'Name saved to OpenShock.');
      if (!stopped) log(`[${name}] Name saved to OpenShock`);
    } catch (error) {
      const detail = error.statusCode === 403 ? 'Name sync needs an owner token with Shockers Edit permission.'
        : error.statusCode === 404 ? 'Name sync target is unavailable or not owned by this account.' : errorDetail(error);
      report(false, detail);
      if (!stopped) log(`[${s.name}] OpenShock name sync failed: ${detail}`);
    } finally { nameUpdates.delete(key); }
  }

  async function applyLimits(key, requestId) {
    const runtime = runtimes.get(key);
    const settings = runtime?.settings;
    const report = (success, detail) => {
      if (!stopped) log('@limits ' + JSON.stringify({ kind: 'share-limits', key, requestId, success, detail }));
    };
    if (!settings || typeof requestId !== 'string' || !uuidPattern.test(requestId)) return;
    if (config.dryRun || !apiToken || !settings.uuid) {
      report(false, 'Not applied: requires a live bridge, token and shocker UUID.'); return;
    }
    if (shareUpdates.has(key)) { report(false, 'Another Apply is still running.'); return; }
    shareUpdates.add(key);
    try {
      const result = await applyShareLimits(settings, (method, endpoint, body) => {
        if (stopped || runtimes.get(key) !== runtime || runtime.settings.uuid !== settings.uuid ||
            runtime.settings.maxIntensity !== settings.maxIntensity || runtime.settings.maxDurationMs !== settings.maxDurationMs ||
            runtime.settings.cooldownMs !== settings.cooldownMs)
          throw new Error('Limits changed or bridge stopped; apply again');
        return apiRequest(config, method, endpoint, body, apiToken);
      });
      const detail = result.total === 0 ? 'No existing OpenShock shares found. Reapply after sharing.'
        : `Applied to ${result.applied} existing shares. Reapply after adding shares.`;
      report(true, detail);
      if (!stopped) log(`[${settings.name}] OpenShock sharing limits: ${detail}`);
    } catch (error) {
      const partial = error.totalShares ? `${error.appliedShares}/${error.totalShares} shares updated. ` : '';
      const reason = error.statusCode === 403 ? 'Owner token needs Shockers Edit and Publicshares Edit permissions.'
        : error.statusCode === 404 ? 'Share unavailable or this account does not own the shocker.' : errorDetail(error);
      report(false, partial + 'Not fully applied: ' + reason);
      if (!stopped) log(`[${settings.name}] OpenShock share limits failed: ${partial}${reason}`);
    } finally { shareUpdates.delete(key); }
  }
  let selfTestCapture;

  function hubStatus() {
    const targets = [...runtimes.values()].filter(runtime => runtime.settings.uuid);
    const ids = new Set(targets.map(runtime => runtime.deviceId).filter(Boolean));
    const fresh = useLiveEvents && liveConnected && hubSnapshotReceived;
    const unknownTargets = targets.filter(runtime => !runtime.deviceId).length;
    const online = fresh ? [...ids].filter(id => hubStates.get(id) === true).length : 0;
    const total = ids.size;
    const state = !apiToken ? 'TokenNeeded' : !targets.length ? 'UuidNeeded'
      : !useLiveEvents || !liveConnected ? 'Unknown' : !hubSnapshotReceived || unknownTargets ? 'Checking'
      : !total ? 'Unknown' : online === total ? 'Online' : online === 0 ? 'Offline' : 'Mixed';
    return { state, total, online,
      detail: state === 'TokenNeeded' ? 'Enter the API token in Settings to read hub status.'
        : state === 'UuidNeeded' ? 'Add a shocker UUID to find its hub.'
        : !useLiveEvents ? 'Live hub status is unavailable in history-only mode.'
        : !liveConnected ? 'Live connection unavailable; previous hub status is no longer current.'
        : !hubSnapshotReceived ? 'Waiting for the live hub status snapshot.'
        : unknownTargets ? 'Waiting to identify the hub for each shocker.'
        : 'Live OpenShock hub connection status. Online does not confirm shocker power or RF reception.',
      shockers: targets.map(runtime => ({ key: runtime.settings.key,
        state: !fresh || !runtime.deviceId ? 'Unknown' : hubStates.get(runtime.deviceId) === true ? 'Online' : 'Offline' })) };
  }

  function publishStatus() {
    if (stopped) return;
    if (retryAt && (retryAt <= clock() || ![...runtimes.values()].some(runtime => runtime.pollError?.statusCode === 429)))
      retryAt = 0;
    const hub = hubStatus();
    const hubSignature = `${hub.state}|${hub.total}|${hub.online}`;
    if (hubSignature !== lastHubState && ['Online', 'Offline', 'Mixed'].includes(hub.state))
      log(`OpenShock hub status: ${hub.state === 'Mixed' ? `${hub.online}/${hub.total} online` : hub.state}${hub.total > 1 && hub.state !== 'Mixed' ? ` (${hub.total} hubs)` : ''}`);
    lastHubState = hubSignature;
    const status = { kind: 'api-status', retryAt, hub, shockers: [...runtimes.values()].map(runtime => {
      const s = runtime.settings;
      const failure = runtime.commandError ?? runtime.pollError;
      const state = !apiToken ? 'TokenNeeded' : !s.uuid ? 'UuidNeeded'
        : failure ? failure.statusCode === 429 ? 'RateLimited' : 'Error'
        : runtime.apiConnected && liveConnected ? 'Connected' : 'Checking';
      return { key: s.key, state, statusCode: failure?.statusCode ?? 0,
        detail: failure?.detail ?? (state === 'Connected' ? useLiveEvents ? 'Shocker verified; receiving live API logs' : 'API logs connected' : state === 'Checking'
          ? runtime.apiConnected ? liveDetail : 'Checking access to this shocker' : state === 'TokenNeeded' ? 'Enter the generated API token in Settings'
          : 'Save this shocker\'s UUID under Options') };
    }) };
    const serialized = JSON.stringify(status);
    if (serialized !== lastStatus) { lastStatus = serialized; log('@api ' + serialized); }
  }

  function send(address, value, numberType = 'auto') {
    if (stopped) return;
    sender.send(encodeOsc(address, value, numberType), config.osc.sendPort, config.osc.sendHost ?? '127.0.0.1');
  }
  sender.on('error', error => log(`OSC feedback error: ${error.message}`));

  function reportActivity(settings, id, phase, remainingMs) {
    if (!stopped) log('@activity ' + JSON.stringify({ kind: 'shocker-activity', key: settings.key,
      id, phase, remainingMs: Math.max(0, Math.min(73535, Math.ceil(remainingMs))) }));
  }

  async function control(key, action, source = 'OSC') {
    const runtime = runtimes.get(key);
    if (!runtime || stopped) return false;
    const s = runtime.settings;
    if (s.paused) { log(`[${s.name}] Ignored action: paused in overlay`); return false; }
    const request = actionRequest(config, action, key, source === 'manual' || source === 'self-test OSC');
    if (!request) { log(`[${s.name}] Ignored action: missing setup or invalid/zero values`); return false; }
    const now = Date.now();
    if (runtime.lastCommandAt !== null && now - runtime.lastCommandAt < s.cooldownMs) {
      log(`[${s.name}] Ignored action: cooldown`); return false;
    }
    runtime.lastCommandAt = now;
    const command = request.shocks[0];
    if (source === 'self-test OSC')
      log(`[${s.name}] Self-test OSC \u2192 ${config.dryRun ? 'test log' : 'OpenShock'}: ${command.type} ${command.intensity}% for ${command.duration} ms`);
    if (config.dryRun) {
      log(`[${s.name}] TEST ONLY: ${command.type} ${command.intensity}% for ${command.duration} ms`);
      return true;
    }
    const activityId = randomUUID();
    reportActivity(s, activityId, 'pending', command.duration + 8000);
    try {
      await apiRequest(config, 'POST', '/2/shockers/control', request, apiToken);
      reportActivity(s, activityId, 'accepted', command.duration);
      runtime.activityEventAt = Math.max(runtime.activityEventAt ?? 0, clock());
      if (!stopped && runtime.settings.uuid === s.uuid && runtimes.get(key) === runtime) {
        runtime.commandError = null;
        log(`[${s.name}] Sent ${command.type} ${command.intensity}% for ${command.duration} ms`);
        publishStatus();
      }
      return true;
    } catch (error) {
      // A timeout/network failure can occur after acceptance. Keep only a bounded warning; never retry the command.
      reportActivity(s, activityId, error.statusCode >= 400 && error.statusCode < 500 && error.statusCode !== 408
        ? 'rejected' : 'uncertain', command.duration);
      if (!stopped && runtime.settings.uuid === s.uuid && runtimes.get(key) === runtime) {
        const detail = errorDetail(error);
        runtime.commandError = { detail: 'Command failed: ' + detail, statusCode: error.statusCode ?? 0 };
        log(`[${s.name}] OpenShock command failed: ${detail}`);
        publishStatus();
      }
      return false;
    }
  }

  function clearFeedback(runtime) {
    clearTimeout(runtime.activeTimer);
    clearTimeout(runtime.eventTimer);
  }

  function publishFeedback(runtime, event) {
    clearFeedback(runtime);
    const s = runtime.settings;
    const executedAt = Date.parse(event.createdOn);
    if (Number.isFinite(executedAt) && executedAt >= (runtime.activityEventAt ?? 0) && executedAt <= clock() + 2000) {
      runtime.activityEventAt = executedAt;
      if (event.type === 'Stop') reportActivity(s, randomUUID(), 'stop', 0);
      else if (event.intensity > 0 && Number.isFinite(event.durationMs) && event.durationMs > 0 && event.durationMs <= 65535)
        reportActivity(s, randomUUID(), 'feedback', Math.max(0, Math.min(event.durationMs, executedAt + event.durationMs - clock())));
    }
    send(s.feedbackModeAddress, event.type === 'Shock' ? 2 : event.type === 'Vibrate' ? 1 : event.type === 'Sound' ? 3 : 0);
    send(s.feedbackIntensityAddress, Math.max(0, Math.min(1, event.intensity / 100)), 'float');
    send(s.feedbackEventAddress, true);
    runtime.eventTimer = setTimeout(() => send(s.feedbackEventAddress, false), 250);
    const active = event.type !== 'Stop' && event.durationMs > 0;
    send(s.feedbackActiveAddress, active);
    if (active) runtime.activeTimer = setTimeout(() => send(s.feedbackActiveAddress, false), Math.min(event.durationMs, 65535));
    log(`[${s.name}] API log → VRChat: ${event.type} ${event.intensity}% for ${event.durationMs} ms`);
  }

  async function pollLogs(runtime) {
    const s = runtime.settings;
    const generation = runtime.generation;
    if (!apiToken || !s.uuid || stopped || runtime.pollInFlight || (useLiveEvents && runtime.pollInitialized) ||
        clock() < Math.max(retryAt, runtime.nextPollAt)) return;
    runtime.pollInFlight = true;
    try {
      // Verify the target once with the metadata endpoint. Live events then replace history polling.
      const response = await apiRequest(config, 'GET', useLiveEvents ? `/1/shockers/${s.uuid}`
        : `/1/shockers/${s.uuid}/logs?offset=0&limit=30`, undefined, apiToken);
      if (stopped || runtimes.get(s.key) !== runtime || runtime.generation !== generation) return;
      const entries = useLiveEvents ? [] : Array.isArray(response) ? response : response?.data;
      if (!Array.isArray(entries)) throw new Error('Unexpected logs response');
      if (runtime.pollInitialized && !runtime.apiConnected) log(`[${s.name}] OpenShock logs reconnected`);
      runtime.apiConnected = true;
      runtime.pollError = null;
      runtime.pollFailures = 0;
      runtime.nextPollAt = 0;
      if (useLiveEvents) observeName(runtime, response);
      if (runtime.pollInitialized) {
        for (const entry of [...entries].sort((a, b) => Date.parse(a.createdOn ?? 0) - Date.parse(b.createdOn ?? 0))) {
          const event = feedbackFromLog(entry, s.uuid);
          if (event?.id && !runtime.seen.has(event.id)) publishFeedback(runtime, event);
        }
      } else {
        log(useLiveEvents ? `[${s.name}] OpenShock shocker verified`
          : `[${s.name}] OpenShock logs connected; existing history skipped`);
        runtime.pollInitialized = true;
      }
      for (const entry of entries) if (entry.id) runtime.seen.add(String(entry.id));
      while (runtime.seen.size > 500) runtime.seen.delete(runtime.seen.values().next().value);
      publishStatus();
    } catch (error) {
      if (!stopped && runtimes.get(s.key) === runtime && runtime.generation === generation) {
        runtime.apiConnected = false;
        const detail = errorDetail(error);
        const changed = detail !== runtime.pollError?.detail;
        runtime.pollError = { detail, statusCode: error.statusCode ?? 0 };
        runtime.pollFailures++;
        const delay = error.statusCode === 429 ? error.retryAfterMs
          : Math.min(useLiveEvents ? 300000 : 60000,
            Math.max(config.pollIntervalMs, useLiveEvents ? 60000 : 2000) * 2 ** Math.min(runtime.pollFailures - 1, 5));
        runtime.nextPollAt = clock() + delay;
        if (error.statusCode === 429) retryAt = Math.max(retryAt, runtime.nextPollAt);
        if (changed) log(`[${s.name}] OpenShock ${useLiveEvents ? 'shocker check' : 'log poll'} failed: ${detail}. Retrying in ${Math.ceil(delay / 1000)} seconds.`);
        publishStatus();
      }
    } finally { runtime.pollInFlight = false; }
  }

  function configure(input) {
    const next = validateConfig(input, apiToken);
    if ((next.provider ?? 'OpenShock') !== (config.provider ?? 'OpenShock') || next.apiBaseUrl !== config.apiBaseUrl || next.dryRun !== config.dryRun ||
        JSON.stringify(next.osc) !== JSON.stringify(config.osc) || next.pollIntervalMs !== config.pollIntervalMs)
      throw new Error('Restart bridge to change connection or debug settings');
    const routeChanged = next.selfTestEnabled !== config.selfTestEnabled ||
      next.selfTestAvatarGroup !== config.selfTestAvatarGroup ||
      JSON.stringify(next.selfTestShockerKeys) !== JSON.stringify(config.selfTestShockerKeys) ||
      next.selfTestShockerKeys.some(key => next.shockers.find(s => s.key === key)?.uuid !== config.shockers.find(s => s.key === key)?.uuid);
    if (routeChanged) {
      selfTestCapture?.close(); selfTestCapture = null;
      for (const runtime of runtimes.values()) { runtime.capture.close(); runtime.capture = null; }
    }
    config = next;
    for (const [key, runtime] of runtimes) {
      if (!config.shockers.some(s => s.key === key)) {
        runtime.capture?.close(); clearFeedback(runtime); clearTimeout(runtime.nameReadTimer);
        send(runtime.settings.feedbackEventAddress, false);
        send(runtime.settings.feedbackActiveAddress, false);
        runtimes.delete(key);
      }
    }
    for (const s of config.shockers) {
      let runtime = runtimes.get(s.key);
      if (!runtime) {
        runtime = { lastCommandAt: null, seen: new Set(), pollInitialized: false, pollInFlight: false, apiConnected: false,
          pollError: null, commandError: null, pollFailures: 0, nextPollAt: 0, generation: 0 };
        runtimes.set(s.key, runtime);
      } else {
        if (runtime.settings.uuid !== s.uuid || addressFields.some(key => runtime.settings[key] !== s[key])) {
          runtime.capture?.close();
          runtime.capture = null;
          clearFeedback(runtime);
          send(runtime.settings.feedbackEventAddress, false);
          send(runtime.settings.feedbackActiveAddress, false);
        }
        if (runtime.settings.uuid !== s.uuid) {
          clearTimeout(runtime.nameReadTimer);
          runtime.generation++; runtime.deviceId = null; runtime.sentName = null; runtime.nextNameReadAt = 0;
          runtime.seen.clear(); runtime.pollInitialized = false; runtime.apiConnected = false;
          runtime.pollError = null; runtime.commandError = null; runtime.pollFailures = 0; runtime.nextPollAt = 0;
        }
      }
      runtime.settings = s;
      runtime.capture ??= new OscActionCapture(action => void control(s.key, action));
    }
    selfTestCapture ??= new OscActionCapture(action => {
      if (!config.selfTestEnabled) return;
      for (const key of config.selfTestShockerKeys) void control(key, action, 'self-test OSC');
    });
    if (routeChanged || !listening) {
      log(config.selfTestEnabled
        ? `Self-test OSC route: ${config.selfTestAvatarGroup} \u2192 ${config.shockers.filter(s => config.selfTestShockerKeys.includes(s.key)).map(s => s.name).join(', ')}. Only selected test devices receive avatar actions.`
        : 'Normal OSC routing: each avatar group controls its assigned devices');
    }
    log('Shocker settings applied');
    if (!apiToken) log('API setup: token missing; enter the generated API token value in Settings');
    else if (!config.shockers.some(s => s.uuid)) log('API setup: shocker UUID missing; save the UUID under Options on a shocker card');
    else if (![...runtimes.values()].some(runtime => runtime.settings.uuid && runtime.apiConnected))
      log('API setup: checking connection');
    publishStatus();
    if (listening) {
      for (const runtime of runtimes.values()) {
        if (!runtime.pollInitialized) void pollLogs(runtime);
      }
    }
  }
  configure(config);

  function receiveOsc(packet) {
    if (stopped) return;
    try {
      for (const { address, args } of decodeOsc(packet)) {
        if (config.selfTestEnabled) {
          const prefix = `/avatar/parameters/${config.selfTestAvatarGroup}_`;
          const value = args[0];
          if (address === prefix + 'Intensity') selfTestCapture.setIntensity(value);
          else if (address === prefix + 'Duration') selfTestCapture.setDuration(value);
          else if (address === prefix + 'Action') selfTestCapture.setAction(value);
          continue;
        }
        for (const runtime of runtimes.values()) {
          const s = runtime.settings;
          const value = args[0];
          if (address === s.intensityAddress) {
            if (config.dryRun) log(`[${s.name}] OSC intensity = ${value}`);
            runtime.capture.setIntensity(value);
          } else if (address === s.durationAddress) {
            if (config.dryRun) log(`[${s.name}] OSC duration = ${value}`);
            runtime.capture.setDuration(value);
          } else if (address === s.actionAddress) {
            if (config.dryRun) log(`[${s.name}] OSC action = ${value}`);
            runtime.capture.setAction(value);
          }
        }
      }
    } catch (error) { log(`Ignored invalid OSC packet: ${error.message}`); }
  }
  receiver.on('message', receiveOsc);
  receiver.on('error', error => log(`OSC listener error: ${error.message}`));
  if (!options.externalOsc) try {
    await new Promise((resolve, reject) => {
      receiver.once('error', reject);
      receiver.bind(config.osc.listenPort, config.osc.listenHost ?? '127.0.0.1', () => {
        receiver.off('error', reject); resolve();
      });
    });
  } catch (error) {
    selfTestCapture.close();
    for (const runtime of runtimes.values()) runtime.capture.close();
    receiver.close();
    throw error;
  }
  log(`Listening for VRChat OSC ${options.externalOsc ? 'via overlay on' : 'on'} ${config.osc.listenHost ?? '127.0.0.1'}:${config.osc.listenPort}`);
  log(config.dryRun ? 'Test mode: device commands disabled' : 'Live OpenShock control enabled');
  listening = true;
  if (apiToken && useLiveEvents) {
    liveLogs = createLiveLogs(config, apiToken, { log,
      onState: (connected, detail) => {
        if (!connected) { hubStates.clear(); hubSnapshotReceived = false; }
        liveConnected = connected; liveDetail = detail; publishStatus();
        // Refresh after subscribing, closing the gap between the initial GET and live notifications.
        if (connected) for (const runtime of runtimes.values()) scheduleNameRefresh(runtime);
      },
      onWelcome: () => { hubStates.clear(); hubSnapshotReceived = false; publishStatus(); },
      onDeviceStatus: states => {
        // The first event after Welcome lists ALL accessible ONLINE hubs (including an empty list).
        // Later events are incremental; omitted hubs must keep their earlier state.
        if (!Array.isArray(states) || states.some(state => !state ||
            typeof state.device !== 'string' || !uuidPattern.test(state.device) || typeof state.online !== 'boolean')) return;
        for (const state of states) hubStates.set(state.device.toLowerCase(), state.online);
        hubSnapshotReceived = true;
        publishStatus();
      },
      onDeviceUpdate: (deviceId, type) => {
        if (!uuidPattern.test(deviceId ?? '') || !(type === 2 || type === 'ShockerUpdated')) return;
        for (const runtime of runtimes.values())
          if (!runtime.deviceId || runtime.deviceId === deviceId.toLowerCase()) scheduleNameRefresh(runtime);
      },
      onLog: entries => {
        for (const entry of entries) {
          const parsed = feedbackFromLiveLog(entry);
          if (!parsed) continue;
          for (const runtime of runtimes.values()) {
            if (runtime.settings.uuid.toLowerCase() !== parsed.shockerId || runtime.seen.has(parsed.event.id)) continue;
            runtime.apiConnected = true; runtime.pollInitialized = true; runtime.pollError = null;
            runtime.seen.add(parsed.event.id);
            while (runtime.seen.size > 500) runtime.seen.delete(runtime.seen.values().next().value);
            publishFeedback(runtime, parsed.event);
          }
        }
        publishStatus();
      },
    });
    void liveLogs.start();
  }
  await Promise.all([...runtimes.values()].map(pollLogs));
  if (apiToken) {
    // Successful target checks are never repeated in live event mode. Only failed/new UUIDs retry.
    poller = setInterval(() => { for (const runtime of runtimes.values()) void pollLogs(runtime); },
      useLiveEvents ? 1000 : config.pollIntervalMs);
    poller.unref();
  }

  function stop() {
    if (stopped) return;
    stopped = true; clearInterval(poller);
    selfTestCapture.close();
    for (const runtime of runtimes.values()) { runtime.capture.close(); clearFeedback(runtime); clearTimeout(runtime.nameReadTimer); }
    try { receiver.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    // The sender may never have been bound if no feedback was published.
    try { sender.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    return liveLogs?.stop();
  }
  return Object.assign(stop, {
    configure,
    // Also permits deterministic connection checks in local integration tests.
    poll: () => Promise.all([...runtimes.values()].map(pollLogs)),
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
    applyLimits,
    rename,
    osc: packet => { if (options.externalOsc && Buffer.isBuffer(packet) && packet.length > 0 && packet.length <= 65507) receiveOsc(packet); },
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const configPath = process.argv[2] ?? path.join(here, 'config.json');
    const settings = JSON.parse(await fs.readFile(configPath, 'utf8'));
    const bridge = await startBridge(settings, settings.provider === 'PiShock' ? process.env.PISHOCK_API_KEY?.trim() ?? '' : token, { externalOsc: process.env.SHOCKWAVE_EXTERNAL_OSC === '1' || process.env.SZEBS_EXTERNAL_OSC === '1' });
    const input = readline.createInterface({ input: process.stdin });
    let remoteSession = '';
    input.on('line', line => {
      try {
        const message = JSON.parse(line);
        if (message.kind === 'configure') bridge.configure(message.config);
        else if (message.kind === 'control') void bridge.control(message.key, message.type);
        else if (message.kind === 'panelControl') void bridge.controlPanel(message.key, message.type, message.intensity, message.durationMs, message.expiresAt);
        else if (message.kind === 'remoteGate') remoteSession = /^[a-f0-9]{64}$/.test(message.session) ? message.session : '';
        else if (message.kind === 'remoteControl' && remoteSession && message.session === remoteSession) void bridge.controlRemote(message.key, message.type, message.intensity, message.durationMs, message.expiresAt);
        else if (message.kind === 'applyLimits') void bridge.applyLimits(message.key, message.requestId);
        else if (message.kind === 'rename') void bridge.rename(message.key, message.name, message.requestId);
        else if (message.kind === 'refreshDevices' && bridge.refreshDevices) void bridge.refreshDevices();
        else if (message.kind === 'osc' && typeof message.packet === 'string' && message.packet.length <= 87336) bridge.osc(Buffer.from(message.packet, 'base64'));
        else throw new Error('Unknown overlay command');
      } catch (error) { defaultLog(`Overlay command rejected: ${error.message}`); }
    });
    for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { input.close(); bridge(); });
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
