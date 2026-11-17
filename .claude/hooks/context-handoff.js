#!/usr/bin/env node
// Stop hook: ask for a handoff when the main chat approaches 400K tokens.
//
// Fires at 375K, then again every 25K tokens after that. It does not abort in-progress work:
// it lets the current task finish, then asks Claude to update STATUS.md and write a handoff
// document with the project's `handoff` skill (.claude/skills/handoff, from mattpocock/skills),
// then to post a short handoff message for a fresh session. Per-session state is kept in
// $CONTEXT_HANDOFF_STATE (default: <CLAUDE_CONFIG_DIR or ~/.claude>/context-handoff-state.json).
//
// Reads the hook payload (JSON) on stdin. Always exits 0; a rotation request is a JSON
// decision "block" on stdout (the reason goes to the model).
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const LIMIT = 400_000;
const FIRST = 375_000; // "approaching" the limit: leaves room for the handoff itself
const STEP = 25_000;
const MARKER = 'Handoff from the previous chat';
const STATE_TTL_MS = 14 * 86400e3;

function stateFile(env = process.env) {
  if (env.CONTEXT_HANDOFF_STATE) return env.CONTEXT_HANDOFF_STATE;
  const configDir = env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude');
  return path.join(configDir, 'context-handoff-state.json');
}

// Last 4 MB of the transcript, split into JSONL lines.
function tailLines(file) {
  const size = fs.statSync(file).size;
  const len = Math.min(size, 4 * 1024 * 1024);
  const buf = Buffer.alloc(len);
  const fd = fs.openSync(file, 'r');
  fs.readSync(fd, buf, 0, len, size - len);
  fs.closeSync(fd);
  return buf.toString('utf8').split('\n');
}

// Context size of the last main-chat (non-sidechain) assistant turn.
function lastContextTokens(lines) {
  for (let i = lines.length - 1; i >= 0; i--) {
    if (!lines[i].includes('"usage"')) continue;
    let j;
    try { j = JSON.parse(lines[i]); } catch { continue; }
    if (j.isSidechain || !j.message || !j.message.usage) continue;
    const u = j.message.usage;
    const total = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0)
      + (u.cache_creation_input_tokens || 0) + (u.output_tokens || 0);
    if (total > 0) return total;
  }
  return 0;
}

// True if Claude already emitted the handoff block after `sinceMs`.
function handoffGivenSince(lines, sinceMs) {
  for (let i = lines.length - 1; i >= 0; i--) {
    if (!lines[i].includes(MARKER)) continue;
    let j;
    try { j = JSON.parse(lines[i]); } catch { continue; }
    if (j.isSidechain || j.type !== 'assistant' || !j.message) continue;
    if ((Date.parse(j.timestamp || '') || 0) < sinceMs) return false;
    const c = j.message.content;
    const texts = Array.isArray(c) ? c.filter((x) => x.type === 'text').map((x) => x.text || '') : [String(c || '')];
    if (texts.some((t) => t.includes(MARKER))) return true;
  }
  return false;
}

function handoffReason(tokens) {
  const k = Math.round(tokens / 1000);
  return `Context check: this session is at ~${k}K tokens (the limit is ${LIMIT / 1000}K).

If work is in progress, finish the current step first (don't start new work). Then:
1. Update STATUS.md (state right now, what changed, open items), as CLAUDE.md describes. Don't commit unless the user asked for commits.
2. Call the Skill tool with skill "handoff", passing as its argument what the next session should focus on. It writes the handoff document to %TEMP%.
3. Reply with a short message starting with "${MARKER} (about ${k}K tokens)" that gives the handoff document's full path, the active branch, the immediate next action, and a one-line prompt the user can paste into a fresh chat.`;
}

// Return the Stop-hook output object, or null to let the stop proceed.
function evaluate(payload, env = process.env) {
  if (!payload || payload.stop_hook_active || !payload.transcript_path || !fs.existsSync(payload.transcript_path)) return null;

  const lines = tailLines(payload.transcript_path);
  const tokens = lastContextTokens(lines);
  if (tokens < FIRST) return null;

  const level = FIRST + Math.floor((tokens - FIRST) / STEP) * STEP;
  const file = stateFile(env);
  let state = {};
  try { state = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { /* first run: empty state */ }

  const id = payload.session_id || payload.transcript_path;
  let s = state[id];
  if (!s || s.level < level) s = state[id] = { level, since: Date.now(), delivered: false };
  s.at = Date.now();
  if (!s.delivered && handoffGivenSince(lines, s.since)) s.delivered = true;

  const cutoff = Date.now() - STATE_TTL_MS;
  for (const key of Object.keys(state)) {
    if ((state[key].at || 0) < cutoff) delete state[key];
  }
  try { fs.writeFileSync(file, JSON.stringify(state, null, 1)); } catch { /* state is best-effort */ }
  if (s.delivered) return null;

  const k = Math.round(tokens / 1000);
  return {
    decision: 'block',
    reason: handoffReason(tokens),
    systemMessage: `Session context is at ~${k}K tokens (limit ${LIMIT / 1000}K). Claude will write a handoff once the current task is done.`,
  };
}

module.exports = { evaluate, lastContextTokens, handoffGivenSince, stateFile };

if (require.main === module) {
  let payload = {};
  try { payload = JSON.parse(fs.readFileSync(0, 'utf8') || '{}'); } catch { /* unparseable: let the stop proceed */ }
  const out = evaluate(payload);
  if (out) process.stdout.write(JSON.stringify(out));
  process.exit(0);
}
