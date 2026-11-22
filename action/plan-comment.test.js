'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { render, marker } = require('./plan-comment.js');

const envelope = (result, extra = {}) => ({
  schemaVersion: 1, command: 'plan', success: true, exitCode: 0, error: null,
  environment: 'prod', target: { id: 'Kubernetes', name: 'Kubernetes' }, diagnostics: [], result, ...extra,
});
const plan = (over = {}) => ({
  create: [], update: [], delete: [], unchanged: [], unknown: [], skipped: [],
  upToDate: false, deletesOnApply: false, blocked: false, blockedReason: null, manifestFindings: [], ...over,
});

test('starts with the marker for the environment and project', () => {
  const body = render(envelope(plan({ upToDate: true })), null, { environment: 'prod', projectPath: 'src/AppHost' });
  assert.ok(body.startsWith(marker('prod', 'src/AppHost')));
  assert.notStrictEqual(marker('prod', '.'), marker('staging', '.'));
});

test('up to date', () => {
  const body = render(envelope(plan({ upToDate: true, unchanged: ['api'] })), null, {});
  assert.match(body, /No changes/);
  assert.match(body, /### Deployify plan · `prod` → Kubernetes/);
  assert.match(body, /Unchanged: `api`/);
});

test('counts and rows for each kind of change', () => {
  const body = render(envelope(plan({
    create: ['api'], update: ['worker'], delete: ['old'],
    unknown: [{ service: 'web', reason: 'timed out | 30s' }],
  })), null, {});
  assert.match(body, /1 to create, 1 to update, 1 left in place, 1 couldn't be checked/);
  assert.match(body, /\| ➕ create \| `api` \|/);
  assert.match(body, /\| ❔ unknown \| `web` \| timed out \\\| 30s \|/);
});

test('deletes when apply prunes', () => {
  const body = render(envelope(plan({ delete: ['old'], deletesOnApply: true })), null, {});
  assert.match(body, /1 to delete/);
  assert.match(body, /🗑️ delete/);
});

test('blocked plan', () => {
  const body = render(envelope(plan({
    blocked: true, blockedReason: "parameter 'db-password' has no value",
    manifestFindings: [{ resource: 'db-password', type: 'parameter.v0', severity: 'error', message: 'no value' }],
  }), { success: false, exitCode: 1 }), null, {});
  assert.match(body, /⛔ \*\*Blocked\*\*: parameter 'db-password' has no value/);
  assert.match(body, /❌ `db-password` \(parameter.v0\): no value/);
});

test('failed before a result', () => {
  const body = render(envelope(null, { success: false, exitCode: 1, error: 'No AppHost found' }), null, { environment: 'prod' });
  assert.match(body, /Plan failed\*\*: No AppHost found/);
});

test('no output at all', () => {
  assert.match(render(null, null, {}), /Deployify wrote no output/);
});

test('diff values go in collapsed blocks with a safe fence', () => {
  const diff = { result: { entries: [
    { service: 'worker', kind: 'changed', field: 'manifest', oldValue: null, newValue: '-  image: a\n+  image: b ```' },
    { service: 'api', kind: 'changed', field: 'instance_count', oldValue: '1', newValue: '2' },
    { service: 'redis', kind: 'unchanged', field: null, oldValue: null, newValue: null },
  ] } };
  const body = render(envelope(plan({ update: ['worker', 'api'] })), diff, {});
  assert.match(body, /<summary><code>worker · manifest<\/code> \(changed\)<\/summary>/);
  assert.match(body, /\|\n\n\*\*Changes\*\*/);
  assert.match(body, /````diff\n-  image: a/);
  assert.match(body, /```diff\n- 1\n\+ 2\n```/);
  assert.doesNotMatch(body, /redis<\/code> \(unchanged/);
});

test('stays under the comment limit', () => {
  const big = 'x'.repeat(7000);
  const entries = Array.from({ length: 40 }, (_, i) => ({ service: `s${i}`, kind: 'changed', field: null, oldValue: null, newValue: big }));
  const body = render(envelope(plan({ update: entries.map(e => e.service) })), { result: { entries } }, {});
  assert.ok(body.length < 65536, `length ${body.length}`);
  assert.match(body, /more change\(s\) left out/);
});

test('footer links the commit and run', () => {
  const body = render(envelope(plan({ upToDate: true })), null, { sha: 'abcdef1234567', runUrl: 'https://example.com/run' });
  assert.match(body, /Commit `abcdef1` · \[workflow run\]\(https:\/\/example.com\/run\)/);
});
