import test from 'node:test';
import assert from 'node:assert/strict';
import dgram from 'node:dgram';
import { once } from 'node:events';
import { startBridge } from './bridge.mjs';
import { encodeOsc } from './osc.mjs';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
test('overlay-owned OSC socket forwards PiCtrl once while toy feedback stays separate', async () => {
  const owner = dgram.createSocket('udp4'); owner.bind(0, '127.0.0.1'); await once(owner, 'listening');
  const lines = [];
  const config = { apiBaseUrl: 'https://api.openshock.app', dryRun: true, pollIntervalMs: 1000,
    osc: { listenPort: owner.address().port, sendPort: owner.address().port, listenHost: '127.0.0.1', sendHost: '127.0.0.1' },
    shockers: [{ key: 'fixture', name: 'Fixture', uuid: '', avatarGroup: 'PiCtrl', cooldownMs: 0 }] };
  const bridge = await startBridge(config, '', { externalOsc: true, log: line => lines.push(line) });
  try {
    assert(lines.some(line => line.includes('via overlay')), 'bridge starts while the overlay owns the port');
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 0));
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Intensity', 50));
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Duration', 5));
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 2));
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 2));
    await wait(180);
    assert.equal(lines.filter(line => line.includes('TEST ONLY: Vibrate')).length, 1, 'duplicate held action stays one command');
    bridge.osc(encodeOsc('/avatar/parameters/SzebsToy_InPlay', true));
    bridge.osc(encodeOsc('/avatar/parameters/SzebsToy_Rx_Level', .9, 'float'));
    await wait(80);
    assert.equal(lines.filter(line => line.includes('TEST ONLY:')).length, 1, 'toy input and feedback cannot become OpenShock commands');
    await bridge();
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 0));
    bridge.osc(encodeOsc('/avatar/parameters/PiCtrl_Action', 1));
    await wait(80);
    assert.equal(lines.filter(line => line.includes('TEST ONLY:')).length, 1, 'stopped bridge drops forwarded packets without replay');
  } finally { await bridge(); owner.close(); }
});
