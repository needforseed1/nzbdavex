# Agent guidance

## Change delivery

- Never stage, commit, or push changes unless the user explicitly requests it. An implementation, test run, local build, or local deployment does not imply permission to commit or push.
- After implementing and validating changes, when delivery was not already specified, ask whether the user wants:
  1. a local image build/redeployment; or
  2. a release with a version bump, changelog entry, commit/push, and an updated GitHub release page.
- Do not start either delivery path until the user chooses it explicitly.

## Release workflow

Treat a release as complete only after the code, GitHub release, and requested deployment agree on the same version.

1. Inspect `git status -sb` first. Never stage, commit, or build unrelated local changes. If the worktree is dirty, stage selected files/hunks and build from a clean detached worktree at the pushed commit.
2. Update `version.txt` with plain SemVer (for example, `1.3.3`). This supplies `NZBDAV_VERSION` and the version shown in the app footer.
3. Add the newest entry at the top of `CHANGELOG.md`, including the date and a `vOLD...vNEW` comparison link. Keep release notes user-facing and concise.
   - Write for people using the app: lead with what improved or was fixed, in plain language.
   - Prefer one clear outcome per bullet. Avoid class names, method names, internal control flow, and implementation narration unless users need them to understand behavior or compatibility.
   - Do not include private test titles, benchmark results, before/after timings, throughput figures, or other development-session measurements.
   - Include exact numbers only when they are part of user-visible behavior, a configurable setting, a limit, or a compatibility requirement.
   - Keep the GitHub release notes consistent with the changelog instead of adding more technical or benchmark detail there.
4. Validate in proportion to the change. The normal release checks are:

   ```sh
   dotnet test backend.Tests/NzbWebDAV.Tests.csproj
   npm --prefix frontend run typecheck
   npm --prefix frontend run build
   ```

5. Review `git diff --cached --check` and `git diff --cached` before committing. Push the intended branch and confirm the remote contains the release commit.
6. Publish `vVERSION` on GitHub from that exact commit. Use the full 40-character commit SHA for `gh release create --target`; abbreviated SHAs can be rejected. Mark a stable release as latest, use the changelog entry for its notes, and verify both the published release and remote tag afterward.

## Local production deployment

- Compose project: `/opt/docker/compose/nzbdavex/docker-compose.yml`
- Application image: `nzbdavex:main-local`
- Application service: `nzbdavex`
- When the user explicitly asks for a local rebuild or redeployment, keep it simple and build directly from the current checkout, including its uncommitted changes. Do not create a temporary worktree, isolated source copy, change digest, prerelease version, or Docker rollback tag.
- A local deployment must not stage, commit, push, bump versions, tag, or publish anything. Git is sufficient for source rollback.
- Build the local image from the repository root:

  ```sh
  docker build -t nzbdavex:main-local .
  ```

- Recreate only the application unless the user explicitly asks to replace another service:

  ```sh
  docker compose -f /opt/docker/compose/nzbdavex/docker-compose.yml up -d --force-recreate --no-deps nzbdavex
  ```

- Briefly confirm that the application container starts and becomes healthy. Inspect logs only when startup or health fails.
- Keep progress updates and the final deployment report terse unless something goes wrong.

## Safety

- Do not display or commit Compose `.env` contents, credentials, or `/config` data.
- Preserve user changes in a dirty worktree and call them out in the handoff.
- Do not create a tag, GitHub release, or production deployment unless the user requested that stage.
