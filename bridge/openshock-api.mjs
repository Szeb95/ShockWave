import { defaultApiToken as token, uuidPattern } from './bridge-config.mjs';

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

export function errorDetail(error) {
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

