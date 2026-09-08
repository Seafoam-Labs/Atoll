#!/usr/bin/env bash
# Prints a comma-separated package list for loadtest.js's PACKAGES, so each run
# exercises a fresh slice of a production-shaped corpus instead of the same
# cache-hot handful of documents.
#
# Only the local Docker stack is supported (it reads the corpus through the
# benchmark Mongo container); against a remote target, pass a curated PACKAGES.
#
# Usage:
#   PACKAGES="$(./sample-packages.sh 80)" k6 run loadtest.js
#   PACKAGES="duckstation-gpl,$(./sample-packages.sh 80)" k6 run loadtest.js
#
# Env overrides: MONGO_CONTAINER, MONGO_USER, MONGO_PASSWORD, MONGO_DB,
# KEEP (comma-separated names to include in every sample).
set -euo pipefail

COUNT="${1:-80}"
MONGO_CONTAINER="${MONGO_CONTAINER:-benchmark-mongo-1}"
MONGO_USER="${MONGO_USER:-admin}"
MONGO_PASSWORD="${MONGO_PASSWORD:-change_me}"
MONGO_DB="${MONGO_DB:-atoll}"
KEEP="${KEEP:-}"

[[ "$COUNT" =~ ^[0-9]+$ ]] || { echo "count must be an integer, got '$COUNT'" >&2; exit 2; }
[[ "$KEEP" =~ ^[A-Za-z0-9._+@,-]*$ ]] || { echo "KEEP contains unsupported characters" >&2; exit 2; }

# Heads whose stored scan is Verified: with the security gate enabled those are
# the only packages the API will serve content and Git for, so a sample that
# mixed in a Flagged head would abort setup() with VERIFY_SECURITY=true.
names=$(docker exec "$MONGO_CONTAINER" mongosh \
  -u "$MONGO_USER" -p "$MONGO_PASSWORD" --quiet --eval "
    const keep = '${KEEP}'.split(',').filter(Boolean);
    const pool = db.getSiblingDB('${MONGO_DB}')['package-security-scans'].aggregate([
      { \$match: { isHead: true, status: 'Verified', packageName: { \$nin: keep } } },
      { \$sample: { size: ${COUNT} } },
      { \$project: { _id: 0, packageName: 1 } },
    ]).toArray().map((d) => d.packageName);
    print(keep.concat(pool).join(','));
  ")

if [[ -z "$names" ]]; then
  echo "no servable packages found in ${MONGO_DB}; start the stack with a restored corpus" >&2
  exit 1
fi

keep_count=0
if [[ -n "$KEEP" ]]; then
  keep_count=$(tr ',' '\n' <<<"$KEEP" | grep -c .)
fi
sampled=$(( $(tr ',' '\n' <<<"$names" | grep -c .) - keep_count ))
if (( sampled < COUNT )); then
  echo "warning: only ${sampled} of ${COUNT} requested names were available" >&2
fi

printf '%s\n' "$names"
