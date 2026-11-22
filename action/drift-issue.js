// Turns `deployify drift --output json` into a job summary, step outputs and, per environment,
// a GitHub issue that is opened when it drifts, updated when what drifted changes, and closed
// when it matches its deploy again. Runs inside actions/github-script; `render`, `issueBody` and
// `decide` are pure so the tests can call them directly.
'use strict';

const fs = require('fs');

const LABEL = 'deployify-drift';
// GitHub rejects issue bodies over 65,536 characters.
const MAX_BODY = 60000;
const MAX_VALUE = 4000;

function marker(environment, projectPath, fingerprint) {
  return `<!-- deployify-drift env=${environment} project=${projectPath || '.'}${fingerprint ? ` fingerprint=${fingerprint}` : ''} -->`;
}

// The environment, project and fingerprint an issue body was written for, or null.
function parseMarker(body) {
  const m = /^<!-- deployify-drift env=(\S+) project=(\S+)(?: fingerprint=(\S+))? -->/.exec(body || '');
  return m ? { environment: m[1], projectPath: m[2], fingerprint: m[3] || '' } : null;
}

function code(text) {
  return '`' + String(text).replace(/`/g, "'") + '`';
}

function html(text) {
  return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function fenced(text, lang) {
  const longest = Math.max(2, ...(String(text).match(/`+/g) || []).map(s => s.length));
  const fence = '`'.repeat(longest + 1);
  return `${fence}${lang}\n${text}\n${fence}`;
}

function truncate(text, max) {
  if (text.length <= max) return text;
  return text.slice(0, max) + `\n… (${text.length - max} more characters; see the job log)`;
}

const STATUS = {
  inSync: '✅ in sync',
  drifted: '⚠️ drifted',
  unknown: '❔ partly checked',
  error: '❌ check failed',
  notDeployed: '➖ not deployed yet',
  skipped: '⏭️ skipped',
};

const KIND = {
  missing: ['➖ missing', 'deployed, no longer on the target'],
  extra: ['➕ extra', 'on the target, not deployed by Deployify'],
  changed: ['🔄 changed', ''],
  unknown: ['❔ unknown', ''],
};

function cell(text) {
  return String(text || '').replace(/\|/g, '\\|').replace(/\n/g, ' ');
}

function deployedLine(env) {
  const d = env.deployed;
  if (!d) return null;
  const parts = [];
  if (d.tag) parts.push(`tag ${code(d.tag)}`);
  if (d.commit) parts.push(`commit ${code(d.commit.slice(0, 12))}`);
  if (d.actor) parts.push(`by ${d.actor}`);
  const run = d.runUrl ? ` ([run](${d.runUrl}))` : '';
  return `Compared with the deploy of ${d.at}${parts.length ? ` (${parts.join(', ')})` : ''}${run}.`;
}

// The differences of one environment: a table, then each changed value in a collapsed block.
function entriesMarkdown(env) {
  const lines = [];
  if (!env.entries.length) return lines;
  lines.push('| | Service | |', '|---|---|---|');
  for (const e of env.entries) {
    const [label, note] = KIND[e.kind] || [e.kind, ''];
    const detail = e.kind === 'unknown' ? e.reason : e.field ? `${e.field}` : note;
    lines.push(`| ${label} | ${code(e.service)} | ${cell(detail)} |`);
  }
  lines.push('');
  for (const e of env.entries.filter(x => x.kind === 'changed' && (x.live || x.deployed))) {
    let body;
    if (e.live != null && e.deployed != null && !e.live.includes('\n') && !e.deployed.includes('\n')) {
      body = fenced(`- ${e.live}   (live)\n+ ${e.deployed}   (deployed)`, 'diff');
    } else {
      body = fenced(truncate([e.live, e.deployed].filter(v => v != null).join('\n'), MAX_VALUE), 'diff');
    }
    const label = e.field ? `${e.service} · ${e.field}` : e.service;
    lines.push(`<details><summary><code>${html(label)}</code></summary>\n\n${body}\n\n</details>`, '');
  }
  return lines;
}

/** The job summary for the whole run. */
function render(doc, meta = {}) {
  const lines = ['### Deployify drift', ''];
  if (!doc) return lines.concat('❌ **Drift check failed**: Deployify wrote no output.').join('\n');
  const result = doc.result;
  if (!result) return lines.concat(`❌ **Drift check failed**: ${doc.error || `exit code ${doc.exitCode}`}`).join('\n');

  lines.push('| Environment | Target | Status | |', '|---|---|---|---|');
  for (const env of result.environments) {
    const note = env.status === 'drifted' ? `${env.entries.filter(e => e.kind !== 'unknown').length} difference(s)` : env.reason || '';
    lines.push(`| ${code(env.environment)} | ${env.target ? env.target.name : ''} | ${STATUS[env.status] || env.status} | ${cell(note)} |`);
  }
  lines.push('');
  for (const env of result.environments.filter(e => e.entries.length)) {
    lines.push(`#### ${code(env.environment)}`, '');
    const d = deployedLine(env);
    if (d) lines.push(d, '');
    lines.push(...entriesMarkdown(env));
  }
  if (meta.runUrl) lines.push(`<sub>[workflow run](${meta.runUrl})</sub>`);
  return lines.join('\n');
}

/** The body of the issue for one drifted environment. */
function issueBody(env, meta = {}) {
  const lines = [marker(env.environment, meta.projectPath, env.fingerprint), ''];
  lines.push(`Deployify found that ${code(env.environment)}${env.target ? ` (${env.target.name})` : ''} no longer matches what was last deployed to it.`, '');
  const d = deployedLine(env);
  if (d) lines.push(d, '');
  lines.push(...entriesMarkdown(env));
  if (env.ignored) lines.push(`${env.ignored} expected difference(s) left out (\`drift.ignore\`).`, '');
  lines.push('Redeploy to put back what was deployed, or, if the change should stay, make it in the AppHost or ' +
    '`deployify-state.json` and deploy that. This issue closes when the environment matches its deploy again.', '');
  const footer = [`Checked ${env.checkedAt}`];
  if (meta.runUrl) footer.push(`[workflow run](${meta.runUrl})`);
  lines.push(`<sub>${footer.join(' · ')}</sub>`);
  const body = lines.join('\n');
  return body.length <= MAX_BODY ? body : body.slice(0, MAX_BODY) + '\n\n… (cut to fit; see the job log)';
}

function issueTitle(env) {
  return `Drift in ${env.environment}`;
}

/**
 * What to do with issues: one action per environment that needs one.
 * @param {object[]} environments the drift document's environments
 * @param {object[]} openIssues open issues with the label ({ number, body })
 * @returns {{ type: 'create'|'update'|'close', environment: string, number?: number }[]}
 */
function decide(environments, openIssues, projectPath) {
  const project = projectPath || '.';
  const actions = [];
  for (const env of environments) {
    const issue = openIssues.find(i => {
      const m = parseMarker(i.body);
      return m && m.environment === env.environment && m.projectPath === project;
    });
    if (env.status === 'drifted') {
      if (!issue) actions.push({ type: 'create', environment: env.environment });
      else if (parseMarker(issue.body).fingerprint !== env.fingerprint) {
        actions.push({ type: 'update', environment: env.environment, number: issue.number });
      }
    } else if (env.status === 'inSync' && issue) {
      actions.push({ type: 'close', environment: env.environment, number: issue.number });
    }
  }
  return actions;
}

function readJson(path, core) {
  if (!path) return null;
  try {
    return JSON.parse(fs.readFileSync(path, 'utf8'));
  } catch (err) {
    core.warning(`Couldn't read ${path}: ${err.message}`);
    return null;
  }
}

async function syncIssues({ github, context, core }, environments, meta) {
  const { owner, repo } = context.repo;
  const open = await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'open', labels: LABEL, per_page: 100 });
  for (const action of decide(environments, open, meta.projectPath)) {
    const env = environments.find(e => e.environment === action.environment);
    if (action.type === 'create') {
      const res = await github.rest.issues.create({ owner, repo, title: issueTitle(env), body: issueBody(env, meta), labels: [LABEL] });
      core.info(`Opened ${res.data.html_url}`);
    } else if (action.type === 'update') {
      await github.rest.issues.update({ owner, repo, issue_number: action.number, body: issueBody(env, meta) });
      await github.rest.issues.createComment({ owner, repo, issue_number: action.number,
        body: `What drifted in ${code(env.environment)} has changed; the description now shows the current differences.` +
          (meta.runUrl ? ` ([run](${meta.runUrl}))` : '') });
      core.info(`Updated issue #${action.number}`);
    } else {
      await github.rest.issues.createComment({ owner, repo, issue_number: action.number,
        body: `✅ ${code(env.environment)} matches its last deploy again.` + (meta.runUrl ? ` ([run](${meta.runUrl}))` : '') });
      await github.rest.issues.update({ owner, repo, issue_number: action.number, state: 'closed', state_reason: 'completed' });
      core.info(`Closed issue #${action.number}`);
    }
  }
}

async function run({ github, context, core }) {
  const env = process.env;
  const doc = readJson(env.DRIFT_JSON, core);
  const meta = {
    projectPath: env.PROJECT_PATH,
    runUrl: `${context.serverUrl}/${context.repo.owner}/${context.repo.repo}/actions/runs/${context.runId}`,
  };

  const environments = doc && doc.result ? doc.result.environments : [];
  core.setOutput('drifted', String(!!(doc && doc.result && doc.result.drifted)));
  core.setOutput('drifted-environments', environments.filter(e => e.status === 'drifted').map(e => e.environment).join(','));
  await core.summary.addRaw(render(doc, meta), true).write();

  if (env.ISSUE !== 'true' || !doc || !doc.result) return;
  try {
    await syncIssues({ github, context, core }, environments, meta);
  } catch (err) {
    core.warning(`Couldn't open or update the drift issue (${err.status || ''} ${err.message}). ` +
      'The job needs `permissions: issues: write`.');
  }
}

module.exports = { run, render, issueBody, issueTitle, decide, marker, parseMarker, LABEL };
