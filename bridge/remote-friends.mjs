import { randomBytes, createHmac } from 'node:crypto';

export const validPeerId = value => typeof value === 'string' && /^sw-[a-f0-9]{32}$/.test(value);
const validKey = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const validRequest = value => typeof value === 'string' && /^[a-f0-9]{32}$/.test(value);
export function savedTarget(value, ownId) {
  if (!value || !validPeerId(value.id) || value.id === ownId || !validKey(value.secret)) throw new Error('Invalid saved friend');
  return { id: value.id, secret: value.secret, saved: true };
}
export const initiatesSavedConnection = (ownId, target) => ownId < target.id;

// Only used after the existing fingerprint-bound encrypted handshake. Nonces
// never enter signalling; possession of an old invitation cannot derive this key.
export class FriendPairing {
  constructor({ ownId, remoteId, secret, saved = false, send, event, complete }) {
    if (!validPeerId(ownId) || !validPeerId(remoteId) || ownId === remoteId || !validKey(secret)) throw new Error('Invalid friend pairing');
    Object.assign(this, { ownId, remoteId, secret, saved, send, event, complete });
    this.nonce = randomBytes(32).toString('hex'); this.pending = new Map(); this.finished = new Set();
  }
  start() { this.send({ op: 'hello', version: 1, id: this.ownId, nonce: this.nonce }); }
  key() {
    const identities = [[this.ownId, this.nonce], [this.remoteId, this.remoteNonce]].sort((a, b) => a[0] < b[0] ? -1 : 1);
    return createHmac('sha256', Buffer.from(this.secret, 'hex')).update(JSON.stringify(['ShockWave saved friend v1', identities])).digest('hex');
  }
  remember() {
    if (!this.remoteNonce || this.saved || this.pending.size) return false;
    const request = randomBytes(16).toString('hex');
    this.store(request, true); return true;
  }
  store(request, outgoing) {
    this.pending.set(request, { outgoing, at: performance.now() });
    this.event({ kind: 'friend-save', id: this.remoteId, secret: this.key(), request, outgoing });
  }
  stored(request, ok) {
    const pending = this.pending.get(request); if (!pending || pending.stored) return;
    if (!ok) {
      this.pending.delete(request); this.send({ op: 'failed', request });
      this.event({ kind: 'friend-failed' }); return;
    }
    pending.stored = true;
    this.send({ op: pending.outgoing ? 'save' : 'saved', request });
    if (!pending.outgoing) this.finish(request);
  }
  finish(request) {
    this.pending.delete(request); this.finished.add(request);
    if (this.finished.size > 4) this.finished.delete(this.finished.values().next().value);
    if (this.saved) return;
    this.saved = true;
    this.complete({ id: this.remoteId, secret: this.key(), saved: true });
    this.event({ kind: 'friend-saved', id: this.remoteId });
  }
  receive(value) {
    if (!value || typeof value !== 'object') return;
    if (value.op === 'hello') {
      if (value.version !== 1 || value.id !== this.remoteId || !validKey(value.nonce) || this.remoteNonce) return;
      this.remoteNonce = value.nonce;
      this.start(); // The first hello may have arrived before the other clock handshake completed.
      this.event({ kind: 'friend-ready', id: this.remoteId, available: !this.saved }); return;
    }
    if (!this.remoteNonce || !validRequest(value.request)) return;
    if (value.op === 'save') {
      if (this.finished.has(value.request)) { this.send({ op: 'saved', request: value.request }); return; }
      if (this.saved || this.pending.has(value.request) || this.pending.size >= 2) return;
      this.store(value.request, false);
    } else if (value.op === 'saved' && this.pending.get(value.request)?.outgoing && this.pending.get(value.request)?.stored) this.finish(value.request);
    else if (value.op === 'failed' && this.pending.delete(value.request)) this.event({ kind: 'friend-failed' });
  }
  tick() {
    for (const [request, pending] of this.pending) if (performance.now() - pending.at > 10000) {
      this.pending.delete(request); this.event({ kind: 'friend-failed' });
    }
  }
}
