import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { DirectPeer, makeInvitation, parseInvitation, validateDescription, DIRECT_ICE } from './remote-peer.mjs';

const wait = async condition => { const deadline = Date.now() + 18000; while (!condition()) { if (Date.now() > deadline) throw new Error('Direct peer timeout'); await new Promise(r => setTimeout(r, 20)); } };
test('invitations validate format, secret, expiry; no TURN configuration is accepted', () => {
  const id = 'sw-' + 'a'.repeat(32), secret = 'b'.repeat(64), code = makeInvitation(id, secret);
  assert.equal(parseInvitation(code).secret, secret);
  for (const invalid of ['', 'SW1.bad', makeInvitation(id, secret, Date.now() - 1), makeInvitation('other', secret), makeInvitation(id, 'short')]) assert.throws(() => parseInvitation(invalid));
  assert.ok(DIRECT_ICE.every(s => s.urls.startsWith('stun:')));
  assert.throws(() => new DirectPeer({ secret, iceServers: [{ urls: 'turn:example.com' }] }));
  assert.throws(() => validateDescription({ type: 'offer', sdp: 'm=audio 9 UDP/TLS/RTP/SAVPF 0' }));
});
test('real Node WebRTC peers authenticate, exchange fresh bidirectional state and stop; stale/duplicate/future/wrong session data is ignored', { timeout: 30000 }, async () => {
  const peers = [], events = [[], []], signals = [], secret = randomBytes(32).toString('hex');
  try {
    for (let i = 0; i < 2; i++) peers.push(new DirectPeer({ secret, initiator: i === 0, iceServers: [],
      signal: (type, payload) => { signals.push(JSON.stringify(payload)); setTimeout(() => peers[1 - i].accept(type, payload).catch(() => {}), 0); }, event: e => {
        events[i].push(e); if (e.kind === 'connected') peers[i].send('inventory', { toys: ['initial'] });
      } }));
    await peers[0].offer(); await wait(() => peers.every(p => p.ready));
    await wait(() => events.every(list => list.some(e => e.message?.kind === 'inventory')));
    assert.equal(peers[0].session, peers[1].session);
    assert.ok(signals.every(s => !s.includes(secret) && !s.includes('typ relay')));
    peers[0].send('state', [{ device: 'toy', percent: 40 }], true); peers[1].send('inventory', { toys: ['fixture'] });
    await wait(() => events[0].some(e => e.message?.kind === 'inventory') && events[1].some(e => e.message?.kind === 'state'));
    assert.equal(events[1].find(e => e.message?.kind === 'state').message.payload[0].percent, 40);
    const before = events[1].filter(e => e.kind === 'message').length;
    const packet = { version: 1, session: peers[0].session, kind: 'state', sequence: 1, sentAt: Date.now(), payload: [] };
    peers[0].sendRaw(packet, true); // duplicate
    peers[0].sendRaw({ ...packet, sequence: 200, sentAt: Date.now() - 2000 }, true);
    peers[0].sendRaw({ ...packet, sequence: 201, sentAt: Date.now() + 10000 }, true);
    peers[0].sendRaw({ ...packet, sequence: 202, session: 'wrong' }, true);
    await new Promise(r => setTimeout(r, 100)); assert.equal(events[1].filter(e => e.kind === 'message').length, before);
    peers[0].send('stop', {}); await wait(() => events[1].some(e => e.message?.kind === 'stop'));
    peers[0].close(); await wait(() => peers[1].closed);
  } finally { for (const p of peers) p.close(); }
});
test('wrong invitation secret fails authentication before any application data', { timeout: 30000 }, async () => {
  const peers = [], events = [];
  try {
    for (let i = 0; i < 2; i++) peers.push(new DirectPeer({ secret: (i ? 'b' : 'a').repeat(64), initiator: i === 0, iceServers: [],
      signal: (t, p) => setTimeout(() => peers[1 - i].accept(t, p).catch(() => {}), 0), event: e => events.push(e) }));
    await peers[0].offer(); await wait(() => peers.some(p => p.closed));
    assert.ok(events.every(e => e.kind !== 'connected' && e.kind !== 'message'));
  } finally { for (const p of peers) p.close(); }
});
