import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { DirectPeer, makeInvitation, parseInvitation, validateDescription, DIRECT_ICE, STATE_LIFETIME_MS, messageLifetimeMs } from './remote-peer.mjs';

test('toy jitter budget preserves original age and clock uncertainty; shock expiry remains shorter', t => {
  const now = 1000000, offset = 120000, events = [];
  t.mock.method(Date, 'now', () => now);
  const peer = Object.assign(Object.create(DirectPeer.prototype), { closed: false, authenticated: true, ready: true,
    offset, uncertainty: 81, session: 'fixture', lastSequence: new Map(), event: e => events.push(e) });
  let sequence = 0;
  const deliver = (kind, age) => peer.receive(JSON.stringify({ version: 1, session: 'fixture', kind,
    sequence: ++sequence, sentAt: now + offset - age, payload: [] }), kind === 'state' ? 'sw-state' : 'sw-control');
  deliver('state', 80);
  assert.equal(STATE_LIFETIME_MS, 500);
  assert.equal(events.length, 1);
  assert.equal(events[0].remainingMs, 339); // 500 minus one-way age and conservative RTT/2 + 5 error.
  deliver('state', 300); // Delayed but still fresh: the old 250 ms mode dropped this.
  assert.equal(events.length, 2); assert.equal(events[1].remainingMs, 119);
  deliver('state', 419); deliver('state', 500); deliver('state', -82);
  assert.equal(events.length, 2, 'expired/boundary/future state cannot renew a motor');
  deliver('shock', 170);
  assert.equal(events.length, 2, '500 ms toy tolerance must never extend shock expiry');
  deliver('shock', 80);
  assert.equal(events.at(-1).remainingMs, 89);
  assert.equal(messageLifetimeMs('shock'), 250);
  assert.equal(messageLifetimeMs('inventory'), 3000);
  peer.sendRaw = () => false;
  peer.send('state', [] ,true);
  peer.reportHealth();
  const health = events.at(-1);
  assert.equal(health.kind, 'health');
  assert.equal(health.received, 2); assert.equal(health.expired, 2); assert.equal(health.future, 1); assert.equal(health.blocked, 1);
  assert.equal(health.maxAgeMs, 500); assert.equal(health.minRemainingMs, 0);
  assert.ok(Object.entries(health).every(([key,value]) => key === 'kind' || Number.isFinite(value)));
  assert.ok(!JSON.stringify(health).includes('fixture') && !('session' in health) && !('payload' in health));
  assert.equal(peer.metrics().received, 0, 'reported counters reset instead of growing without bound');
});

test('real encrypted peers sustain toy state with 150 ms RTT, jitter and loss, then expire silence', { timeout: 30000 }, async () => {
  const peers = [], events = [[], []], secret = randomBytes(32).toString('hex');
  let stream, leaseDeadline = 0, lapse = false, received = 0;
  try {
    for (let i = 0; i < 2; i++) peers.push(new DirectPeer({ secret, initiator: i === 0, iceServers: [],
      signal: (type, payload) => setTimeout(() => peers[1-i].accept(type, payload).catch(() => {}), 0),
      event: e => events[i].push(e) }));
    await peers[0].offer(); await wait(() => peers.every(p => p.ready));
    for (const peer of peers) {
      const send = peer.sendRaw.bind(peer);
      peer.sendRaw = (value, fast = false) => {
        if (value.kind === 'state' && value.sequence % 7 === 0) return true; // dropped, never replayed
        const delay = value.kind === 'state' && value.sequence % 5 === 0 ? 145 : 75;
        setTimeout(() => send(value, fast), delay); return true;
      };
    }
    await wait(() => peers.every(p => p.uncertainty >= 75 && p.uncertainty < 120));
    peers[1].event = e => {
      events[1].push(e);
      if (e.message?.kind === 'state') {
        // Account for a receiving UI hop, without timestamping this packet fresh.
        setTimeout(() => { if (leaseDeadline && Date.now() >= leaseDeadline) lapse = true;
          leaseDeadline = e.receivedAt + e.remainingMs; received++; }, 25);
      }
    };
    stream = setInterval(() => peers[0].send('state', [{ percent: 65, claim: 1 }], true), 33);
    await new Promise(r => setTimeout(r, 5000)); clearInterval(stream); stream = null;
    // Windows/shared CI timers need not deliver 33 ms callbacks at that rate.
    // Assert sustained coverage through the actual lease deadlines below,
    // rather than requiring desktop throughput from the test host.
    assert.ok(received >= 20, `sustained state reaches the receiver (${received} accepted updates)`);
    assert.equal(lapse, false, 'normal Internet delay must not revoke a healthy drawing');
    assert.ok(Date.now() < leaseDeadline, 'the final drawing lease is still live when the stream stops');
    assert.ok(peers.every(p => p.ready), 'connection remains active during drawing');
    await new Promise(r => setTimeout(r, STATE_LIFETIME_MS + 200));
    assert.ok(Date.now() >= leaseDeadline, 'silence expires output while the connection remains active');
  } finally { clearInterval(stream); for (const peer of peers) peer.close(); }
});

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
