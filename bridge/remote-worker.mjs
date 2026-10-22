import { randomBytes, createHmac, timingSafeEqual } from 'node:crypto';
import { createInterface } from 'node:readline';
import WebSocket from 'ws';
import { DirectPeer, makeInvitation, parseInvitation, messageLifetimeMs } from './remote-peer.mjs';
import { FriendPairing, validPeerId, savedTarget, initiatesSavedConnection } from './remote-friends.mjs';

const emit = value => process.stdout.write(JSON.stringify(value) + '\n');
let id = 'sw-' + randomBytes(16).toString('hex');
let secret = randomBytes(32).toString('hex'), expires = Date.now() + 86400000;
const token = randomBytes(24).toString('hex');
let socket, peer, pairing, target, remoteId, connectionId, enabled = false, retryAt = 0, heartbeat = performance.now();
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
function closePeer() { const old = peer; peer = null; pairing = null; old?.close(); }
function createPeer(initiator, key = initiator ? target.secret : secret) {
  const current = new DirectPeer({ secret: key, initiator, signal, event: value => {
    if (current !== peer) return;
    if (value.kind === 'disconnected') { retryAt = performance.now() + 5000; }
    if (value.kind === 'connected') {
      pairing = new FriendPairing({ ownId: id, remoteId, secret: key, saved: !!target?.saved,
        send: payload => current.send('friend', payload), event: event => emit({ ...event, session: current.session }),
        complete: friend => {
          target = friend;
          // Retire the invitation used to pair. It cannot authorize a new pairing after Forget.
          secret = randomBytes(32).toString('hex'); expires = Date.now() + 86400000;
          emit({ kind: 'invitation', code: '' });
        } });
      emit(value); pairing.start(); return;
    }
    if (value.kind === 'message' && value.message.kind === 'friend') { pairing?.receive(value.message.payload); return; }
    if (value.kind === 'health') { emit({ ...value, ipcExpired: expiredOutgoingState }); expiredOutgoingState = 0; }
    else emit(value);
  } }); peer = current; return current;
}
async function join() {
  if (!enabled || !target || socket?.readyState !== 1 || peer && !peer.closed) return;
  if (target.saved && !initiatesSavedConnection(id, target)) return;
  if (!target.saved && target.expires < Date.now()) { disable(); emit({ kind: 'error', detail: 'Friend’s code expired. Ask for a new connection code.' }); return; }
  closePeer(); retryAt = performance.now() + 5000;
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
      else if (msg.type === 'OFFER' && (!peer || peer.closed) && validPeerId(msg.src)) {
        const key = target?.saved && msg.src === target.id && !initiatesSavedConnection(id, target) ? target.secret :
          !target && Date.now() < expires ? secret : null;
        if (!key) return;
        const offeredId = msg.payload?.connectionId, proof = msg.payload?.metadata?.sw1;
        if (!/^dc_[a-f0-9]{32}$/.test(offeredId) || seenOffers.has(offeredId) || !/^[a-f0-9]{64}$/.test(proof) ||
            typeof msg.payload?.sdp?.sdp !== 'string' || msg.payload.sdp.sdp.length > 48000 ||
            !timingSafeEqual(Buffer.from(proof, 'hex'), Buffer.from(offerProof(key, offeredId, msg.payload.sdp), 'hex'))) return;
        seenOffers.add(offeredId); if (seenOffers.size > 64) seenOffers.delete(seenOffers.values().next().value);
        closePeer(); remoteId = msg.src; connectionId = offeredId;
        await createPeer(false, key).accept('OFFER', msg.payload);
      } else if (['ANSWER', 'CANDIDATE'].includes(msg.type) && peer && msg.src === remoteId && msg.payload?.connectionId === connectionId) await peer.accept(msg.type, msg.payload);
      else if (msg.type === 'EXPIRE' && peer && msg.src === remoteId) { closePeer(); retryAt = performance.now() + 5000; }
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
      disable();
      if (msg.identity !== undefined) { if (!validPeerId(msg.identity)) throw new Error('Invalid identity'); id = msg.identity; }
      target = msg.friend ? savedTarget(msg.friend, id) : msg.code?.trim() ? parseInvitation(msg.code.trim()) : null;
      if (target?.id === id) throw new Error('Use your friend’s code, not your own');
      if (Date.now() >= expires) { secret = randomBytes(32).toString('hex'); expires = Date.now() + 86400000; }
      enabled = true; emit({ kind: 'invitation', code: target?.saved ? '' : makeInvitation(id, secret, expires) }); openHelper();
    } else if (msg.kind === 'disable') disable();
    else if (msg.kind === 'remember-friend') { if (!pairing?.remember()) emit({ kind: 'friend-failed', session: peer?.session ?? '' }); }
    else if (msg.kind === 'friend-stored') pairing?.stored(msg.request, msg.ok === true);
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
  if (target && (!peer || peer.closed) && performance.now() >= retryAt) void join();
  pairing?.tick();
  if (socket?.readyState === 1 && performance.now() - (globalThis.swLastHeartbeat ?? 0) >= 5000) {
    globalThis.swLastHeartbeat = performance.now(); socket.send(JSON.stringify({ type: 'HEARTBEAT' }));
  }
}, 250);
emit({ kind: 'ready' });
