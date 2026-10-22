import { RTCPeerConnection } from 'werift';
import { randomBytes, createHash, createHmac, timingSafeEqual } from 'node:crypto';

export const DIRECT_ICE = Object.freeze([{ urls: 'stun:stun.l.google.com:19302' }]);
// Continuous toy state needs room for Internet RTT, conservative clock error
// and both UI/IPC hops. Discrete shock commands retain their shorter deadline.
export const STATE_LIFETIME_MS = 500;
export const messageLifetimeMs = kind => kind === 'state' ? STATE_LIFETIME_MS : kind === 'shock' ? 250 : 3000;
const nonce = () => randomBytes(24).toString('hex');
const digest = text => createHash('sha256').update(text).digest('hex');
const fingerprint = sdp => {
  const matches = [...sdp.matchAll(/^a=fingerprint:sha-256 ([A-Fa-f0-9:]+)\r?$/gm)];
  const values = new Set(matches.map(m => m[1].toLowerCase()));
  if (values.size !== 1 || !/^(?:[a-f0-9]{2}:){31}[a-f0-9]{2}$/.test([...values][0])) throw new Error('Invalid peer identity');
  return [...values][0];
};
export function validateDescription(description) {
  if (!description || !['offer', 'answer'].includes(description.type) || typeof description.sdp !== 'string' || description.sdp.length > 48000 ||
      /typ relay|^m=(?!application )/m.test(description.sdp) || !description.sdp.includes('m=application ')) throw new Error('Invalid direct connection');
  fingerprint(description.sdp); return description;
}
export function makeInvitation(id, secret, expires = Date.now() + 86400000) {
  return 'SW1.' + Buffer.from(JSON.stringify({ id, secret, expires })).toString('base64url');
}
export function parseInvitation(code) {
  if (typeof code !== 'string' || code.length > 600 || !code.startsWith('SW1.')) throw new Error('Paste a ShockWave connection code');
  let value; try { value = JSON.parse(Buffer.from(code.slice(4), 'base64url').toString()); } catch { throw new Error('Invalid connection code'); }
  if (!/^sw-[a-f0-9]{32}$/.test(value.id) || !/^[a-f0-9]{64}$/.test(value.secret) || !Number.isSafeInteger(value.expires) || value.expires < Date.now() || value.expires > Date.now() + 90000000)
    throw new Error('Connection code is invalid or expired');
  return value;
}

// Application data is never sent through the signalling service. Only STUN is
// configured; incoming relay candidates/descriptions are rejected as well.
export class DirectPeer {
  constructor({ secret, initiator, signal, event, iceServers = DIRECT_ICE }) {
    if (!/^[a-f0-9]{64}$/.test(secret) || iceServers.some(s => !String(s.urls).startsWith('stun:'))) throw new Error('Direct connections require STUN only');
    this.secret = secret; this.initiator = initiator; this.signal = signal; this.event = event;
    this.closed = false; this.authenticated = false; this.ready = false; this.offset = 0; this.uncertainty = Infinity;
    this.lastSequence = new Map(); this.sequence = 0; this.lastRx = performance.now(); this.pings = new Map();
    this.pc = new RTCPeerConnection({ iceServers, iceUseIpv4: true, iceUseIpv6: true, iceStunGatherTimeout: 2, iceAdditionalHostAddresses: ['127.0.0.1'] });
    this.pc.onicecandidate = ({ candidate }) => {
      if (candidate && !candidate.candidate.includes('typ relay')) this.signal('CANDIDATE', { candidate: candidate.toJSON() });
    };
    this.pc.ondatachannel = ({ channel }) => this.bind(channel);
    this.pc.onconnectionstatechange = () => {
      if (['failed', 'closed', 'disconnected'].includes(this.pc.connectionState)) this.close('Direct connection ended');
    };
    this.timer = setInterval(() => this.tick(), 250);
    this.deadline = setTimeout(() => this.close('No direct route available. TURN is disabled.'), 20000);
    if (initiator) { this.bind(this.pc.createDataChannel('sw-control')); this.bind(this.pc.createDataChannel('sw-state', { ordered: false, maxRetransmits: 0 })); }
  }
  async offer() {
    const sdp = await this.pc.createOffer();
    const gathering = this.pc.setLocalDescription(sdp);
    this.signal('OFFER', { sdp }); // Trickle immediately; an unavailable STUN server cannot hold the offer hostage.
    await gathering;
  }
  async accept(type, payload) {
    if (this.closed) return;
    if (type === 'CANDIDATE') {
      if (payload?.candidate && typeof payload.candidate.candidate === 'string' && payload.candidate.candidate.length < 2048 && !payload.candidate.candidate.includes('typ relay'))
        await this.pc.addIceCandidate(payload.candidate);
    } else {
      await this.pc.setRemoteDescription(validateDescription(payload.sdp));
      if (type === 'OFFER') {
        const sdp = await this.pc.createAnswer(); const gathering = this.pc.setLocalDescription(sdp);
        this.signal('ANSWER', { sdp }); await gathering;
      }
    }
  }
  bind(channel) {
    if (!['sw-control', 'sw-state'].includes(channel.label) || this[channel.label]) { channel.close(); return; }
    this[channel.label] = channel;
    channel.onopen = () => { if (channel.label === 'sw-control') this.authenticate(); };
    channel.onmessage = ({ data }) => this.receive(data, channel.label);
    channel.onclose = () => this.close('Peer disconnected');
  }
  proof(role, local, remote, challenge) { return createHmac('sha256', Buffer.from(this.secret, 'hex')).update(JSON.stringify([1, role, local, remote, challenge])).digest('hex'); }
  authenticate() {
    if (this.closed) return;
    try {
      this.localFingerprint = fingerprint(this.pc.localDescription.sdp); this.remoteFingerprint = fingerprint(this.pc.remoteDescription.sdp);
      const challenge = nonce(), role = this.initiator ? 'join' : 'host';
      this.sendRaw({ kind: 'auth', challenge, proof: this.proof(role, this.localFingerprint, this.remoteFingerprint, challenge) });
    } catch { this.close('Peer identity could not be verified'); }
  }
  sendRaw(value, fast = false) {
    const channel = this[fast ? 'sw-state' : 'sw-control'];
    if (this.closed || channel?.readyState !== 'open' || channel.bufferedAmount > (fast ? 8192 : 131072)) return false;
    const text = JSON.stringify(value); if (text.length > 60000) return false;
    try { channel.send(text); return true; } catch { this.close('Direct connection write failed'); return false; }
  }
  send(kind, payload, fast = false, sentAt = Date.now()) {
    if (!this.ready) return false;
    const sent = this.sendRaw({ version: 1, session: this.session, kind, sequence: ++this.sequence, sentAt, payload }, fast);
    if (kind === 'state') { const stats = this.metrics(); if (sent) stats.sent++; else stats.blocked++; }
    return sent;
  }
  metrics() {
    return this.stateMetrics ??= { sent: 0, received: 0, expired: 0, future: 0, blocked: 0,
      maxAgeMs: 0, maxGapMs: 0, minRemainingMs: STATE_LIFETIME_MS, rttMs: 0, uncertaintyMs: 0 };
  }
  receive(data, channel) {
    if (this.closed || data.length > 60000) return;
    let msg; try { msg = JSON.parse(data.toString()); } catch { return; }
    try {
      if (!this.authenticated) {
        if (channel !== 'sw-control' || msg.kind !== 'auth' || !/^[a-f0-9]{48}$/.test(msg.challenge) || !/^[a-f0-9]{64}$/.test(msg.proof)) return;
        this.localFingerprint = fingerprint(this.pc.localDescription.sdp); this.remoteFingerprint = fingerprint(this.pc.remoteDescription.sdp);
        const expected = this.proof(this.initiator ? 'host' : 'join', this.remoteFingerprint, this.localFingerprint, msg.challenge);
        if (!timingSafeEqual(Buffer.from(expected, 'hex'), Buffer.from(msg.proof, 'hex'))) { this.close('Connection code did not match peer identity'); return; }
        this.authenticated = true;
        this.session = digest([this.secret, ...[this.localFingerprint, this.remoteFingerprint].sort()].join('|'));
        this.ping(); return;
      }
      if (msg.kind === 'ping' && Number.isSafeInteger(msg.t1) && typeof msg.id === 'string') {
        this.sendRaw({ kind: 'pong', id: msg.id, t1: msg.t1, t2: Date.now(), t3: Date.now() }); return;
      }
      if (msg.kind === 'pong' && this.pings.get(msg.id) === msg.t1 && [msg.t1, msg.t2, msg.t3].every(Number.isSafeInteger)) {
        this.pings.delete(msg.id); const t4 = Date.now(), rtt = t4 - msg.t1 - (msg.t3 - msg.t2);
        if (rtt < 0 || rtt > 1000) return;
        this.offset = ((msg.t2 - msg.t1) + (msg.t3 - t4)) / 2; this.uncertainty = rtt / 2 + 5;
        const stats = this.metrics(); stats.rttMs = Math.max(stats.rttMs, rtt); stats.uncertaintyMs = Math.max(stats.uncertaintyMs, this.uncertainty);
        this.lastRx = performance.now();
        if (!this.ready) {
          this.ready = true; clearTimeout(this.deadline); this.event({ kind: 'connected', session: this.session });
          if (this.pendingInventory) { const pending = this.pendingInventory; this.pendingInventory = null; this.receive(pending, 'sw-control'); }
        }
        this.event({ kind: 'timing', milliseconds: rtt }); return;
      }
      if (!this.ready) {
        if (channel === 'sw-control' && msg.kind === 'inventory' && msg.version === 1 && msg.session === this.session) this.pendingInventory = data;
        return;
      }
      if (msg.version !== 1 || msg.session !== this.session || !Number.isSafeInteger(msg.sequence) || msg.sequence <= (this.lastSequence.get(msg.kind) ?? 0) || !Number.isSafeInteger(msg.sentAt)) return;
      const age = Date.now() - (msg.sentAt - this.offset);
      if (msg.kind === 'state') {
        const stats = this.metrics(); stats.maxAgeMs = Math.max(stats.maxAgeMs, Math.max(0, age));
        stats.minRemainingMs = Math.min(stats.minRemainingMs, Math.max(0, STATE_LIFETIME_MS - Math.max(0, age) - this.uncertainty));
        if (age < -this.uncertainty) stats.future++;
        else if (age + this.uncertainty >= STATE_LIFETIME_MS) stats.expired++;
      }
      if (age < -this.uncertainty || age + this.uncertainty >= messageLifetimeMs(msg.kind)) return;
      if (!['inventory', 'state', 'shock', 'result', 'stop', 'friend'].includes(msg.kind) || (channel === 'sw-state' && msg.kind !== 'state')) return;
      this.lastSequence.set(msg.kind, msg.sequence); this.lastRx = performance.now();
      if (msg.kind === 'state') {
        const stats = this.metrics(), now = performance.now(); stats.received++;
        if (this.lastStateAt !== undefined) stats.maxGapMs = Math.max(stats.maxGapMs, now-this.lastStateAt);
        this.lastStateAt = now;
      }
      this.event({ kind: 'message', message: msg, receivedAt: Date.now(), remainingMs: messageLifetimeMs(msg.kind) - Math.max(0, age) - this.uncertainty });
    } catch { this.close('Invalid peer connection'); }
  }
  ping() { const id = nonce(); const t1 = Date.now(); this.pings.clear(); this.pings.set(id, t1); this.sendRaw({ kind: 'ping', id, t1 }); }
  tick() {
    if (this.closed) return;
    if (this.authenticated && performance.now() - (this.lastPing ?? 0) > 1000) { this.lastPing = performance.now(); this.ping(); }
    if (this.ready && performance.now() - this.lastRx > 2500) this.close('Peer connection timed out');
    if (this.ready && performance.now() - (this.lastHealthAt ?? 0) >= 5000) this.reportHealth();
  }
  reportHealth() {
    const stats = this.metrics();
    // Numeric aggregates only: no codes, endpoints, session/device IDs or payloads.
    this.event({ kind: 'health', ...Object.fromEntries(Object.entries(stats).map(([key, value]) => [key, Math.round(value)])) });
    this.lastHealthAt = performance.now(); this.stateMetrics = null;
  }
  close(detail = 'Disconnected') {
    if (this.closed) return; this.closed = true; this.ready = false; clearInterval(this.timer); clearTimeout(this.deadline);
    this.pc.close().catch(() => {}); this.event({ kind: 'disconnected', detail });
  }
}
