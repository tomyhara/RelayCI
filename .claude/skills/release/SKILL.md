---
name: release
description: Cut a RelayCI release (tag vX.Y.Z, push it, and let GitHub Actions build the win-x64 artifact and publish the GitHub Release). Use when the user asks to release, ship, or tag a new version of RelayCI.
---

# Releasing RelayCI

The GitHub MCP tools available in this environment cannot create a GitHub Release object
directly (no `create_release`/`release_write` tool exists) - only `git` tag/push works, and there
is no `gh` CLI access from this session. The release therefore happens in two stages:

1. This skill creates and pushes an annotated tag `vX.Y.Z`.
2. That push triggers `.github/workflows/release.yml`, which runs on GitHub's own runners (which
   *do* have `gh` and a properly-scoped `GITHUB_TOKEN`), builds a self-contained win-x64 publish
   of `CiRunner.Host`, zips it, and runs `gh release create` with `--generate-notes`.

Do not attempt to call a release-creation API directly, and do not shell out to `gh` or the GitHub
REST API from this session - neither is available here.

## Steps

1. **Get the version** from the user's request (e.g. "0.0.1"). Validate it matches `X.Y.Z`
   (no leading `v`, no pre-release suffixes unless the user explicitly asked for one - if they
   did, adapt the tag accordingly and confirm with them first).

2. **Sync the default branch.** Run:
   ```
   git fetch origin main
   ```
   Confirm the local checkout of `main` matches `origin/main` (or check out `origin/main` into a
   throwaway local branch if the working copy is elsewhere). Do not tag a stale commit.

3. **Confirm CI is green** on that commit before tagging:
   `mcp__github__actions_list` with `method: list_workflow_runs`, `resource_id: ci.yml`,
   `workflow_runs_filter: {"branch": "main"}` - check the latest run for the target SHA has
   `conclusion: "success"`. If it doesn't, tell the user and ask whether to proceed anyway rather
   than silently tagging a red commit.

4. **Check the tag doesn't already exist**: `mcp__github__get_tag` (or `git ls-remote --tags
   origin`). If it does, stop and ask the user how to proceed (bump version vs. re-release).

5. **Create and push the tag:**
   ```
   git tag -a "v$VERSION" -m "Release v$VERSION"
   git push origin "v$VERSION"
   ```
   `scripts/release.sh <version>` wraps steps 2-5 (fetch/clean/up-to-date checks, optional local
   `gh`-based CI check, tag, push) for a human running it outside of Claude Code - reuse it via
   Bash if convenient, but the explicit steps above are what to follow when driving this from a
   Claude Code session since the script's CI check depends on a local `gh` install this
   environment doesn't have.

6. **Watch the triggered `release.yml` run** with `mcp__github__actions_list` /
   `method: list_workflow_runs`, `resource_id: release.yml`, filtered to the pushed tag
   (`workflow_runs_filter: {"event": "push"}`, then match `head_branch`/tag on the newest run).
   Poll via `mcp__github__actions_list` `method: list_workflow_jobs` on that run id until the
   `release` job's `status` is `completed`. Use `ScheduleWakeup` (60-120s) between checks instead
   of sleeping inline.

7. **Report the result**: on success, give the user the Release URL
   (`https://github.com/<owner>/<repo>/releases/tag/v<version>`) and the run URL. On failure, use
   `mcp__github__get_job_logs` on the failed job to diagnose, fix, and re-tag only if the fix
   requires new commits (in which case: delete the bad tag with the user's confirmation, land the
   fix, re-tag). Never force-push over a tag without the user's explicit go-ahead - tags are
   meant to be immutable pointers to a release.

## Notes

- `release.yml` only accepts tags matching `v*.*.*` and validates the format again inside the
  workflow, so a malformed tag fails fast in CI rather than silently publishing garbage.
- The artifact is `RelayCI-<version>-win-x64.zip`, a `dotnet publish -c Release -r win-x64
  --self-contained` output - Release (not Debug) is correct and required here: the Debug-only
  `auth.localUsers` LDAP test double is refused at startup in Release builds by design (spec §9),
  which is exactly the behavior a distributable build should have.
- The release-creation step is idempotent (`gh release view` then `edit`+`upload --clobber` vs.
  `create`): if a tag was created through the GitHub UI's "Create a new release" flow, GitHub
  already published an asset-less, note-less release alongside the tag, and a plain
  `gh release create` would fail with "a release with the same tag name already exists". Re-running
  the workflow against an existing tag/release fills in the notes and asset rather than erroring.
- This repo has a tag ruleset (`refs/tags/**` or similar under Settings -> Rules) that has, in
  practice, blocked this session's `git push origin vX.Y.Z` with a plain HTTP 403 even after the
  legacy Tag protection rules screen was relaxed - the two are separate GitHub features. If a tag
  push 403s, don't loop on retries: ask the user to check Settings -> Rules -> Rule Insights for
  the specific rule/actor blocking it, or have them create the tag directly (e.g. via the GitHub
  UI) and continue from step 6 above once it exists on the remote.
- Don't tag off a branch other than `main` without the user asking for it explicitly (e.g. a
  hotfix branch).
