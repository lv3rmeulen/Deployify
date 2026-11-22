// Turns `deployify plan --output json` (and `diff --output json`) into a pull request comment,
// a job summary and step outputs. Runs inside actions/github-script; `render` is pure so the
// tests can call it directly.
'use strict';

const fs = require('fs');

// GitHub rejects comments over 65,536 characters; leave room for the rest of the comment.
const MAX_COMMENT = 60000;
const MAX_DIFF_PER_SERVICE = 8000;

function marker(environment, projectPath) {
  return `<!-- deployify-plan env=${environment || '(active)'} project=${projectPath || '.'} -->`;
}

function code(text) {
  return '`' + String(text).replace(/`/g, "'") + '`';
}

// Pick a fence longer than any backtick run in the text, so a diff can't close it early.
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

function headline(doc, plan) {
  if (!doc) return '❌ **Plan failed**: Deployify wrote no output.';
  if (plan && plan.blocked) return `⛔ **Blocked**: ${plan.blockedReason || doc.error || 'the manifest audit failed'}`;
  if (!doc.success || !plan) return `❌ **Plan failed**: ${doc.error || `exit code ${doc.exitCode}`}`;
  if (plan.upToDate) return '✅ **No changes.** Everything is up to date.';
  const parts = [];
  if (plan.create.length) parts.push(`${plan.create.length} to create`);
  if (plan.update.length) parts.push(`${plan.update.length} to update`);
  if (plan.delete.length) parts.push(`${plan.delete.length} ${plan.deletesOnApply ? 'to delete' : 'left in place'}`);
  if (plan.unknown.length) parts.push(`${plan.unknown.length} couldn't be checked`);
  return `📋 **${parts.join(', ')}**`;
}

/**
 * @param {object|null} doc  the plan envelope, or null when stdout wasn't valid JSON
 * @param {object|null} diff the diff envelope, if diff ran
 * @param {object} meta { environment, projectPath, sha, runUrl }
 */
function render(doc, diff, meta = {}) {
  const plan = doc && doc.result;
  const environment = (doc && doc.environment) || meta.environment;
  const target = doc && doc.target ? doc.target.name : null;

  const lines = [marker(meta.environment, meta.projectPath)];
  let title = '### Deployify plan';
  if (environment) title += ` · ${code(environment)}`;
  if (target) title += ` → ${target}`;
  lines.push(title, '', headline(doc, plan), '');

  if (plan) {
    const rows = [];
    for (const s of plan.create) rows.push(['➕ create', s, '']);
    for (const s of plan.update) rows.push(['🔄 update', s, '']);
    for (const s of plan.delete) {
      rows.push([plan.deletesOnApply ? '🗑️ delete' : '⏸️ left in place', s,
        plan.deletesOnApply ? '' : 'not in the manifest; apply leaves it running']);
    }
    for (const u of plan.unknown) rows.push(['❔ unknown', u.service, u.reason || '']);
    if (rows.length) {
      lines.push('| | Service | |', '|---|---|---|');
      for (const [action, service, note] of rows) {
        lines.push(`| ${action} | ${code(service)} | ${note.replace(/\|/g, '\\|').replace(/\n/g, ' ')} |`);
      }
      lines.push('');
    }
    if (plan.unchanged.length) {
      lines.push(`Unchanged: ${plan.unchanged.map(code).join(', ')}`, '');
    }

    const findings = (plan.manifestFindings || []).filter(f => f.severity !== 'info');
    if (findings.length) {
      lines.push('**Manifest findings**', '');
      for (const f of findings) {
        const icon = f.severity === 'error' ? '❌' : '⚠️';
        lines.push(`- ${icon} ${code(f.resource)} (${f.type}): ${f.message}`);
      }
      lines.push('');
    }

    if (plan.skipped.length) {
      lines.push('<details><summary>Not deployed by Deployify (' + plan.skipped.length + ')</summary>', '');
      for (const s of plan.skipped) lines.push(`- ${code(s.name)} (${s.type}): ${s.reason}`);
      lines.push('', '</details>', '');
    }
  }

  const changed = diff && diff.result && Array.isArray(diff.result.entries)
    ? diff.result.entries.filter(e => e.kind !== 'unchanged' && (e.newValue || e.oldValue))
    : [];
  const diffBlocks = [];
  for (const e of changed) {
    let body;
    if (e.oldValue != null && e.newValue != null && !e.newValue.includes('\n')) {
      body = fenced(`- ${e.oldValue}\n+ ${e.newValue}`, 'diff');
    } else {
      body = fenced(truncate(e.newValue || e.oldValue, MAX_DIFF_PER_SERVICE), 'diff');
    }
    const label = e.field ? `${e.service} · ${e.field}` : e.service;
    diffBlocks.push(`<details><summary><code>${html(label)}</code> (${e.kind})</summary>\n\n${body}\n\n</details>`);
  }

  const warnings = doc ? (doc.diagnostics || []).filter(d => d.severity !== 'info') : [];
  const footerParts = [];
  if (meta.sha) footerParts.push(`Commit ${code(meta.sha.slice(0, 7))}`);
  if (meta.runUrl) footerParts.push(`[workflow run](${meta.runUrl})`);
  const footer = [];
  if (warnings.length) {
    footer.push('<details><summary>Warnings (' + warnings.length + ')</summary>', '');
    for (const d of warnings) footer.push(`- ${d.severity === 'error' ? '❌' : '⚠️'} ${d.message.replace(/\n/g, ' ')}`);
    footer.push('', '</details>', '');
  }
  if (footerParts.length) footer.push(`<sub>${footerParts.join(' · ')}</sub>`);

  let body = lines.join('\n');
  const tail = footer.join('\n');
  if (diffBlocks.length) {
    let diffs = '\n**Changes**\n\n';
    let omitted = 0;
    for (const block of diffBlocks) {
      if (body.length + diffs.length + block.length + tail.length + 200 > MAX_COMMENT) omitted++;
      else diffs += block + '\n';
    }
    if (omitted) diffs += `\n${omitted} more change(s) left out to fit the comment; see the job log.\n`;
    body += diffs + '\n';
  }
  return body + tail;
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

async function upsertComment({ github, context, core }, body, mark) {
  const { owner, repo } = context.repo;
  const issue_number = context.issue.number;
  const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number, per_page: 100 });
  const existing = comments.find(c => c.user && c.user.type === 'Bot' && c.body && c.body.startsWith(mark));
  const res = existing
    ? await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body })
    : await github.rest.issues.createComment({ owner, repo, issue_number, body });
  return res.data.html_url;
}

async function run({ github, context, core }) {
  const env = process.env;
  const doc = readJson(env.PLAN_JSON, core);
  const diff = readJson(env.DIFF_JSON, core);
  const meta = {
    environment: env.DEPLOYIFY_ENV,
    projectPath: env.PROJECT_PATH,
    sha: (context.payload.pull_request && context.payload.pull_request.head.sha) || context.sha,
    runUrl: `${context.serverUrl}/${context.repo.owner}/${context.repo.repo}/actions/runs/${context.runId}`,
  };
  const body = render(doc, diff, meta);

  core.setOutput('up-to-date', String(!!(doc && doc.result && doc.result.upToDate)));
  await core.summary.addRaw(body.split('\n').slice(1).join('\n'), true).write();

  if (env.COMMENT !== 'true' || !context.issue.number) return;
  try {
    const url = await upsertComment({ github, context, core }, body, marker(meta.environment, meta.projectPath));
    core.setOutput('comment-url', url);
    core.info(`Plan comment: ${url}`);
  } catch (err) {
    // Pull requests from forks get a read-only token.
    core.warning(`Couldn't post the plan comment (${err.status || ''} ${err.message}). ` +
      'The job needs `permissions: pull-requests: write`; pull requests from forks get a read-only token.');
  }
}

module.exports = { run, render, marker };
