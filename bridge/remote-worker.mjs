import { randomBytes, createHmac, timingSafeEqual } from 'node:crypto';
import { createInterface } from 'node:readline';
import WebSocket from 'ws';
import { DirectPeer, makeInvitation, parseInvitation, messageLifetimeMs } from './remote-peer.mjs';

const emit = value => process.stdout.write(JSON.stringify(value) + '\n');
const id = 'sw-' + randomBytes(16).toString('hex');
let secret = randomBytes(32).toString('hex'), expires = Date.now() + 86400000;
const token = randomBytes(24).toString('hex');
let socket, peer, target, remoteId, connectionId, enabled = false, retryAt = 0, heartbeat = performance.now();
const seenOffers = new Set();
let expiredOutgoingState = 0;
const offerProof = (key, id, sdp) => createHmac('sha256', Buffer.from(key, 'hex')).update(id + '\n' + sdp.type + '\n' + sdp.sdp).digest('hex');
const signal = (type, payload) => {
  if (socket?.readyState === 1) {
    const text = JSON.stringify({ type, dst: remoteId, payload: { ...payload, connectionId, type: 'data', ...(type === 'OFFER' ? {
      label: 'sw-control', reliable: true, serialization: 'raw', metadata: { sw1: offerProof(target.secret, connectionId, payload.sdp) }
    } : {}) } });
    if (process.env.SHOCKWAVE_REMOTE_DIAGNOSTIC === '1') emit({ kind: 'diagnostic', type: 'SEND-' + type, bytes: text.length });
    socket.send(text);
  }
};
function closePeer() { const old = peer; peer = null; old?.close(); }
function createPeer(initiator) {
  peer = new DirectPeer({ secret: initiator ? target.secret : secret, initiator, signal, event: value => {
    if (value.kind === 'disconnected') { retryAt = performance.now() + 5000; }
    if (value.kind === 'health') { emit({ ...value, ipcExpired: expiredOutgoingState }); expiredOutgoingState = 0; }
    else emit(value);
  } }); return peer;
}
async function join() {
  if (!enabled || !target || socket?.readyState !== 1 || peer && !peer.closed) return;
  if (target.expires < Date.now()) { disable(); emit({ kind: 'error', detail: 'Friend’s code expired. Ask for a new connection code.' }); return; }
  remoteId = target.id; connectionId = 'dc_' + randomBytes(16).toString('hex');
  try { await createPeer(true).offer(); } catch { closePeer(); emit({ kind: 'disconnected', detail: 'Direct connection could not start' }); }
}
function openHelper() {
  if (!enabled) return;
  emit({ kind: 'status', detail: 'Connecting to setup helper · TURN disabled' });
  socket = new WebSocket(`wss://0.peerjs.com/peerjs?key=peerjs&id=${id}&token=${token}&version=1.5.5`, { maxPayload: 65000, handshakeTimeout: 12000 });
  const current = socket;
  current.onmessage = async ({ data }) => {
    if (current !== socket || !enabled || typeof data !== 'string' || data.length > 65000) return;
    try {
      const msg = JSON.parse(data);
      if (process.env.SHOCKWAVE_REMOTE_DIAGNOSTIC === '1') emit({ kind: 'diagnostic', type: msg.type, validSource: /^sw-[a-f0-9]{32}$/.test(msg.src), validConnection: /^dc_[a-f0-9]{32}$/.test(msg.payload?.connectionId) });
      if (msg.type === 'OPEN') { emit({ kind: 'status', detail: target ? 'Waiting for friend · direct only' : 'Connect is on · share your code with your friend' }); await join(); }
      else if (msg.type === 'OFFER' && !target && Date.now() < expires && (!peer || peer.closed) && /^sw-[a-f0-9]{32}$/.test(msg.src)) {
        const offeredId = msg.payload?.connectionId, proof = msg.payload?.metadata?.sw1;
        if (!/^dc_[a-f0-9]{32}$/.test(offeredId) || seenOffers.has(offeredId) || !/^[a-f0-9]{64}$/.test(proof) ||
            typeof msg.payload?.sdp?.sdp !== 'string' || msg.payload.sdp.sdp.length > 48000 ||
            !timingSafeEqual(Buffer.from(proof, 'hex'), Buffer.from(offerProof(secret, offeredId, msg.payload.sdp), 'hex'))) return;
        seenOffers.add(offeredId); if (seenOffers.size > 64) seenOffers.delete(seenOffers.values().next().value);
        remoteId = msg.src; connectionId = offeredId;
        await createPeer(false).accept('OFFER', msg.payload);
      } else if (['ANSWER', 'CANDIDATE'].includes(msg.type) && peer && msg.src === remoteId && msg.payload?.connectionId === connectionId) await peer.accept(msg.type, msg.payload);
      else if (msg.type === 'EXPIRE' && peer && msg.src === remoteId) closePeer();
      else if (['ERROR', 'ID-TAKEN', 'INVALID-KEY'].includes(msg.type)) { emit({ kind: 'status', detail: 'Setup helper unavailable. Try Disconnect, then Connect.' }); }
    } catch { closePeer(); }
  };
  current.onerror = () => { if (current === socket) emit({ kind: 'status', detail: 'Setup helper unavailable · retrying' }); };
  current.onclose = event => {
    if (process.env.SHOCKWAVE_REMOTE_DIAGNOSTIC === '1') emit({ kind: 'diagnostic', type: 'CLOSE', code: event.code, reason: event.reason });
    if (current === socket) { socket = null; retryAt = performance.now() + 5000; }
  };
}
function disable() { enabled = false; closePeer(); const old = socket; socket = null; old?.close(); emit({ kind: 'status', detail: 'Disconnected · Connect is off' }); }
const input = createInterface({ input: process.stdin });
input.on('line', line => {
  if (line.length > 65000) return;
  try {
    const msg = JSON.parse(line);
    if (msg.kind === 'heartbeat') heartbeat = performance.now();
    else if (msg.kind === 'enable') {
      disable(); target = msg.code?.trim() ? parseInvitation(msg.code.trim()) : null;
      if (target?.id === id) throw new Error('Use your friend’s code, not your own');
      if (Date.now() >= expires) { secret = randomBytes(32).toString('hex'); expires = Date.now() + 86400000; }
      enabled = true; emit({ kind: 'invitation', code: makeInvitation(id, secret, expires) }); openHelper();
    } else if (msg.kind === 'disable') disable();
    else if (msg.kind === 'send' && ['inventory', 'state', 'shock', 'result', 'stop'].includes(msg.type)) {
      const age = Date.now() - msg.createdAt;
      if (Number.isSafeInteger(msg.createdAt) && age >= -10 && age < messageLifetimeMs(msg.type))
        peer?.send(msg.type, msg.payload, msg.type === 'state', msg.createdAt);
      else if (msg.type === 'state') expiredOutgoingState++;
    }
  } catch { disable(); emit({ kind: 'error', detail: 'Invalid or expired connection code. Connect is off.' }); }
});
input.on('close', () => { disable(); process.exit(0); });
const timer = setInterval(() => {
  if (performance.now() - heartbeat > 2000) { disable(); clearInterval(timer); process.exit(0); }
  if (!enabled) return;
  if (!socket && performance.now() >= retryAt) openHelper();
  if (target && peer?.closed && performance.now() >= retryAt) void join();
  if (socket?.readyState === 1 && performance.now() - (globalThis.swLastHeartbeat ?? 0) >= 5000) {
    globalThis.swLastHeartbeat = performance.now(); socket.send(JSON.stringify({ type: 'HEARTBEAT' }));
  }
}, 250);
emit({ kind: 'ready' });
