import { Buffer } from 'node:buffer';

function paddedLength(length) {
  return (length + 3) & ~3;
}

function readString(buffer, offset, end) {
  const zero = buffer.indexOf(0, offset);
  if (zero < 0 || zero >= end) throw new Error('Invalid OSC string');
  const next = offset + paddedLength(zero - offset + 1);
  if (next > end) throw new Error('Truncated OSC string');
  return [buffer.toString('utf8', offset, zero), next];
}

export function decodeOsc(buffer) {
  const messages = [];
  function parse(start, end) {
    let [address, offset] = readString(buffer, start, end);
    if (address === '#bundle') {
      if (offset + 8 > end) throw new Error('Truncated OSC bundle');
      offset += 8;
      while (offset < end) {
        if (offset + 4 > end) throw new Error('Truncated OSC bundle element');
        const size = buffer.readInt32BE(offset);
        offset += 4;
        if (size <= 0 || offset + size > end) throw new Error('Invalid OSC bundle element');
        parse(offset, offset + size);
        offset += size;
      }
      return;
    }
    if (!address.startsWith('/')) throw new Error('Invalid OSC address');
    const [tags, afterTags] = readString(buffer, offset, end);
    if (!tags.startsWith(',')) throw new Error('Missing OSC type tag');
    offset = afterTags;
    const args = [];
    for (const tag of tags.slice(1)) {
      if (tag === 'T') args.push(true);
      else if (tag === 'F') args.push(false);
      else if (tag === 'i' || tag === 'f') {
        if (offset + 4 > end) throw new Error('Truncated OSC argument');
        args.push(tag === 'i' ? buffer.readInt32BE(offset) : buffer.readFloatBE(offset));
        offset += 4;
      } else if (tag === 's') {
        const [value, next] = readString(buffer, offset, end);
        args.push(value);
        offset = next;
      } else throw new Error(`Unsupported OSC type ${tag}`);
    }
    messages.push({ address, args });
  }
  parse(0, buffer.length);
  return messages;
}

function encodeString(value) {
  const raw = Buffer.from(value, 'utf8');
  const out = Buffer.alloc(paddedLength(raw.length + 1));
  raw.copy(out);
  return out;
}

export function encodeOsc(address, value, numberType = 'auto') {
  if (!address.startsWith('/')) throw new Error('OSC address must start with /');
  let tag;
  let argument = Buffer.alloc(0);
  if (typeof value === 'boolean') tag = value ? 'T' : 'F';
  else if (typeof value === 'number' && Number.isFinite(value)) {
    argument = Buffer.alloc(4);
    if (Number.isInteger(value) && numberType !== 'float') {
      tag = 'i';
      argument.writeInt32BE(value);
    } else {
      tag = 'f';
      argument.writeFloatBE(value);
    }
  } else if (typeof value === 'string') {
    tag = 's';
    argument = encodeString(value);
  } else throw new Error('Unsupported OSC value');
  return Buffer.concat([encodeString(address), encodeString(',' + tag), argument]);
}
