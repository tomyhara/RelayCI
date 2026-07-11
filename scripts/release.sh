#!/usr/bin/env bash
# Tags a release and pushes it, which triggers .github/workflows/release.yml to build the
# self-contained win-x64 artifact and publish the GitHub Release.
#
# Usage: scripts/release.sh <version>   (e.g. scripts/release.sh 0.0.1)
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <version>  (e.g. $0 0.0.1)" >&2
  exit 1
fi

version="$1"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Error: version must be X.Y.Z (got '$version')" >&2
  exit 1
fi
tag="v$version"

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

default_branch="$(git remote show origin | sed -n '/HEAD branch/s/.*: //p')"
default_branch="${default_branch:-main}"

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Error: working tree is not clean. Commit or stash changes first." >&2
  exit 1
fi

echo "Fetching origin/$default_branch..."
git fetch origin "$default_branch"

current_branch="$(git rev-parse --abbrev-ref HEAD)"
if [[ "$current_branch" != "$default_branch" ]]; then
  echo "Error: checkout $default_branch before releasing (currently on $current_branch)." >&2
  exit 1
fi

local_sha="$(git rev-parse HEAD)"
remote_sha="$(git rev-parse "origin/$default_branch")"
if [[ "$local_sha" != "$remote_sha" ]]; then
  echo "Error: local $default_branch ($local_sha) is not up to date with origin/$default_branch ($remote_sha)." >&2
  echo "Run: git pull origin $default_branch" >&2
  exit 1
fi

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "Error: tag $tag already exists locally." >&2
  exit 1
fi

if command -v gh >/dev/null 2>&1; then
  echo "Checking CI status for $local_sha via gh..."
  ci_conclusion="$(gh run list --branch "$default_branch" --workflow ci.yml --commit "$local_sha" \
    --json conclusion --jq '.[0].conclusion // "unknown"' 2>/dev/null || echo "unknown")"
  if [[ "$ci_conclusion" != "success" ]]; then
    echo "Warning: latest CI run for $local_sha reports '$ci_conclusion', not 'success'." >&2
    read -r -p "Continue tagging $tag anyway? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
  fi
else
  echo "Warning: gh CLI not found, skipping CI status check." >&2
fi

echo "Creating annotated tag $tag on $local_sha..."
git tag -a "$tag" -m "Release $tag"

echo "Pushing $tag to origin..."
git push origin "$tag"

remote_url="$(git remote get-url origin)"
slug="$(echo "$remote_url" | sed -E 's#(git@github\.com:|https://github\.com/)##; s#\.git$##')"

cat <<EOF

Tag $tag pushed. GitHub Actions will now build the release artifact and publish the Release:
  https://github.com/$slug/actions/workflows/release.yml
Once it completes:
  https://github.com/$slug/releases/tag/$tag
EOF
