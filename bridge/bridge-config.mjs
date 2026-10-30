import { validShockerName } from './shocker-names.mjs';
import { validPiShockTarget } from './pishock.mjs';

export const defaultApiToken = process.env.OPENSHOCK_TOKEN?.trim() ?? '';
const token = defaultApiToken;
export const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const addressFields = ['intensityAddress', 'durationAddress', 'actionAddress',
  'feedbackEventAddress', 'feedbackActiveAddress', 'feedbackIntensityAddress', 'feedbackModeAddress'];
const suffixes = ['Intensity', 'Duration', 'Action', 'FeedbackEvent', 'FeedbackActive', 'FeedbackIntensity', 'FeedbackMode'];

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

