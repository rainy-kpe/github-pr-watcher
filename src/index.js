const fs = require("fs");
const path = require("path");
const { exec } = require("child_process");
const dotenv = require("dotenv");
const notifier = require("node-notifier");
const { Octokit } = require("@octokit/rest");

dotenv.config();

const DEBUG = process.argv.includes("--debug");

// In-memory ETag cache: cacheKey -> { etag, data }
const etagCache = new Map();

async function fetchWithETag(octokit, endpoint, params, cacheKey) {
  const cached = etagCache.get(cacheKey);
  const requestParams = {
    ...params,
    per_page: 100,
    headers: cached?.etag ? { "If-None-Match": cached.etag } : {},
  };

  let firstResponse;
  try {
    firstResponse = await octokit.request(endpoint, requestParams);
  } catch (error) {
    if (error.status === 304 && cached) {
      return cached.data;
    }
    throw error;
  }

  let allData = Array.isArray(firstResponse.data) ? [...firstResponse.data] : [];
  const etag = firstResponse.headers?.etag || null;

  let linkHeader = firstResponse.headers?.link || "";
  let page = 2;
  while (linkHeader.includes('rel="next"')) {
    const nextResponse = await octokit.request(endpoint, {
      ...params,
      per_page: 100,
      page,
    });
    allData = [...allData, ...nextResponse.data];
    linkHeader = nextResponse.headers?.link || "";
    page++;
  }

  etagCache.set(cacheKey, { etag, data: allData });
  return allData;
}

function getRequiredEnv(name) {
  const value = process.env[name];
  if (!value || !value.trim()) {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return value.trim();
}

function getGitHubToken() {
  const token = (process.env.GITHUB_TOKEN || "").trim();
  const classicToken = (process.env.GITHUB_CLASSIC_TOKEN || "").trim();

  if (token) {
    return token;
  }

  if (classicToken) {
    return classicToken;
  }

  throw new Error(
    "Missing GitHub token. Set GITHUB_TOKEN or GITHUB_CLASSIC_TOKEN.",
  );
}

function getTokenKind(token) {
  if (token.startsWith("github_pat_")) {
    return "fine-grained";
  }
  if (token.startsWith("ghp_")) {
    return "classic";
  }
  return "unknown";
}

function parseRepositories(raw) {
  return raw
    .split(",")
    .map((x) => x.trim())
    .filter(Boolean)
    .map((repo) => {
      const [owner, name] = repo.split("/");
      if (!owner || !name) {
        throw new Error(`Invalid repository format: ${repo}. Use owner/repo.`);
      }
      return { owner, repo: name, key: `${owner}/${name}` };
    });
}

function ensureDirectoryForFile(filePath) {
  const dir = path.dirname(filePath);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
}

function loadState(stateFilePath) {
  if (!fs.existsSync(stateFilePath)) {
    return { repos: {} };
  }

  try {
    const data = fs.readFileSync(stateFilePath, "utf8");
    const parsed = JSON.parse(data);
    if (!parsed || typeof parsed !== "object" || !parsed.repos) {
      return { repos: {} };
    }
    return parsed;
  } catch (error) {
    console.warn(`Could not read state file ${stateFilePath}:`, error.message);
    return { repos: {} };
  }
}

function saveState(stateFilePath, state) {
  ensureDirectoryForFile(stateFilePath);
  fs.writeFileSync(stateFilePath, JSON.stringify(state, null, 2), "utf8");
}

function ensurePlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value
    : {};
}

function createNormalizedRepoState(rawRepoState) {
  const raw = ensurePlainObject(rawRepoState);
  return {
    initialized: Boolean(raw.initialized),
    lastCheckedAt: raw.lastCheckedAt || null,
    openPrs: ensurePlainObject(raw.openPrs),
    notifiedClosedPrs: ensurePlainObject(raw.notifiedClosedPrs),
    latestCommentCreatedAtByPr: ensurePlainObject(
      raw.latestCommentCreatedAtByPr,
    ),
    notifiedCommentIdsByPr: ensurePlainObject(raw.notifiedCommentIdsByPr),
    latestApprovalSubmittedAtByPr: ensurePlainObject(
      raw.latestApprovalSubmittedAtByPr,
    ),
    notifiedApprovalReviewIdsByPr: ensurePlainObject(
      raw.notifiedApprovalReviewIdsByPr,
    ),
  };
}

function openUrlInBrowser(url) {
  if (!url || !url.trim()) {
    return;
  }

  const safeUrl = url.replace(/"/g, '""');
  const commandByPlatform = {
    win32: `start "" "${safeUrl}"`,
    darwin: `open "${safeUrl}"`,
    linux: `xdg-open "${safeUrl}"`,
  };

  const command =
    commandByPlatform[process.platform] || commandByPlatform.win32;
  exec(command, (error) => {
    if (error) {
      console.warn(`Could not open browser for URL ${url}:`, error.message);
    }
  });
}

function notify(appName, title, message, urlToOpen) {
  notifier.notify(
    {
      title,
      message,
      appName,
      wait: Boolean(urlToOpen),
      timeout: 5,
    },
    (error, response, metadata) => {
      if (error || !urlToOpen) {
        return;
      }

      const clickedByResponse =
        String(response || "").toLowerCase() === "activate";
      const activationType = metadata && metadata.activationType;
      const clickedByMetadata =
        activationType === "contentsClicked" ||
        activationType === "actionButtonClicked";

      if (clickedByResponse || clickedByMetadata) {
        openUrlInBrowser(urlToOpen);
      }
    },
  );

  console.log(`[NOTIFY] ${title} - ${message}`);
}

function toIsoOrNull(value) {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function trimText(value, maxLength) {
  const text = (value || "").replace(/\s+/g, " ").trim();
  if (text.length <= maxLength) {
    return text;
  }
  return `${text.slice(0, maxLength - 3)}...`;
}

function filterItemsAfter(items, dateFieldName, sinceIso) {
  if (!sinceIso) {
    return items;
  }

  const sinceMs = Date.parse(sinceIso);
  if (!Number.isFinite(sinceMs)) {
    return items;
  }

  return items.filter((item) => {
    const itemDate = item[dateFieldName];
    if (!itemDate) {
      return false;
    }
    const itemMs = Date.parse(itemDate);
    return Number.isFinite(itemMs) && itemMs > sinceMs;
  });
}

function hasActiveApproval(reviews) {
  const latestReviewByUser = {};

  for (const review of reviews) {
    const user = review.user || "unknown";
    const existing = latestReviewByUser[user];
    if (!existing) {
      latestReviewByUser[user] = review;
      continue;
    }

    const reviewMs = Date.parse(review.submitted_at || "");
    const existingMs = Date.parse(existing.submitted_at || "");
    const reviewTs = Number.isFinite(reviewMs) ? reviewMs : -1;
    const existingTs = Number.isFinite(existingMs) ? existingMs : -1;

    if (reviewTs >= existingTs) {
      latestReviewByUser[user] = review;
    }
  }

  return Object.values(latestReviewByUser).some(
    (review) => review.state === "APPROVED",
  );
}

async function fetchOpenPullRequests(octokit, owner, repo) {
  const prs = await fetchWithETag(
    octokit,
    "GET /repos/{owner}/{repo}/pulls",
    { owner, repo, state: "open" },
    `${owner}/${repo}:openPrs`,
  );

  return prs.map((pr) => ({
    number: pr.number,
    title: pr.title,
    html_url: pr.html_url,
    created_at: toIsoOrNull(pr.created_at),
    updated_at: toIsoOrNull(pr.updated_at),
    issue_comment_count: Number(pr.comments || 0),
    review_comment_count: Number(pr.review_comments || 0),
  }));
}

async function fetchRecentlyClosedPullRequests(octokit, owner, repo, sinceIso) {
  const prs = await octokit.paginate(octokit.pulls.list, {
    owner,
    repo,
    state: "closed",
    sort: "updated",
    direction: "desc",
    per_page: 100,
  });

  const sinceMs = sinceIso ? Date.parse(sinceIso) : null;

  return prs
    .filter((pr) => pr.closed_at)
    .filter((pr) => {
      if (!sinceMs) {
        return true;
      }
      const closedMs = Date.parse(pr.closed_at);
      return Number.isFinite(closedMs) && closedMs > sinceMs;
    })
    .map((pr) => ({
      number: pr.number,
      title: pr.title,
      html_url: pr.html_url,
      closed_at: toIsoOrNull(pr.closed_at),
    }));
}

async function fetchIssueComments(octokit, owner, repo, issueNumber) {
  const comments = await fetchWithETag(
    octokit,
    "GET /repos/{owner}/{repo}/issues/{issue_number}/comments",
    { owner, repo, issue_number: issueNumber },
    `${owner}/${repo}:pr:${issueNumber}:issueComments`,
  );

  return comments.map((comment) => ({
    id: comment.id,
    created_at: toIsoOrNull(comment.created_at),
    user: comment.user ? comment.user.login : "unknown",
    body: comment.body || "",
    type: "issue",
  }));
}

async function fetchReviewComments(octokit, owner, repo, pullNumber) {
  const comments = await fetchWithETag(
    octokit,
    "GET /repos/{owner}/{repo}/pulls/{pull_number}/comments",
    { owner, repo, pull_number: pullNumber },
    `${owner}/${repo}:pr:${pullNumber}:reviewComments`,
  );

  return comments.map((comment) => ({
    id: comment.id,
    created_at: toIsoOrNull(comment.created_at),
    user: comment.user ? comment.user.login : "unknown",
    body: comment.body || "",
    type: "review",
  }));
}

async function fetchPullReviews(octokit, owner, repo, pullNumber) {
  const reviews = await fetchWithETag(
    octokit,
    "GET /repos/{owner}/{repo}/pulls/{pull_number}/reviews",
    { owner, repo, pull_number: pullNumber },
    `${owner}/${repo}:pr:${pullNumber}:reviews`,
  );

  return reviews.map((review) => ({
    id: review.id,
    state: review.state,
    submitted_at: toIsoOrNull(review.submitted_at || review.created_at),
    user: review.user ? review.user.login : "unknown",
  }));
}

async function checkRepository({
  octokit,
  repoConfig,
  state,
  nowIso,
  notifyOnStartup,
  appName,
}) {
  const repoKey = repoConfig.key;
  const repoState = createNormalizedRepoState(state.repos[repoKey]);

  const previousCheck = repoState.lastCheckedAt;

  const openPrs = await fetchOpenPullRequests(
    octokit,
    repoConfig.owner,
    repoConfig.repo,
  );

  const openPrMap = {};
  for (const pr of openPrs) {
    openPrMap[String(pr.number)] = pr;
  }

  const shouldNotify = repoState.initialized || notifyOnStartup;

  if (shouldNotify) {
    for (const pr of openPrs) {
      const prKey = String(pr.number);
      if (!repoState.openPrs[prKey]) {
        notify(
          appName,
          `New PR in ${repoKey}`,
          `#${pr.number}: ${trimText(pr.title, 100)}`,
          pr.html_url,
        );
      }
    }
  }

  if (shouldNotify) {
    const recentlyClosedPrs = await fetchRecentlyClosedPullRequests(
      octokit,
      repoConfig.owner,
      repoConfig.repo,
      previousCheck,
    );

    for (const pr of recentlyClosedPrs) {
      const prKey = String(pr.number);
      const alreadyNotifiedAt = repoState.notifiedClosedPrs[prKey];
      const closedAt = pr.closed_at || nowIso;

      if (
        !alreadyNotifiedAt ||
        Date.parse(closedAt) > Date.parse(alreadyNotifiedAt)
      ) {
        notify(
          appName,
          `PR closed in ${repoKey}`,
          `#${pr.number}: ${trimText(pr.title, 100)}`,
          pr.html_url,
        );
        repoState.notifiedClosedPrs[prKey] = closedAt;
      }
    }
  }

  for (const pr of openPrs) {
    const prKey = String(pr.number);
    const latestKnownComment =
      repoState.latestCommentCreatedAtByPr[prKey] ||
      previousCheck ||
      pr.created_at ||
      null;

    const allIssueComments = await fetchIssueComments(
      octokit,
      repoConfig.owner,
      repoConfig.repo,
      pr.number,
    );

    const allReviewComments = await fetchReviewComments(
      octokit,
      repoConfig.owner,
      repoConfig.repo,
      pr.number,
    );

    const allComments = [...allIssueComments, ...allReviewComments];
    const newComments = filterItemsAfter(
      allComments,
      "created_at",
      latestKnownComment,
    );

    const allReviews = await fetchPullReviews(
      octokit,
      repoConfig.owner,
      repoConfig.repo,
      pr.number,
    );

    const isApproved = hasActiveApproval(allReviews);
    const prLink = `\x1b]8;;${pr.html_url}\x1b\\${repoKey} #${pr.number}\x1b]8;;\x1b\\`;
    console.log(
      `${isApproved ? "[APPROVED]" : "[WAITING]"} | ${prLink} | ${trimText(pr.title, 120)}`,
    );

    if (!repoState.notifiedCommentIdsByPr[prKey]) {
      repoState.notifiedCommentIdsByPr[prKey] = {};
    }

    for (const comment of newComments) {
      const commentId = `${comment.type}:${String(comment.id)}`;
      if (!comment.id) {
        continue;
      }

      if (!shouldNotify) {
        repoState.notifiedCommentIdsByPr[prKey][commentId] = true;
        continue;
      }

      if (!repoState.notifiedCommentIdsByPr[prKey][commentId]) {
        notify(
          appName,
          `New comment in ${repoKey} PR #${pr.number}`,
          `${comment.user}: ${trimText(comment.body, 120)}`,
          pr.html_url,
        );
        repoState.notifiedCommentIdsByPr[prKey][commentId] = true;
      }
    }

    const newestCommentIso = newComments
      .map((c) => c.created_at)
      .filter(Boolean)
      .sort()
      .at(-1);

    if (newestCommentIso) {
      const oldIso = repoState.latestCommentCreatedAtByPr[prKey];
      if (!oldIso || Date.parse(newestCommentIso) > Date.parse(oldIso)) {
        repoState.latestCommentCreatedAtByPr[prKey] = newestCommentIso;
      }
    }

    const latestApprovalKnown =
      repoState.latestApprovalSubmittedAtByPr[prKey] ||
      previousCheck ||
      pr.created_at ||
      null;

    const newApprovedReviews = filterItemsAfter(
      allReviews.filter((review) => review.state === "APPROVED"),
      "submitted_at",
      latestApprovalKnown,
    );

    if (!repoState.notifiedApprovalReviewIdsByPr[prKey]) {
      repoState.notifiedApprovalReviewIdsByPr[prKey] = {};
    }

    for (const review of newApprovedReviews) {
      const reviewId = String(review.id);
      if (!review.id) {
        continue;
      }

      if (!shouldNotify) {
        repoState.notifiedApprovalReviewIdsByPr[prKey][reviewId] = true;
        continue;
      }

      if (!repoState.notifiedApprovalReviewIdsByPr[prKey][reviewId]) {
        notify(
          appName,
          `PR approved in ${repoKey}`,
          `${review.user} approved #${pr.number}: ${trimText(pr.title, 90)}`,
          pr.html_url,
        );
        repoState.notifiedApprovalReviewIdsByPr[prKey][reviewId] = true;
      }
    }

    const newestApprovalIso = newApprovedReviews
      .map((r) => r.submitted_at)
      .filter(Boolean)
      .sort()
      .at(-1);

    if (newestApprovalIso) {
      const oldIso = repoState.latestApprovalSubmittedAtByPr[prKey];
      if (!oldIso || Date.parse(newestApprovalIso) > Date.parse(oldIso)) {
        repoState.latestApprovalSubmittedAtByPr[prKey] = newestApprovalIso;
      }
    }
  }

  for (const prNumber of Object.keys(repoState.latestCommentCreatedAtByPr)) {
    if (!openPrMap[prNumber]) {
      delete repoState.latestCommentCreatedAtByPr[prNumber];
      delete repoState.notifiedCommentIdsByPr[prNumber];
    }
  }

  for (const prNumber of Object.keys(repoState.latestApprovalSubmittedAtByPr)) {
    if (!openPrMap[prNumber]) {
      delete repoState.latestApprovalSubmittedAtByPr[prNumber];
      delete repoState.notifiedApprovalReviewIdsByPr[prNumber];
    }
  }

  repoState.openPrs = openPrMap;
  repoState.initialized = true;
  repoState.lastCheckedAt = nowIso;

  state.repos[repoKey] = repoState;
}

async function runOnce(config, state) {
  const noop = () => {};
  const octokitOptions = {
    auth: config.githubToken,
    log: DEBUG
      ? console
      : {
          debug: noop,
          info: noop,
          warn: noop,
          error: console.error,
        },
  };
  if (config.githubApiBaseUrl) {
    octokitOptions.baseUrl = config.githubApiBaseUrl;
  }

  const octokit = new Octokit(octokitOptions);

  try {
    await octokit.users.getAuthenticated();
  } catch (error) {
    const status = error && error.status ? ` (status ${error.status})` : "";
    throw new Error(
      "GitHub authentication failed" +
        `${status}. Check GITHUB_TOKEN/GITHUB_CLASSIC_TOKEN and GITHUB_API_BASE_URL.`,
    );
  }

  const nowIso = new Date().toISOString();

  for (const repoConfig of config.repositories) {
    try {
      await checkRepository({
        octokit,
        repoConfig,
        state,
        nowIso,
        notifyOnStartup: config.notifyOnStartup,
        appName: config.appName,
      });
    } catch (error) {
      if (error && error.status === 404) {
        console.error(
          `Failed to process ${repoConfig.key}: 404 Not Found. ` +
            "The repository path may be wrong, token may not have access, or API host may be incorrect. " +
            "Check REPOSITORIES, token repository permissions, and GITHUB_API_BASE_URL.",
        );
        continue;
      }

      console.error(
        `Failed to process ${repoConfig.key}:`,
        error.status || "",
        error.message,
      );
    }
  }
}

function loadConfig() {
  const githubToken = getGitHubToken();
  const repositoriesRaw = getRequiredEnv("REPOSITORIES");
  const repositories = parseRepositories(repositoriesRaw);

  const pollIntervalSeconds = Number(process.env.POLL_INTERVAL_SECONDS || "30");
  if (!Number.isFinite(pollIntervalSeconds) || pollIntervalSeconds < 10) {
    throw new Error("POLL_INTERVAL_SECONDS must be a number >= 10.");
  }

  const stateFile = process.env.STATE_FILE
    ? path.resolve(process.env.STATE_FILE)
    : path.resolve("./state/watcher-state.json");

  const notifyOnStartup =
    String(process.env.NOTIFY_ON_STARTUP || "false").toLowerCase() === "true";

  const appName = (process.env.APP_NAME || "GitHub PR Watcher").trim();
  const githubApiBaseUrl = (process.env.GITHUB_API_BASE_URL || "").trim();

  return {
    githubToken,
    githubTokenKind: getTokenKind(githubToken),
    repositories,
    pollIntervalMs: pollIntervalSeconds * 1000,
    stateFile,
    notifyOnStartup,
    appName,
    githubApiBaseUrl,
  };
}

async function main() {
  const config = loadConfig();
  const state = loadState(config.stateFile);

  console.log("Starting GitHub PR watcher with repositories:");
  for (const repo of config.repositories) {
    console.log(`- ${repo.key}`);
  }

  console.log(`Detected token type: ${config.githubTokenKind}`);

  if (config.githubApiBaseUrl) {
    console.log(`Using GitHub API base URL: ${config.githubApiBaseUrl}`);
  }

  const loop = async () => {
    const started = Date.now();
    try {
      await runOnce(config, state);
      saveState(config.stateFile, state);
      const elapsedMs = Date.now() - started;
      console.log(
        `Check complete in ${elapsedMs} ms at ${new Date().toISOString()}`,
      );
    } catch (error) {
      console.error("Watcher loop failed:", error.message);
    }
  };

  await loop();
  setInterval(loop, config.pollIntervalMs);
}

main().catch((error) => {
  console.error("Fatal error:", error.message);
  process.exit(1);
});
