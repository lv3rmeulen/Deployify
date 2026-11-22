'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { render, issueBody, issueTitle, decide, marker, parseMarker } = require('./drift-issue.js');

const deployed = { releaseId: 'a1b2c3d4e5f6', at: '2026-11-20T14:02:00+00:00', tag: '4f2c9e1', commit: '4f2c9e1a7b3d5e6f', actor: 'lv3rmeulen', runUrl: null };

function env(name, status, extra = {}) {
  return {
    environment: name, target: { id: 'Kubernetes', name: 'Kubernetes' }, status,
    checkedAt: '2026-11-22T09:00:00+00:00', reason: null, deployed, entries: [], ignored: 0, fingerprint: '', alert: null,
    ...extra,
  };
}

const drifted = env('prod', 'drifted', {
  fingerprint: '0123456789abcdef',
  entries: [
    { service: 'api', kind: 'changed', field: 'replicas', live: '5', deployed: '2', reason: null },
    { service: 'worker', kind: 'missing', field: null, live: null, deployed: null, reason: null },
  ],
});

function doc(environments, exitCode = 2) {
  return { schemaVersion: 1, command: 'drift', success: exitCode === 0, exitCode, error: null, environment: null, target: null,
    diagnostics: [], result: { drifted: environments.some(e => e.status === 'drifted'), environments } };
}

test('marker round-trips', () => {
  assert.deepStrictEqual(parseMarker(marker('prod', 'src/AppHost', 'abc') + '\nrest'),
    { environment: 'prod', projectPath: 'src/AppHost', fingerprint: 'abc' });
  assert.strictEqual(parseMarker('no marker'), null);
});

test('summary lists every environment and the differences', () => {
  const md = render(doc([drifted, env('staging', 'inSync'), env('dev', 'notDeployed', { deployed: null, reason: 'Nothing to compare against.' })]));
  assert.match(md, /`prod` \| Kubernetes \| ⚠️ drifted \| 2 difference\(s\)/);
  assert.match(md, /`staging` \| Kubernetes \| ✅ in sync/);
  assert.match(md, /`dev` .*Nothing to compare against/);
  assert.match(md, /🔄 changed \| `api` \| replicas/);
  assert.match(md, /- 5 {3}\(live\)\n\+ 2 {3}\(deployed\)/);
});

test('summary of a failed run', () => {
  assert.match(render(null), /wrote no output/);
  assert.match(render({ ...doc([], 1), result: null, error: 'boom' }), /boom/);
});

test('issue body starts with the marker and says what was deployed', () => {
  const body = issueBody(drifted, { projectPath: 'src/AppHost', runUrl: 'https://example.test/run/1' });
  assert.ok(body.startsWith(marker('prod', 'src/AppHost', '0123456789abcdef')));
  assert.match(body, /tag `4f2c9e1`, commit `4f2c9e1a7b3d`, by lv3rmeulen/);
  assert.match(body, /➖ missing \| `worker`/);
  assert.match(body, /\[workflow run\]\(https:\/\/example.test\/run\/1\)/);
  assert.strictEqual(issueTitle(drifted), 'Drift in prod');
});

test('opens an issue for a new drift', () => {
  assert.deepStrictEqual(decide([drifted], [], '.'), [{ type: 'create', environment: 'prod' }]);
});

test('leaves the issue alone while the drift is the same', () => {
  const open = [{ number: 7, body: issueBody(drifted) }];
  assert.deepStrictEqual(decide([drifted], open, '.'), []);
});

test('updates the issue when what drifted changes', () => {
  const open = [{ number: 7, body: issueBody({ ...drifted, fingerprint: 'ffffffffffffffff' }) }];
  assert.deepStrictEqual(decide([drifted], open, '.'), [{ type: 'update', environment: 'prod', number: 7 }]);
});

test('closes the issue when the environment is in sync again', () => {
  const open = [{ number: 7, body: issueBody(drifted) }];
  assert.deepStrictEqual(decide([env('prod', 'inSync')], open, '.'), [{ type: 'close', environment: 'prod', number: 7 }]);
});

test('a failed check neither opens nor closes an issue', () => {
  const open = [{ number: 7, body: issueBody(drifted) }];
  assert.deepStrictEqual(decide([env('prod', 'error', { reason: 'unreachable' })], open, '.'), []);
  assert.deepStrictEqual(decide([env('prod', 'error')], [], '.'), []);
});

test('issues of another project or environment are not touched', () => {
  const open = [{ number: 7, body: issueBody(drifted, { projectPath: 'other' }) }, { number: 8, body: issueBody({ ...drifted, environment: 'staging' }) }];
  assert.deepStrictEqual(decide([env('prod', 'inSync')], open, '.'), []);
  assert.deepStrictEqual(decide([drifted], open, '.'), [{ type: 'create', environment: 'prod' }]);
});
