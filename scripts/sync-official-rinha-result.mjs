#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const REPO = 'zanfranceschi/rinha-de-backend-2026';
const AUTHOR = 'jonathanperis';
const OUT_DIR = join(ROOT, 'docs/public/official');
const ISSUE_SEARCH = 'reason:completed rinha/test';

function gh(args) {
  return execFileSync('gh', args, {
    cwd: ROOT,
    encoding: 'utf8',
    env: process.env,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function extractJsonBlocks(markdown) {
  const blocks = [];
  const fenced = /```(?:json)?\s*([\s\S]*?)```/gi;
  let match;

  while ((match = fenced.exec(markdown)) !== null) {
    const candidate = match[1].trim();
    if (candidate.startsWith('{') && candidate.endsWith('}')) {
      blocks.push(candidate);
    }
  }

  if (blocks.length === 0) {
    const start = markdown.indexOf('{');
    const end = markdown.lastIndexOf('}');
    if (start >= 0 && end > start) {
      blocks.push(markdown.slice(start, end + 1));
    }
  }

  return blocks;
}

function parseResultComment(commentBody) {
  for (const block of extractJsonBlocks(commentBody)) {
    try {
      const parsed = JSON.parse(block);
      if (parsed?.['test-results']?.scoring) {
        return parsed;
      }
    } catch {
      // Ignore non-result JSON blocks.
    }
  }

  return null;
}

function scoreRun(parsed, issue, comment) {
  const runtimeInfo = parsed['runtime-info'] ?? null;

  return {
    issue: {
      number: issue.number,
      title: issue.title,
      url: issue.url,
      updated_at: issue.updatedAt,
      closed_at: issue.closedAt,
    },
    comment: {
      url: comment.url,
      created_at: comment.createdAt,
      author: comment.author?.login ?? '',
    },
    repo_url: parsed['repo-url'] ?? '',
    commit: parsed['runtime-info']?.commit ?? '',
    image: findSubmittedImage(runtimeInfo?.images),
    result: parsed['test-results'],
    runtime_info: runtimeSummary(runtimeInfo),
  };
}

function findSubmittedImage(images) {
  if (!images || typeof images !== 'object') {
    return '';
  }

  return Object.keys(images).find((name) => name.includes('jonathanperis/rinha4-back-end-dotnet')) ?? '';
}

function runtimeSummary(runtimeInfo) {
  if (!runtimeInfo || typeof runtimeInfo !== 'object') {
    return null;
  }

  return {
    mem: runtimeInfo.mem ?? null,
    cpu: runtimeInfo.cpu ?? null,
    commit: runtimeInfo.commit ?? '',
    instances_number_ok: runtimeInfo['instances-number-ok?'] ?? null,
    unlimited_services: runtimeInfo['unlimited-services'] ?? null,
  };
}

function main() {
  const issues = JSON.parse(gh([
    'issue',
    'list',
    '--repo',
    REPO,
    '--author',
    AUTHOR,
    '--state',
    'closed',
    '--search',
    ISSUE_SEARCH,
    '--json',
    'number,title,url,updatedAt,closedAt,body',
    '--limit',
    '50',
  ]));

  const runs = [];
  for (const issue of issues) {
    const viewed = JSON.parse(gh([
      'issue',
      'view',
      String(issue.number),
      '--repo',
      REPO,
      '--comments',
      '--json',
      'number,title,url,updatedAt,closedAt,comments',
    ]));

    for (const comment of viewed.comments ?? []) {
      const parsed = parseResultComment(comment.body ?? '');
      if (parsed) {
        runs.push(scoreRun(parsed, viewed, comment));
      }
    }
  }

  runs.sort((left, right) => {
    const rightTime = Date.parse(right.comment.created_at || right.issue.updated_at || right.issue.closed_at || '');
    const leftTime = Date.parse(left.comment.created_at || left.issue.updated_at || left.issue.closed_at || '');
    return rightTime - leftTime;
  });

  if (runs.length === 0) {
    throw new Error(`No official Rinha result comments found for ${AUTHOR}`);
  }

  const synced = new Date().toISOString();
  const latest = {
    synced_at: synced,
    source: {
      repo: REPO,
      author: AUTHOR,
      search: `is:issue state:closed author:${AUTHOR} reason:completed sort:updated-desc`,
    },
    ...runs[0],
  };

  mkdirSync(OUT_DIR, { recursive: true });
  writeFileSync(join(OUT_DIR, 'latest.json'), `${JSON.stringify(latest, null, 2)}\n`);
  writeFileSync(join(OUT_DIR, 'index.json'), `${JSON.stringify({ synced_at: synced, runs }, null, 2)}\n`);

  const score = latest.result.scoring.final_score.toLocaleString('en-US', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  console.log(`Synced official result #${latest.issue.number}: p99=${latest.result.p99} failures=${latest.result.scoring.failure_rate} score=${score}`);
}

main();
