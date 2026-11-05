// npm-audit-baseline.mjs - lane D CI npm dependency-audit step (R-OD4).
// Node built-ins only; runs without node_modules.
// Usage: node scripts/npm-audit-baseline.mjs <generate|check> <app-dir>
//   generate  writes <app-dir>/audit-baseline.txt from a fresh audit (owner-invoked job only).
//   check     compares a fresh audit with <app-dir>/audit-baseline.txt (CI; never writes).
// Exit: 0 ok; 1 check found flagged names not in the baseline;
//       2 usage, environment, audit-output or baseline error.
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const BASELINE = 'audit-baseline.txt';
const AUDIT_ARGS = ['audit', '--package-lock-only', '--json'];
const WIN = process.platform === 'win32'; // npm is npm.cmd there: run one fixed command line via the shell

class Fatal extends Error {}
const fail = (msg) => { throw new Fatal(msg); };
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const byteOrder = (a, b) => Buffer.compare(Buffer.from(a, 'utf8'), Buffer.from(b, 'utf8'));

// npm config comes from the caller: cache and logs dir set (under RUNNER_TEMP on a runner), notifier off.
function auditEnv() {
  for (const k of ['npm_config_cache', 'npm_config_logs_dir']) {
    const v = process.env[k];
    if (!v) fail(`${k} is not set`);
    if (process.env.RUNNER_TEMP) {
      const rel = path.relative(path.resolve(process.env.RUNNER_TEMP), path.resolve(v));
      if (rel === '' || rel.startsWith('..') || path.isAbsolute(rel)) fail(`${k} is not under RUNNER_TEMP`);
    }
  }
  if (process.env.npm_config_update_notifier !== 'false') fail('npm_config_update_notifier must be false');
  return process.env;
}

// Runs the audit and applies the validity rule; never treats bad output as "no findings".
function runAudit(appDir) {
  const r = spawnSync(WIN ? `npm ${AUDIT_ARGS.join(' ')}` : 'npm', WIN ? [] : AUDIT_ARGS, {
    cwd: appDir, env: auditEnv(), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'inherit'], shell: WIN,
  });
  if (r.error) fail(`could not run npm: ${r.error.message}`);
  if (r.status !== 0 && r.status !== 1) fail(`npm audit exited ${r.status ?? `by signal ${r.signal}`}`);
  let report;
  try { report = JSON.parse(r.stdout); } catch { fail('npm audit output is not valid JSON'); }
  if (!isObject(report)) fail('npm audit output is not a JSON object');
  if (report.auditReportVersion !== 2) fail(`auditReportVersion is ${JSON.stringify(report.auditReportVersion)}, expected 2`);
  if (!isObject(report.vulnerabilities)) fail('"vulnerabilities" is missing or not an object');
  if (Object.keys(report.vulnerabilities).length === 0 && report.metadata?.vulnerabilities?.total !== 0) {
    fail('"vulnerabilities" is empty but metadata.vulnerabilities.total is not 0');
  }
  return report;
}

// The one key extraction, shared by both modes: sorted top-level keys of "vulnerabilities".
const extractKeys = (report) => Object.keys(report.vulnerabilities).sort(byteOrder);

function readBaseline(file) {
  if (!existsSync(file)) fail(`baseline ${file} does not exist`);
  const text = readFileSync(file, 'utf8').replace(/\r\n/g, '\n'); // a Windows checkout may be CRLF
  if (text === '') return [];
  if (text.includes('\r') || !text.endsWith('\n')) fail(`baseline ${file}: bad line ending`);
  const names = text.slice(0, -1).split('\n');
  if (names.some((n) => n === '' || n !== n.trim())) fail(`baseline ${file}: empty or padded line`);
  if (new Set(names).size !== names.length) fail(`baseline ${file}: duplicate name`);
  return names;
}

function main(argv) {
  const [mode, appArg, ...rest] = argv;
  if (!['generate', 'check'].includes(mode) || !appArg || rest.length) {
    fail('usage: node scripts/npm-audit-baseline.mjs <generate|check> <app-dir>');
  }
  const appDir = path.resolve(appArg);
  if (!existsSync(path.join(appDir, 'package-lock.json'))) fail(`${appDir} has no package-lock.json`);
  const baselineFile = path.join(appDir, BASELINE);
  if (mode === 'generate') {
    if (process.env.GITHUB_ACTIONS === 'true') fail('generate never runs in CI; CI is check-only');
    const keys = extractKeys(runAudit(appDir));
    writeFileSync(baselineFile, keys.map((k) => `${k}\n`).join(''));
    console.log(`wrote ${baselineFile} (${keys.length} names)`);
    for (const k of keys) console.log(`  ${k}`);
    return 0;
  }

  const baseline = new Set(readBaseline(baselineFile)); // missing baseline: exit 2 before any network
  const report = runAudit(appDir);
  const fresh = extractKeys(report);
  const extra = fresh.filter((k) => !baseline.has(k));
  const unchanged = fresh.filter((k) => baseline.has(k));
  const missing = [...baseline].filter((k) => !fresh.includes(k)).sort(byteOrder);
  const sev = (k) => (typeof report.vulnerabilities[k]?.severity === 'string' ? report.vulnerabilities[k].severity : 'unknown');
  const list = (title, names, withSev) => {
    console.log(`${title} (${names.length}):`);
    for (const k of names) console.log(withSev ? `  ${k}  severity=${sev(k)} (informational)` : `  ${k}`);
  };
  const gha = process.env.GITHUB_ACTIONS === 'true';
  console.log(`npm audit baseline check: ${appDir}`);
  list('EXTRA - flagged now, not in the baseline', extra, true);
  list('UNCHANGED - flagged now and in the baseline', unchanged, true);
  list('MISSING - in the baseline, no longer flagged', missing, false);
  if (missing.length) {
    const msg = `${missing.length} baseline name(s) no longer flagged: ${missing.join(', ')}. Refresh via the owner-invoked generate job.`;
    console.log(gha ? `::warning title=npm audit baseline::${msg}` : `WARNING: ${msg}`);
  }
  if (extra.length) {
    const msg = `${extra.length} flagged name(s) not in ${BASELINE}: ${extra.join(', ')}`;
    console.log(gha ? `::error title=npm audit baseline::${msg}` : `FAIL: ${msg}`);
    return 1;
  }
  console.log('OK: no flagged name outside the baseline');
  return 0;
}

try {
  process.exitCode = main(process.argv.slice(2));
} catch (e) {
  console.error(`npm-audit-baseline: ${e instanceof Fatal ? e.message : e?.stack ?? e}`);
  process.exitCode = 2;
}
