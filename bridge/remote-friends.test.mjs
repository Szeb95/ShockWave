import test from 'node:test';
import assert from 'node:assert/strict';
import { FriendPairing, savedTarget, initiatesSavedConnection } from './remote-friends.mjs';

const ids = ['sw-' + '1'.repeat(32), 'sw-' + '2'.repeat(32)];
function fixture({ fail = -1, automatic = true } = {}) {
  const peers = [], events = [[], []], complete = [[], []], packets = [];
  for (let i = 0; i < 2; i++) peers.push(new FriendPairing({ ownId: ids[i], remoteId: ids[1-i], secret: 'a'.repeat(64),
    send: payload => { packets.push(payload); peers[1-i].receive(payload); },
    event: event => { events[i].push(event); if (automatic && event.kind === 'friend-save') peers[i].stored(event.request, i !== fail); },
    complete: friend => complete[i].push(friend) }));
  peers[0].start(); peers[1].start(); return { peers, events, complete, packets };
}
test('one Save exchanges a distinct per-friend key and persists both sides before completion', () => {
  const f = fixture(); assert.equal(f.peers[0].remember(), true);
  assert.equal(f.complete[0].length, 1); assert.equal(f.complete[1].length, 1);
  assert.equal(f.complete[0][0].secret, f.complete[1][0].secret);
  assert.notEqual(f.complete[0][0].secret, 'a'.repeat(64));
  assert.equal(f.complete[0][0].id, ids[1]); assert.equal(f.complete[1][0].id, ids[0]);
  assert.ok(f.events.every(events => events.some(e => e.kind === 'friend-save') && events.some(e => e.kind === 'friend-saved')));
  assert.ok(f.packets.every(p => !JSON.stringify(p).includes(f.complete[0][0].secret)), 'durable key is not transmitted even inside pairing messages');
  assert.equal(f.peers[0].remember(), false);
});
test('fresh encrypted pairing nonces isolate durable keys from an old invitation', () => {
  const a = fixture(), b = fixture(); a.peers[1].remember(); b.peers[1].remember();
  assert.notEqual(a.complete[0][0].secret, b.complete[0][0].secret);
});
test('disk failure on either side never reports a confirmed pairing', () => {
  for (const fail of [0, 1]) {
    const f = fixture({ fail }); f.peers[0].remember();
    assert.ok(f.complete.every(list => list.length === 0));
    assert.ok(f.events[0].some(e => e.kind === 'friend-failed'));
  }
});
test('saving waits for local disk acknowledgment and cannot be acknowledged by another request', () => {
  const f = fixture({ automatic: false }); f.peers[0].remember();
  assert.equal(f.events[1].filter(e => e.kind === 'friend-save').length, 0);
  const request = f.events[0].find(e => e.kind === 'friend-save').request;
  f.peers[0].receive({ op: 'saved', request }); assert.equal(f.complete[0].length, 0);
  f.peers[0].stored(request, true);
  f.peers[0].receive({ op: 'saved', request: 'f'.repeat(32) }); assert.equal(f.complete[0].length, 0);
  f.peers[1].stored(request, true); assert.equal(f.complete[0].length, 1);
  f.peers[1].receive({ op: 'save', request }); assert.equal(f.complete[1].length, 1);
});
test('simultaneous Save uses the same key and bounded idempotent requests', () => {
  const f = fixture({ automatic: false }); f.peers[0].remember(); f.peers[1].remember();
  for (let round = 0; round < 3; round++) for (let i = 0; i < 2; i++)
    for (const e of f.events[i].filter(e => e.kind === 'friend-save')) f.peers[i].stored(e.request, true);
  assert.equal(f.complete[0].length, 1); assert.equal(f.complete[1].length, 1);
  assert.equal(f.complete[0][0].secret, f.complete[1][0].secret);
});
test('identity mismatch, malformed nonce and old/incomplete requests cannot create a saved friend', () => {
  const events = [], p = new FriendPairing({ ownId: ids[0], remoteId: ids[1], secret: 'a'.repeat(64), send() {}, event: e => events.push(e), complete() { throw new Error('unexpected save'); } });
  p.receive({ op: 'hello', version: 1, id: ids[0], nonce: 'a'.repeat(64) });
  p.receive({ op: 'hello', version: 1, id: ids[1], nonce: 'x'.repeat(64) });
  p.receive({ op: 'save', request: '1'.repeat(32) }); assert.equal(events.length, 0);
  assert.equal(p.remember(), false);
  p.receive({ op: 'hello', version: 1, id: ids[1], nonce: 'b'.repeat(64) });
  p.receive({ op: 'save', request: 'bad' }); assert.equal(events.length, 1);
  p.remember(); for (const pending of p.pending.values()) pending.at -= 11000; p.tick();
  assert.ok(events.some(e => e.kind === 'friend-failed')); assert.equal(p.pending.size, 0);
});
test('saved connections pin identity/key and choose one caller when both Connect switches are on', () => {
  const a = savedTarget({ id: ids[1], secret: 'a'.repeat(64), expires: 1 }, ids[0]);
  const b = savedTarget({ id: ids[0], secret: 'a'.repeat(64) }, ids[1]);
  assert.equal(initiatesSavedConnection(ids[0], a), true); assert.equal(initiatesSavedConnection(ids[1], b), false);
  for (const value of [null, { id: ids[0], secret: 'a'.repeat(64) }, { id: ids[1], secret: 'bad' }]) assert.throws(() => savedTarget(value, ids[0]));
});
