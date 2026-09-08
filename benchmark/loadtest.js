// k6 load test for Atoll's public read paths: in-memory search, AUR-compatible
// RPC, Mongo-backed catalog reads, server-rendered UI pages, and Git Smart HTTP
// fetches. Catalog sorting is isolated because non-default sorts currently
// load the full seeded catalog before returning one page.
//
// Local (see docker-compose.yaml next to this file):
//   docker compose up --build -d
//   k6 run loadtest.js
//
// Against a remote deployment (e.g. the Terraform-managed AWS stack):
//   TARGET=https://atoll.example.com k6 run loadtest.js
// The AWS web ACL rate-limits per source IP (terraform/waf.tf); raise
// `waf_rate_limit` before pointing this script at a real deployment.
//
// Tunables via env: TARGET, PACKAGES, TERMS, PROVIDES, VUS, GIT_RATE,
// SORT_RATE, UI_VUS, WARMUP, HOLD, COOLDOWN, GIT_DURATION, VERIFY_SECURITY.
//
// PACKAGES is the request mix, not the corpus: every name-keyed read (RPC,
// detail, versions, UI, Git) and every exact-name search draws from it, while
// catalog-wide paths (word search, paging, sorting) always see the whole
// database. Rotate it with sample-packages.sh against a production-shaped
// corpus, otherwise the same handful of documents stays cache-hot.

import http from "k6/http";
import { check, fail, sleep } from "k6";

let TARGET = __ENV.TARGET || "http://localhost:8080";
while (TARGET.endsWith("/")) TARGET = TARGET.slice(0, -1);
const VUS = Number(__ENV.VUS || 25);
const GIT_RATE = Number(__ENV.GIT_RATE || 2);
const SORT_RATE = Number(__ENV.SORT_RATE || 1);
const UI_VUS = Number(__ENV.UI_VUS || 5);
const VERIFY_SECURITY = __ENV.VERIFY_SECURITY === "true";
const WARMUP = __ENV.WARMUP || "20s";
const HOLD = __ENV.HOLD || "1m";
const COOLDOWN = __ENV.COOLDOWN || "15s";
// Keep this aligned with WARMUP + HOLD + COOLDOWN when changing the stages.
const GIT_DURATION = __ENV.GIT_DURATION || "95s";

const csv = (raw) => raw.split(",").map((s) => s.trim()).filter(Boolean);

// Every default must name a package that still exists upstream: setup() treats
// an unseedable name as fatal. The set spans the corpus's cost envelope on each
// name-keyed path — median ~2 KB documents, plus the stored-content and history
// tail (xpipe-ptb sits at the MaxRevisions cap, duckstation-gpl is the corpus's
// largest package and a split package whose pkgbase differs from its pkgname).
const PACKAGES = csv(__ENV.PACKAGES ||
  "yay,paru,visual-studio-code-bin,slack-desktop,xpipe-ptb,duckstation-gpl");

// Separate pools because the three search modes match differently: `name` is an
// exact pkgname lookup, `words` walks the word index (cost tracks posting-list
// length, so a few hot terms dominate), `provides` matches a provided token.
const TERMS = csv(__ENV.TERMS ||
  "python,linux,data,game,theme,video,audio,electron,java,gtk,font,editor,vim,node," +
  "docker,utils,kernel,nightly,glibc,webkit,bin,git");
const PROVIDES = csv(__ENV.PROVIDES ||
  "vim,nvidia,gcc-libs,sh,wine,chromium,gtk2,bash,jre");

// Real `yay` runs ask for many packages at once and the RPC silently truncates
// batches past 200 args, so tiers stay at or below that ceiling; tiers larger
// than the package pool are skipped rather than sending duplicates.
const RPC_ARG_TIERS = [1, 2, 5, 10, 25, 50, 100, 200];

const rampStages = (vus) => [
  { duration: WARMUP, target: vus },
  { duration: HOLD, target: vus },
  { duration: COOLDOWN, target: 0 },
];

// k6 requires an integer arrival rate; express sub-1/s rates as one request
// every N seconds.
const sortedSchedule =
  SORT_RATE < 1
    ? { rate: 1, timeUnit: `${Math.round(1 / SORT_RATE)}s` }
    : { rate: SORT_RATE, timeUnit: "1s" };

export const options = {
  scenarios: {
    search: {
      executor: "ramping-vus",
      exec: "search",
      startVUs: 0,
      stages: rampStages(VUS),
      gracefulRampDown: "10s",
    },
    rpc: {
      executor: "ramping-vus",
      exec: "rpc",
      startVUs: 0,
      stages: rampStages(VUS),
      gracefulRampDown: "10s",
    },
    catalog: {
      executor: "ramping-vus",
      exec: "catalog",
      startVUs: 0,
      stages: rampStages(Math.max(1, Math.floor(VUS / 2))),
      gracefulRampDown: "10s",
    },
    catalog_sorted: {
      // Any non-default sort materializes the complete catalog before paging.
      // Arrival-rate scheduling keeps this known-expensive public request from
      // being accidentally amplified into a denial-of-service test.
      executor: "constant-arrival-rate",
      exec: "catalogSorted",
      rate: sortedSchedule.rate,
      timeUnit: sortedSchedule.timeUnit,
      duration: GIT_DURATION,
      preAllocatedVUs: 2,
      maxVUs: 6,
      gracefulStop: "10s",
    },
    ui: {
      executor: "ramping-vus",
      exec: "ui",
      startVUs: 0,
      stages: rampStages(UI_VUS),
      gracefulRampDown: "10s",
    },
    git_fetch: {
      executor: "constant-arrival-rate",
      exec: "gitFetch",
      rate: GIT_RATE,
      timeUnit: "1s",
      duration: GIT_DURATION,
      preAllocatedVUs: 4,
      maxVUs: 12,
      gracefulStop: "10s",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    "http_req_duration{scenario:search}": ["p(95)<300"],
    "http_req_duration{scenario:rpc}": ["p(95)<300"],
    "http_req_duration{scenario:catalog}": ["p(95)<600"],
    "http_req_duration{scenario:catalog_sorted}": ["p(95)<3000"],
    "http_req_duration{scenario:ui}": ["p(95)<1000"],
    "http_req_duration{scenario:git_fetch}": ["p(95)<2000"],
    "checks{scenario:search}": ["rate>0.99"],
    "checks{scenario:rpc}": ["rate>0.99"],
    "checks{scenario:catalog}": ["rate>0.99"],
    "checks{scenario:catalog_sorted}": ["rate>0.99"],
    "checks{scenario:ui}": ["rate>0.99"],
    "checks{scenario:git_fetch}": ["rate>0.99"],
  },
  summaryTrendStats: ["avg", "min", "med", "p(90)", "p(95)", "max"],
};

export function setup() {
  waitForHealth();

  // 409 = already present, so repeat and rotated runs stay cheap. It is still
  // checked explicitly below, but k6 must not count it as a failed request:
  // that would add one failure per package to http_req_failed and eventually
  // breach the 1% threshold purely by growing the pool.
  const seedResponse = http.expectedStatuses(201, 409);

  for (const name of PACKAGES) {
    const res = http.post(`${TARGET}/v1/packages/${encodeURIComponent(name)}/seed`, null, {
      tags: { name: "POST /v1/packages/{name}/seed (setup)" },
      responseCallback: seedResponse,
    });
    if (res.status !== 201 && res.status !== 409) {
      fail(`seeding '${name}' failed: HTTP ${res.status} ${res.body}`);
    }

    const packageResult = http.get(`${TARGET}/rpc/v5/info/${encodeURIComponent(name)}`, {
      tags: { name: "GET /rpc/v5/info/{name} (setup)" },
    });
    const resultCount = packageResult.json("resultcount");
    if (packageResult.status !== 200 || !Number.isInteger(resultCount) || resultCount < 1) {
      fail(`seeded '${name}' is not queryable through the AUR RPC`);
    }

    if (VERIFY_SECURITY) verifyServableHead(name);
  }

  return { packages: PACKAGES, terms: TERMS, provides: PROVIDES };
}

function waitForHealth() {
  // Startup downloads and parses the AUR metadata dump before serving, so a
  // cold stack needs a generous window; the compose healthcheck usually gets
  // there first.
  const deadline = Date.now() + 300_000;
  while (Date.now() < deadline) {
    const res = http.get(`${TARGET}/health`, { tags: { name: "GET /health (setup)" } });
    if (res.status === 200) return;
    sleep(2);
  }
  fail(`${TARGET} did not become healthy within 5m`);
}

function verifyServableHead(name) {
  const res = http.get(`${TARGET}/v1/packages/${encodeURIComponent(name)}/security`, {
    tags: { name: "GET /v1/packages/{name}/security (setup)" },
  });
  const head = res.json("headRevisionId");
  const revisions = res.json("revisions");
  const headScan = Array.isArray(revisions) && revisions.find((scan) => scan.revisionId === head);
  if (res.status !== 200 || headScan?.status !== "Verified") {
    fail(`'${name}' is not security-verified; restore a scan-complete benchmark corpus first`);
  }
}

const pick = (items) => items[Math.floor(Math.random() * items.length)];

// Distinct sample without replacement (partial Fisher-Yates).
function sampleDistinct(items, n) {
  const count = Math.min(n, items.length);
  const pool = items.slice();
  for (let i = 0; i < count; i++) {
    const j = i + Math.floor(Math.random() * (pool.length - i));
    [pool[i], pool[j]] = [pool[j], pool[i]];
  }
  return pool.slice(0, count);
}

export function search(data) {
  const pools = { name: data.packages, words: data.terms, provides: data.provides };
  const by = pick(Object.keys(pools));
  const query = `${TARGET}/v1/search?query=${encodeURIComponent(pick(pools[by]))}&by=${by}`;
  const res = http.get(query, { tags: { name: "GET /v1/search" } });
  check(res, {
    "search returns 200": (r) => r.status === 200,
    "search returns a JSON array": (r) => Array.isArray(r.json()),
  });
}

export function rpc(data) {
  const tiers = RPC_ARG_TIERS.filter((n) => n <= data.packages.length);
  const args = sampleDistinct(data.packages, pick(tiers));
  const body = "v=5&type=multiinfo" +
    args.map((a) => `&arg[]=${encodeURIComponent(a)}`).join("");
  const res = http.post(`${TARGET}/rpc`, body, {
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    tags: { name: "POST /rpc multiinfo" },
  });
  check(res, {
    "rpc returns 200": (r) => r.status === 200,
    // Errors would come back as 200 with type="error"; a well-formed
    // multiinfo echoes the request type.
    "rpc reports multiinfo": (r) => r.json("type") === "multiinfo",
    // Guards against the server dropping args, which would shrink the measured
    // payload without changing latency.
    "rpc answers every requested package": (r) => r.json("resultcount") === args.length,
  });

  if (Math.random() < 0.1) {
    const suggest = http.get(
      `${TARGET}/rpc/v5/suggest/${encodeURIComponent(pick(data.terms))}`,
      { tags: { name: "GET /rpc/v5/suggest" } },
    );
    check(suggest, { "suggest returns 200": (r) => r.status === 200 });
  }
}

export function catalog(data) {
  const page = 1 + Math.floor(Math.random() * 200);
  const index = http.get(`${TARGET}/v1/packages?page=${page}&limit=50`, {
    tags: { name: "GET /v1/packages (index page)" },
  });
  check(index, { "index page returns 200": (r) => r.status === 200 });

  const name = pick(data.packages);
  if (Math.random() < 0.3) {
    const versions = http.get(`${TARGET}/v1/packages/${encodeURIComponent(name)}/versions`, {
      tags: { name: "GET /v1/packages/{name}/versions" },
    });
    check(versions, { "versions return 200": (r) => r.status === 200 });
  }

  if (Math.random() < 0.2) {
    // Not a metadata read: this endpoint returns the head revision's file
    // contents, so the response is the whole package (up to ~15 MB for the
    // largest one). Which names are in the pool drives this scenario's cost.
    const detail = http.get(`${TARGET}/v1/packages/${encodeURIComponent(name)}`, {
      tags: { name: "GET /v1/packages/{name}" },
    });
    check(detail, { "package detail returns 200": (r) => r.status === 200 });
  }
}

export function catalogSorted() {
  const sortBy = pick(["votes", "popularity", "version"]);
  const order = pick(["asc", "desc"]);
  const res = http.get(`${TARGET}/v1/packages?page=1&limit=50&sortBy=${sortBy}&order=${order}`, {
    tags: { name: "GET /v1/packages (full catalog sort)" },
  });
  check(res, {
    "sorted index returns 200": (r) => r.status === 200,
    "sorted index contains a page": (r) => Array.isArray(r.json("items")),
  });
}

export function ui(data) {
  const index = http.get(`${TARGET}/`, { tags: { name: "GET / (catalog UI)" } });
  check(index, {
    "catalog UI returns 200": (r) => r.status === 200,
    "catalog UI renders HTML": (r) => r.body.includes("<html"),
  });

  const packagePage = http.get(`${TARGET}/package/${encodeURIComponent(pick(data.packages))}`, {
    tags: { name: "GET /package/{name} (package UI)" },
  });
  check(packagePage, {
    "package UI returns 200": (r) => r.status === 200,
    "package UI renders HTML": (r) => r.body.includes("<html"),
  });
}

export function gitFetch(data) {
  const url = `${TARGET}/${encodeURIComponent(pick(data.packages))}.git`;
  const adv = http.get(`${url}/info/refs?service=git-upload-pack`, {
    headers: { Accept: "*/*" },
    tags: { name: "GET {name}.git/info/refs" },
  });
  const advertised = check(adv, {
    "advertisement returns 200": (r) => r.status === 200,
    "advertisement lists refs": (r) => r.body.includes("refs/heads"),
  });
  if (!advertised) return;

  const oid = adv.body.match(/([0-9a-f]{40})\srefs/)?.[1];
  if (!oid) return;

  // Minimal one-shot upload-pack negotiation: want the advertised tip, flush,
  // done, flush — equivalent to fetching an empty local repo.
  const req = pktLine(`want ${oid} multi_ack agent=k6\n`) + FLUSH + pktLine("done\n") + FLUSH;
  const res = http.post(`${url}/git-upload-pack`, req, {
    headers: { "Content-Type": "application/x-git-upload-pack-request" },
    tags: { name: "POST {name}.git/git-upload-pack" },
  });
  check(res, {
    "upload-pack returns 200": (r) => r.status === 200,
    "upload-pack streams a pack": (r) =>
      r.headers["Content-Type"] === "application/x-git-upload-pack-result" &&
      r.body.length > 0,
  });
}

const FLUSH = "0000";

// Git pkt-line: 4 hex digits encoding the total length, including themselves.
function pktLine(payload) {
  return ((4 + payload.length) >>> 0).toString(16).padStart(4, "0") + payload;
}
