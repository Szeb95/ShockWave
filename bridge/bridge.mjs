import fs from 'node:fs/promises';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';
import { startPiShockBridge } from './pishock.mjs';
import { startOpenShockBridge } from './openshock-bridge.mjs';
import { validateConfig, actionRequest, defaultApiToken as token } from './bridge-config.mjs';

export { normalizeConfig, validateConfig, actionRequest } from './bridge-config.mjs';
export { apiRequest, feedbackFromLog, feedbackFromLiveLog } from './openshock-api.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const defaultLog = message => console.log(`[${new Date().toLocaleTimeString()}] ${message}`);

export async function startBridge(input, apiToken = token, options = {}) {
  if (input.provider === 'PiShock') return startPiShockBridge(input, apiToken, options, { validateConfig, actionRequest });
  return startOpenShockBridge(input, apiToken, options);
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
