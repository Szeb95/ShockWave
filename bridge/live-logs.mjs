import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

export function createLiveLogs(config, token, { log, onState, onLog, onDeviceUpdate, onDeviceStatus, onWelcome }) {
  const retryDelay = attempt => Math.max(config.pollIntervalMs, [3000, 10000, 30000, 60000][Math.min(attempt, 3)]);
  const connection = new HubConnectionBuilder()
    .withUrl(new URL('/1/hubs/user', config.apiBaseUrl).href, {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      headers: { OpenShockToken: token, 'User-Agent': 'ShockWave-Bridge/0.3' },
    })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: context =>
      retryDelay(context.previousRetryCount) })
    .configureLogging(LogLevel.None)
    .build();
  let stopped = false;
  let retryTimer;
  let startFailures = 0;
  let lastError = '';
  function unavailable(error) {
    const detail = error?.message ?? 'Connection interrupted';
    onState(false, 'Live log connection is reconnecting: ' + detail);
    if (detail !== lastError) { lastError = detail; log('OpenShock live log connection failed: ' + detail); }
  }
  function connected() {
    if (stopped) return;
    startFailures = 0; lastError = '';
    log('OpenShock live logs connected; background log polling disabled');
    onState(true, 'Receiving live OpenShock log events');
  }
  connection.on('Log', (_sender, entries) => { if (!stopped && Array.isArray(entries)) onLog(entries); });
  connection.on('Welcome', () => { if (!stopped) onWelcome?.(); });
  connection.on('DeviceStatus', states => { if (!stopped) onDeviceStatus?.(states); });
  connection.on('DeviceUpdate', (deviceId, type) => { if (!stopped) onDeviceUpdate?.(deviceId, type); });
  connection.onreconnecting(error => { if (!stopped) unavailable(error); });
  connection.onreconnected(connected);
  connection.onclose(error => {
    if (stopped) return;
    unavailable(error);
    scheduleStart();
  });
  function scheduleStart() {
    clearTimeout(retryTimer);
    retryTimer = setTimeout(() => void start(), retryDelay(startFailures++));
    retryTimer.unref();
  }
  async function start() {
    if (stopped) return;
    try { await connection.start(); connected(); }
    catch (error) { if (!stopped) { unavailable(error); scheduleStart(); } }
  }
  return { start, stop: async () => { stopped = true; clearTimeout(retryTimer); await connection.stop(); } };
}
