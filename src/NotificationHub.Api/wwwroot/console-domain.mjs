export function parseObject(text) {
  let value; try { value = JSON.parse(text); } catch { throw new Error('invalidJson'); }
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('objectRequired');
  return value;
}
export function notificationState(deliveries) {
  if (!deliveries.length) return 'pending';
  const states = deliveries.map(x => x.status);
  if (states.every(x => x === 'succeeded')) return 'succeeded';
  if (states.includes('processing')) return 'processing';
  if (states.includes('retry_wait')) return 'retry_wait';
  if (states.includes('pending')) return 'pending';
  if (states.includes('stopped')) return 'stopped';
  if (states.includes('expired')) return 'expired';
  return states.every(x => x === 'cancelled') ? 'cancelled' : 'mixed';
}
export function latestPublished(versions) {
  const result = new Map();
  for (const version of versions.filter(x => x.status === 'published')) {
    if (!result.has(version.operation) || version.version > result.get(version.operation).version) result.set(version.operation, version);
  }
  return [...result.values()];
}
export function submission({ eventType, key, payload, targets, expiry }, now = Date.now()) {
  if (!key.trim()) throw new Error('keyRequired');
  if (!eventType || targets.length === 0 || targets.length > 20) throw new Error('targetRequired');
  const unique = new Map(targets.map(x => [x.connectionId + '|' + x.operation, x]));
  if (unique.size !== targets.length) throw new Error('duplicateTarget');
  const body = { eventType, data: parseObject(payload), targets };
  if (expiry) {
    const date = new Date(expiry);
    if (!Number.isFinite(date.getTime()) || date.getTime() <= now) throw new Error('expiryFuture');
    body.expiresAt = date.toISOString();
  }
  return body;
}
export function operationDefinition(form) {
  let inputs; try { inputs = JSON.parse(form.inputs); } catch { throw new Error('invalidJson'); }
  if (!Array.isArray(inputs)) throw new Error('arrayRequired');
  const definition = {
    eventType: form.eventType.trim(), url: form.url.trim(), method: form.method, encoding: form.encoding,
    description: form.description?.trim() || null,
    inputs, body: parseObject(form.mapping), headers: parseObject(form.headers), success: parseObject(form.successRule),
    authType: form.authType, authHeader: form.authHeader.trim(), idempotencyHeader: form.idempotencyHeader.trim(),
    timeoutSeconds: Number(form.timeoutSeconds), maxAttempts: Number(form.maxAttempts), maxAgeHours: Number(form.maxAgeHours),
    idempotencyRetentionHours: Number(form.idempotencyRetentionHours), requestsPerMinute: Number(form.requestsPerMinute)
  };
  if (definition.idempotencyRetentionHours < definition.maxAgeHours) throw new Error('retentionTooShort');
  if ((definition.description?.length || 0) > 2000) throw new Error('descriptionTooLong');
  return definition;
}
