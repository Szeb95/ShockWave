import dgram from 'node:dgram';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { encodeOsc } from './osc.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(await fs.readFile(path.join(here, 'config.json'), 'utf8'));
if (!config.dryRun) throw new Error('Simulation is available only while dryRun is true');
const type = process.argv[2] ?? 'Vibrate';
const intensity = Number(process.argv[3] ?? 10);
const duration = Number(process.argv[4] ?? 20);
if (!['Shock', 'Vibrate'].includes(type)) throw new Error('Action must be Shock or Vibrate');
if (!Number.isInteger(intensity) || intensity < 0 || intensity > 100)
  throw new Error('Intensity must be an integer from 0 to 100');
if (!Number.isInteger(duration) || duration < 0 || duration > 100)
  throw new Error('Duration must be an integer from 0 to 100');
const socket = dgram.createSocket('udp4');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const send = (address, value) => new Promise((resolve, reject) => {
  socket.send(encodeOsc(address, value), config.osc.listenPort,
    config.osc.listenHost ?? '127.0.0.1', error => error ? reject(error) : resolve());
});

try {
  await send(config.osc.actionAddress, 0);
  await send(config.osc.intensityAddress, intensity);
  await send(config.osc.durationAddress, duration);
  await wait(50);
  await send(config.osc.actionAddress, type === 'Shock' ? 1 : 2);
  await wait(150);
  await send(config.osc.actionAddress, 0);
  console.log(`Sent test OSC values: ${type}, intensity ${intensity}, duration ${duration}`);
} finally {
  socket.close();
}
