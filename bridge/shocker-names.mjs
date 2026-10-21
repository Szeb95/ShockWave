const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const models = ['CaiXianlin', 'PetTrainer', 'Petrainer998DR', 'WellturnT330'];

export function validShockerName(name) {
  return typeof name === 'string' && name.trim().length > 0 && name.length <= 64 && !/[\u0000-\u001f\u007f-\u009f]/.test(name);
}

export function readShockerName(response, uuid) {
  const data = response?.data;
  if (!guid.test(data?.id ?? '') || data.id.toLowerCase() !== uuid.toLowerCase() ||
      !guid.test(data?.device ?? '') || !validShockerName(data?.name)) return null;
  return { name: data.name, device: data.device.toLowerCase() };
}

// OpenShock's edit endpoint requires pairing fields too. Always read them freshly and preserve them.
export async function renameShocker(uuid, name, request, stillCurrent = () => true) {
  if (!guid.test(uuid) || !validShockerName(name)) throw new Error('Name must contain 1–64 characters without line breaks');
  const endpoint = `/1/shockers/${uuid}`;
  const response = await request('GET', endpoint);
  const metadata = readShockerName(response, uuid);
  const data = response?.data;
  if (!metadata || !Number.isInteger(data.rfId) || data.rfId < 0 || data.rfId > 65535 ||
      !(models.includes(data.model) || (Number.isInteger(data.model) && data.model >= 0 && data.model < models.length)))
    throw new Error('Unexpected shocker metadata; name was not written');
  if (!stillCurrent()) throw new Error('Name or target changed; old rename canceled');
  if (data.name !== name)
    await request('PATCH', endpoint, { name, device: data.device, rfId: data.rfId, model: data.model });
  return { name, device: metadata.device };
}
