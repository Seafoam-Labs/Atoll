// k6 load test for Atoll's public read paths: in-memory search, AUR-compatible
// RPC, Mongo-backed catalog reads, server-rendered UI pages, and Git Smart HTTP
// fetches. The two paths that are expensive per request (non-default catalog
// sorts and Git fetches) run on arrival-rate schedules so each stays measurable
// on its own instead of hiding in the VU-driven aggregate.
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
// Tunables via env: TARGET, PACKAGES, TERMS, PROVIDES, RELEVANCE_QUERIES,
// RELEVANCE_POOL, RELEVANCE_POOL_SIZE, VUS, GIT_RATE, SORT_RATE,
// RELEVANCE_RATE, UI_VUS, WARMUP, HOLD, COOLDOWN, GIT_DURATION,
// VERIFY_SECURITY.
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
// 0 deregisters the scenario and its thresholds entirely (k6 rejects a zero
// arrival rate), which is what an isolated run of another scenario needs: at
// its 1/s floor git_fetch alone eats much of a single core.
const GIT_RATE = Number(__ENV.GIT_RATE || 2);
const SORT_RATE = Number(__ENV.SORT_RATE || 10);
// Relevance is opt-in: at the default 0 the scenario and its thresholds are not
// registered, so `k6 run loadtest.js` stays byte-identical to the recorded
// compatibility-mode baseline. Raise it to measure the ranked path (Phase 2 gate).
const RELEVANCE_RATE = Number(__ENV.RELEVANCE_RATE || 0);
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

// Relevance is free text, not the legacy comma-batch grammar, so this pool is
// kept separate from TERMS/PROVIDES and may contain spaces (encodeURIComponent
// turns them into %20, which the relevance parser splits back into terms). It
// spans the distinct code paths: exact name, broad prefix, name-vs-metadata,
// interior token, short term, stop word, no-hit, and multi-term coverage.
const RELEVANCE_QUERIES = csv(__ENV.RELEVANCE_QUERIES ||
  "yay,vim,rust,browser,neovim,qt,git,brwose,vim editor,rust browser gui");

// Which pool the relevance scenario draws from:
//   default      RELEVANCE_QUERIES: ten hot queries spanning the code paths
//   adversarial  inputs whose cost used to track the corpus rather than the result
//   unique       a distinct prefix of a real name per request, sampled at setup()
// `default` flatters the path if a cache is ever added; `unique` is what a
// sustained public rate should be derived from.
const RELEVANCE_POOL = (__ENV.RELEVANCE_POOL || "default").toLowerCase();
const UNIQUE_POOL_SIZE = Number(__ENV.RELEVANCE_POOL_SIZE || 500);

// Single-char and two-char prefixes, a stop-word prefix, allowed short terms,
// the maximum-length term, and the maximum term count, with and without hits.
const RELEVANCE_ADVERSARIAL = [
  "a", "l", "li", "lib", "libg", "xz", "7z", "i3", "brwose",
  "z".repeat(256),
  "lib lib- libg libgtk",
  "zzzqqq zzzwww zzzrrr zzzttt zzzuuu zzzvvv zzzxxx zzzzzz",
];

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

// Same sub-1/s handling as the sorted schedule.
const relevanceSchedule =
  RELEVANCE_RATE < 1
    ? { rate: 1, timeUnit: `${Math.round(1 / RELEVANCE_RATE)}s` }
    : { rate: RELEVANCE_RATE, timeUnit: "1s" };

const scenarios = {
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
    // Non-default sorts serve from a cached per-sort name view plus one
    // name-filtered Mongo query per page. Arrival-rate scheduling keeps this
    // formerly unbounded public request measured in isolation.
    executor: "constant-arrival-rate",
    exec: "catalogSorted",
    rate: sortedSchedule.rate,
    timeUnit: sortedSchedule.timeUnit,
    duration: GIT_DURATION,
    // Sized for the default rate: 10 arrivals/s at the measured full-profile
    // p95 (~0.5 s) needs ~5 VUs in flight, so pre-allocate past that instead
    // of letting the generator allocate mid-run and drop arrivals.
    preAllocatedVUs: 6,
    maxVUs: 12,
    gracefulStop: "10s",
  },
  ui: {
    executor: "ramping-vus",
    exec: "ui",
    startVUs: 0,
    stages: rampStages(UI_VUS),
    gracefulRampDown: "10s",
  },
};

const thresholds = {
  http_req_failed: ["rate<0.01"],
  "http_req_duration{scenario:search}": ["p(95)<300"],
  "http_req_duration{scenario:rpc}": ["p(95)<300"],
  "http_req_duration{scenario:catalog}": ["p(95)<600"],
  "http_req_duration{scenario:catalog_sorted}": ["p(95)<600"],
  "http_req_duration{scenario:ui}": ["p(95)<1000"],
  "checks{scenario:search}": ["rate>0.99"],
  "checks{scenario:rpc}": ["rate>0.99"],
  "checks{scenario:catalog}": ["rate>0.99"],
  "checks{scenario:catalog_sorted}": ["rate>0.99"],
  "checks{scenario:ui}": ["rate>0.99"],
};

// Registered only when measured, so an isolated run of another scenario is not
// paying for `git upload-pack` in the background.
if (GIT_RATE > 0) {
  scenarios.git_fetch = {
    executor: "constant-arrival-rate",
    exec: "gitFetch",
    rate: GIT_RATE,
    timeUnit: "1s",
    duration: GIT_DURATION,
    preAllocatedVUs: 4,
    maxVUs: 12,
    gracefulStop: "10s",
  };
  thresholds["http_req_duration{scenario:git_fetch}"] = ["p(95)<2000"];
  thresholds["checks{scenario:git_fetch}"] = ["rate>0.99"];
}

// Registered only when measured, so the default profile stays byte-identical to
// the recorded baseline. A new per-request-expensive public path (a full rank
// over the in-memory index), so it runs on arrival-rate scheduling in isolation
// like catalog_sorted rather than VU-driven inside the aggregate.
if (RELEVANCE_RATE > 0) {
  scenarios.relevance = {
    executor: "constant-arrival-rate",
    exec: "relevance",
    rate: relevanceSchedule.rate,
    timeUnit: relevanceSchedule.timeUnit,
    duration: GIT_DURATION,
    // Sized from the in-process probe (PackageSearchRelevancePerfTests): a full
    // 120k-index rank is ~25-60 ms p95, so even 10 arrivals/s needs well under
    // one VU in flight; pre-allocate past that to avoid mid-run allocation.
    preAllocatedVUs: 4,
    maxVUs: 12,
    gracefulStop: "10s",
  };
  thresholds["http_req_duration{scenario:relevance}"] = ["p(95)<300"];
  thresholds["checks{scenario:relevance}"] = ["rate>0.99"];
}

export const options = {
  scenarios,
  thresholds,
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

  return { packages: PACKAGES, terms: TERMS, provides: PROVIDES, relevance: relevancePool() };
}

function relevancePool() {
  if (RELEVANCE_POOL === "adversarial") return RELEVANCE_ADVERSARIAL;
  if (RELEVANCE_POOL !== "unique") return RELEVANCE_QUERIES;

  const names = sampleIndexNames();
  if (names.length === 0) fail("RELEVANCE_POOL=unique sampled no package names from /v1/search");

  const pool = uniqueRelevancePool(names, UNIQUE_POOL_SIZE);
  if (pool.length < UNIQUE_POOL_SIZE) {
    fail(`RELEVANCE_POOL=unique built ${pool.length} of ${UNIQUE_POOL_SIZE} queries from ${names.length} names`);
  }
  return pool;
}

// Names come from the in-memory index rather than /v1/packages, which is
// Mongo-backed and holds only the seeded pool on the isolated stack.
function sampleIndexNames() {
  const names = new Set();

  for (const term of [...TERMS, ...PROVIDES]) {
    const res = http.get(`${TARGET}/v1/search?query=${encodeURIComponent(term)}&by=words`, {
      tags: { name: "GET /v1/search (relevance pool setup)" },
    });
    if (res.status !== 200) continue;

    const body = res.json();
    if (!Array.isArray(body)) continue;
    for (const pkg of body) if (pkg && pkg.name) names.add(pkg.name);
  }

  return [...names];
}

// Seeded so a relevance-off/on pair ranks the identical query set.
function uniqueRelevancePool(names, size) {
  const rnd = mulberry32(98765);
  const prefix = (name) => name.slice(0, 2 + Math.floor(rnd() * Math.max(1, name.length - 1)));
  const pickName = () => names[Math.floor(rnd() * names.length)];
  const pool = new Set();

  // Bounded so an exhausted prefix space fails the run instead of spinning setup().
  for (let attempt = 0; pool.size < size && attempt < size * 50; attempt++) {
    const name = pickName();
    pool.add(rnd() < 0.2 ? `${prefix(name)} ${prefix(pickName())}` : prefix(name));
  }

  return [...pool];
}

function mulberry32(seed) {
  let state = seed;
  return function () {
    state = (state + 0x6D2B79F5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
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

export function relevance(data) {
  // Free-text ranked retrieval: one full rank over the in-memory index, served
  // as a bare array capped at 50. Distinct tag so it never folds into `search`.
  const query = `${TARGET}/v1/search?query=${encodeURIComponent(pick(data.relevance))}&by=relevance`;
  const res = http.get(query, { tags: { name: "GET /v1/search (relevance)" } });
  check(res, {
    "relevance returns 200": (r) => r.status === 200,
    "relevance returns a JSON array": (r) => Array.isArray(r.json()),
    "relevance caps at 50 rows": (r) => r.json().length <= 50,
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
