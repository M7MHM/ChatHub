# Continuous integration

ChatHub uses GitHub Actions. The workflow is [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) and the dependency updates are configured in [`.github/dependabot.yml`](../.github/dependabot.yml).

## When it runs

| Event | `test` | `docker` |
|---|---|---|
| Pull request to `main` | yes | builds all images, pushes nothing |
| Push to `main` (a merge) | yes | builds and pushes all images to GHCR |
| Manual run (*Actions → CI → Run workflow*) | yes | pushes only when run on `main` |

One run per branch or pull request at a time. A new commit on a pull request cancels the run that is still going; runs on `main` queue instead, so every merge gets its images.

The workflow token is read-only (`contents: read`). Only the `docker` job also gets `packages: write`, which it needs to push to GHCR.

## Job `test`

Runs on `ubuntu-latest` with a 30-minute timeout.

1. Installs the SDK from `global.json` if the repository has one; there is none today, so it installs the latest `10.0.x`.
2. Restores NuGet packages from the cache, keyed on the hash of every `*.csproj` and `Directory.Build.props`.
3. Pulls the images the integration tests start (`postgres:17`, `redis:8-alpine`, `rabbitmq:4-management`, `axllent/mailpit:v1.31`) in parallel, so pulls don't count against the tests' time. **Keep this list in sync** when a test starts a new image; a missing one is only slower, not a failure.
4. `dotnet restore`, then `dotnet build --configuration Release`. Compiler warnings show up as annotations on the run and on the pull request's changed files.
5. Runs the whole suite with the same command you can run locally:

   ```bash
   dotnet test ChatHub.slnx --configuration Release --no-build --results-directory TestResults \
     --logger "trx;LogFilePrefix=chathub" --logger "console;verbosity=detailed" \
     --blame-hang-timeout 10m --blame-hang-dump-type none
   ```

   The four test projects run in parallel, and Testcontainers uses the runner's Docker. Detailed console output includes the stack trace of fixture cleanup errors, which the default verbosity hides. `--blame-hang-timeout` names a test that hangs for 10 minutes instead of letting the job hit its timeout without saying why.
6. Writes a summary to the run page: tests per project, every failed test with its message and stack trace, errors outside tests (for example a collection fixture that failed to dispose), and the ten slowest tests. Failed tests are also annotations. The script is [`.github/scripts/trx_summary.py`](../.github/scripts/trx_summary.py) and needs nothing beyond Python 3.
7. Uploads `TestResults/` (TRX files and blame sequence files) as the `test-results` artifact, kept for 14 days.

The job fails when any test fails, and also when a fixture throws during cleanup: `dotnet test` returns exit code 1 in both cases. Tests that use Windows ACLs (`[WindowsOnlyFact]`) are reported as skipped on Linux.

## Job `docker`

Needs `test`, so no image is built from a commit whose tests failed. One matrix job per Dockerfile target, in parallel; one failing target does not cancel the others.

| Target | Image | Contents |
|---|---|---|
| `api` | `ghcr.io/<owner>/chathub-api` | The HTTP API and SignalR hub |
| `migrator` | `ghcr.io/<owner>/chathub-migrator` | EF Core migration bundle for the API database |
| `notifications` | `ghcr.io/<owner>/chathub-notifications` | The notification worker |
| `notifications-migrator` | `ghcr.io/<owner>/chathub-notifications-migrator` | EF Core migration bundle for `chathub_notifications` |
| `seeder` | `ghcr.io/<owner>/chathub-seeder` | The data seeder / notification backfill tool |

`<owner>` is the repository owner in lower case (GHCR rejects upper case), so for `M7MHM/ChatHub` it is `m7mhm`.

- Builds use Buildx with the GitHub Actions cache, one cache scope per target, so an unchanged layer such as `dotnet restore` is not rebuilt.
- Pull requests only build, to prove every image still builds; nothing is pushed and no registry login happens.
- On `main` the job logs in to `ghcr.io` with the workflow's `GITHUB_TOKEN` and pushes two tags: the short commit SHA (for example `chathub-api:3f9c2ab`) and `latest`. Deploy by SHA; `latest` is a convenience.
- Each image carries OCI labels: `org.opencontainers.image.source` (the repository URL, which also links the GHCR package to the repository), `org.opencontainers.image.revision` (the full commit SHA), `title`, `created` and `version`.

```bash
docker pull ghcr.io/m7mhm/chathub-api:latest
docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' ghcr.io/m7mhm/chathub-api:latest
```

The first push creates each package as **private**. To let a server pull without logging in, open the package (*your profile → Packages → chathub-api → Package settings*) and change its visibility, or log in on the server with a token that has `read:packages`. If a package with the same name already exists and is not linked to this repository, the push fails with `403`: in that package's settings, under *Manage Actions access*, add this repository with the *Write* role.

## How long it takes

Measured locally on the development laptop (Windows, Docker Desktop, images already pulled), 2026-11-25:

| Step | Time |
|---|---|
| Full test suite, the CI command (848 tests, four projects in parallel), three runs in a row | 2 m 44 s, 2 m 55 s, 2 m 01 s |
| Slowest project in those runs (`Application.IntegrationTests`) | 1 m 56 s to 2 m 51 s |
| `docker build --no-cache --target api` | 2 m 49 s |
| `migrator` / `notifications-migrator` (they also install `dotnet-ef` and build a migration bundle) | 1 m 36 s / 1 m 30 s |
| `notifications` / `seeder` | 43 s / 1 m 13 s |

These are **not** GitHub numbers; nothing has run on GitHub yet. Expected on a GitHub-hosted runner: the `test` job around 6 to 10 minutes (setup, image pulls and the Release build add 2 to 3 minutes to the suite), and each `docker` job 2 to 4 minutes on its first run and about a minute once its cache is warm. The five image jobs run in parallel, so a push to `main` should take about 10 to 14 minutes end to end. Replace this paragraph with the real numbers from the first runs.

## Branch protection for `main`

Do this once the workflow has run at least once (for example on the pull request that adds it); GitHub only suggests check names it has seen.

1. *Settings → Rules → Rulesets → New ruleset → New branch ruleset.*
2. Name it `main`, set *Enforcement status* to **Active**, and under *Target branches* choose *Add target → Include default branch*.
3. Turn on:
   - **Restrict deletions** and **Block force pushes**.
   - **Require a pull request before merging** (zero required approvals is fine when you work alone).
   - **Require status checks to pass**. Click *Add checks*, type `test`, and pick the check from GitHub Actions. Also turn on *Require branches to be up to date before merging* so a pull request is tested against the current `main`.
4. *Create*.

Classic branch protection (*Settings → Branches → Add classic branch protection rule*, pattern `main`) works the same: tick *Require status checks to pass before merging* and add `test`.

`test` is the one required check. The image builds (`docker (api)`, `docker (migrator)`, …) also run on pull requests; add them as required checks too if a Dockerfile change should not merge until every image builds.

## Continuous deployment

Deploys are not automatic. [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) runs only by hand (*Actions → Deploy → Run workflow*) with one input, the image tag to deploy: the short SHA of a green CI run on `main`, whose images are already in GHCR. It connects to the server over SSH and runs `bash deploy/deploy.sh <tag>` there, which rolls the API replicas one at a time without downtime ([DEPLOYMENT.md](DEPLOYMENT.md#deploying)).

- It runs in the `production` environment, so you can require a reviewer before it starts (*Settings → Environments → production → Required reviewers*).
- Until the secrets `DEPLOY_SSH_HOST`, `DEPLOY_SSH_USER`, `DEPLOY_SSH_KEY` and `DEPLOY_SSH_KNOWN_HOSTS` are set on that environment, the workflow does nothing and shows a "Deploy skipped" notice; nothing fails. Setup steps: [DEPLOYMENT.md](DEPLOYMENT.md#continuous-deployment).
- Only one deploy runs at a time (`concurrency: deploy-production`, never cancelled midway), and the server-side script takes its own lock as well.
- The host key is pinned through `DEPLOY_SSH_KNOWN_HOSTS`; the workflow never trusts a key it has not seen before.
- Rollbacks stay manual on the server (`bash deploy/rollback.sh`), or run the workflow with the previous tag (it then also runs the migrators, which do nothing because the database is already newer).

`actionlint` and the GitHub workflow JSON schema check both workflows.

## Dependabot

Every Monday Dependabot checks three ecosystems and opens pull requests, which run the same CI:

| Ecosystem | What | Open pull requests at most |
|---|---|---|
| NuGet | all projects (`/`, `/ChatHub.*`, `/tests/*`, `/tools/*`); minor and patch updates grouped into one pull request, major updates one each | 3 |
| Docker | base images in the `Dockerfile` | 2 |
| GitHub Actions | the actions in the workflow, grouped into one pull request | 2 |

Major updates of the .NET packages and of the `sdk`/`aspnet` 10.0 base images need a new target framework as well. Close those pull requests, or use `@dependabot ignore this major version`, until you move to the next .NET version.

## When CI fails

- **A test failed.** The run summary shows its message and stack trace. Run the same test locally with `dotnet test ChatHub.slnx -c Release --filter "FullyQualifiedName~<TestName>"`; the `test-results` artifact has the full TRX.
- **"Errors outside tests".** A collection fixture failed while it was being created or disposed. The console log of the *Test* step has the stack trace (search for `Cleanup Failure`).
- **A hang.** The blame collector stops the test host after 10 minutes and names the test in the log and in the `Sequence_*.xml` file of the artifact.
- **Only on CI, not locally.** The tests must not depend on timing; see [Stable on CI](../tests/COVERAGE.md#stable-on-ci) for the rules. Fix the test, don't re-run until it passes.
