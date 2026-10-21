const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function id(value) {
  if (typeof value !== 'string' || !guid.test(value)) throw new Error('Unexpected OpenShock share identifier');
  return value.toLowerCase();
}
function list(value) {
  if (!Array.isArray(value) || value.length > 250) throw new Error('Unexpected or too large OpenShock share list');
  return value;
}
function capped(value, maximum, ceiling) {
  if (value == null) return maximum;
  if (!Number.isInteger(value) || value < 0 || value > ceiling) throw new Error('Unexpected OpenShock share limits');
  return Math.min(value, maximum);
}
function preservedSettings(share, settings) {
  const permissions = {};
  for (const key of ['shock', 'vibrate', 'sound', 'live']) {
    if (typeof share.permissions?.[key] !== 'boolean') throw new Error('Unexpected OpenShock share permissions');
    permissions[key] = share.permissions[key];
  }
  if (!share.limits || typeof share.limits !== 'object') throw new Error('Unexpected OpenShock share limits');
  return { permissions, limits: {
    intensity: capped(share.limits.intensity, settings.maxIntensity, 100),
    duration: capped(share.limits.duration, settings.maxDurationMs, 65535),
  } };
}

// Owner operation requested by Apply or a debounced UI maximum edit. No device command or write retry.
export async function applyShareLimits(settings, request) {
  const shocker = id(settings.uuid);
  const plans = [];
  const users = list((await request('GET', `/1/shockers/${shocker}/shares`))?.data);
  for (const share of users) {
    plans.push({ endpoint: `/1/shockers/${shocker}/shares/${id(share.sharedWith?.id)}`,
      body: preservedSettings(share, settings) });
  }
  const links = list((await request('GET', '/1/shares/links'))?.data);
  for (const link of links) {
    const linkId = id(link.id);
    const detail = (await request('GET', `/1/public/shares/links/${linkId}`))?.data;
    const devices = list(detail?.devices);
    for (const device of devices) {
      for (const share of list(device.shockers)) {
        if (id(share.id) !== shocker) continue;
        plans.push({ endpoint: `/1/shares/links/${linkId}/${shocker}`,
          body: { ...preservedSettings(share, settings),
            // This API does not expose the previous cooldown. Apply explicitly uses the card's value.
            cooldown: settings.cooldownMs === 0 ? null : Math.max(300, settings.cooldownMs) } });
      }
    }
  }
  if (plans.length > 250 || new Set(plans.map(plan => plan.endpoint)).size !== plans.length)
    throw new Error('Unexpected or too large OpenShock share update');
  let applied = 0;
  try {
    for (const plan of plans) { await request('PATCH', plan.endpoint, plan.body); applied++; }
  } catch (error) { error.appliedShares = applied; error.totalShares = plans.length; throw error; }
  return { applied, total: plans.length };
}
