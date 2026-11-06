import test from 'node:test';
import assert from 'node:assert/strict';
import { parseObject, notificationState, latestPublished, submission, operationDefinition } from '../../src/NotificationHub.Api/wwwroot/console-domain.mjs';
test('payload must be valid JSON object', () => {
  assert.deepEqual(parseObject('{"name":"sample"}'), { name: 'sample' });
  for (const value of ['[]', 'null', 'true', 'invalid']) assert.throws(() => parseObject(value));
});
test('mixed notification states never appear as fully successful', () => {
  assert.equal(notificationState([{ status: 'succeeded' }, { status: 'retry_wait' }]), 'retry_wait');
  assert.equal(notificationState([{ status: 'succeeded' }, { status: 'stopped' }]), 'stopped');
  assert.equal(notificationState([{ status: 'succeeded' }, { status: 'cancelled' }]), 'mixed');
  assert.equal(notificationState([{ status: 'succeeded' }]), 'succeeded');
});
test('target options use latest published version per capability', () => {
  const versions = [{ operation: 'a', version: 1, status: 'published' }, { operation: 'a', version: 2, status: 'published' }, { operation: 'a', version: 3, status: 'draft' }, { operation: 'b', version: 1, status: 'published' }];
  assert.deepEqual(latestPublished(versions).map(x => x.version), [2, 1]);
});
test('submission keeps selected targets and converts expiration to UTC', () => {
  const target = { connectionId: 'c1', operation: 'update' };
  const input = { eventType: 'paid', key: 'same-key', payload: '{"paid":true}', targets: [target], expiry: '2026-01-02T03:00:00+08:00' };
  const result = submission(input, Date.parse('2026-01-01T00:00:00Z'));
  assert.equal(result.expiresAt, '2026-01-01T19:00:00.000Z');
  assert.deepEqual(result.targets, [target]);
  assert.throws(() => submission({ ...input, targets: [] }), /targetRequired/);
  assert.throws(() => submission({ ...input, targets: [target, target] }), /duplicateTarget/);
  assert.throws(() => submission({ ...input, key: '' }), /keyRequired/);
  assert.throws(() => submission(input, Date.parse('2027-01-01')), /expiryFuture/);
});
test('capability form preserves typed mapping and enforces deduplication lifetime', () => {
  const form = { eventType: 'paid', url: 'https://example.com', method: 'POST', encoding: 'json', inputs: '[]', mapping: '{"status":{"constant":1}}', headers: '{}', successRule: '{"httpStatuses":[200]}', authType: 'none', authHeader: 'Authorization', idempotencyHeader: 'Idempotency-Key', timeoutSeconds: '15', maxAttempts: '8', maxAgeHours: '24', idempotencyRetentionHours: '48', requestsPerMinute: '60' };
  const result = operationDefinition(form);
  assert.equal(result.body.status.constant, 1); assert.equal(result.timeoutSeconds, 15);
  assert.throws(() => operationDefinition({ ...form, idempotencyRetentionHours: '1' }), /retentionTooShort/);
  assert.equal(result.description, null);
  assert.equal(operationDefinition({ ...form, description: '  Contact update\nAfter payment  ' }).description, 'Contact update\nAfter payment');
  assert.throws(() => operationDefinition({ ...form, description: 'x'.repeat(2001) }), /descriptionTooLong/);
});
