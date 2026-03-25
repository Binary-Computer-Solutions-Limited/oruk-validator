# Validator Deployment Guide

This comprehensive guide details the end-to-end setup and deployment of the **OpenReferralUK Validator** — a containerized .NET Core 10 API designed for high-performance data validation. It uses **MongoDB Atlas** for persistence and **Heroku** for hosting, with fully automated CI/CD via GitHub Actions.

**Branching & Release Workflow (Enforced)**

1. All development happens in **feature/*** or **bugfix/*** branches
2. Pull Requests are created **into staging** → code is automatically deployed to staging environment for testing/QA
3. After successful testing in staging → create a PR **from staging → main**
4. **Only merges into main are allowed from staging** (enforced via branch protection rules)
5. Merge to main → automatic production deployment on Heroku

This creates a strict promotion path: feature → staging (test) → main (production)

---

## Table of Contents

1. [Overview](#overview)
2. [MongoDB Atlas Setup](#mongodb-atlas-setup)
3. [Heroku Dashboard Configuration](#heroku-dashboard-configuration)
4. [Required Environment Variables](#required-environment-variables)
5. [Rate Limiting Configuration](#rate-limiting-configuration)
6. [GitHub Branch Strategy & Environments](#github-branch-strategy--environments)
7. [GitHub Actions CI/CD Workflows](#github-actions-cicd-workflows)
8. [Post-Deployment Verification](#post-deployment-verification)
9. [Troubleshooting](#troubleshooting)

---

## Overview

### What This Guide Covers

- MongoDB Atlas setup (isolated environments)
- Heroku app & container stack configuration
- Strict branching workflow with enforced promotion path
- GitHub Actions automated container builds & deployments
- Required config vars (including rate limiting)
- Health check endpoints and basic monitoring steps

---

## MongoDB Atlas Setup

This section assumes you have an existing MongoDB Atlas account. We focus on creating isolated environments for the validator.

### Step 1: Create a Project and Cluster

1. Log in to [MongoDB Atlas](https://www.mongodb.com/cloud/atlas).
2. Click the project dropdown → **New Project**.
3. Name it `OpenReferralUK-Validator`.
4. Click **Create**.
5. Click **Build a Database** / **Create**.
6. Select **M0 (Free)** for testing/staging or **M2/M5** for production.
7. Choose **AWS** provider and **London (eu-west-2)** (or closest region).
8. Click **Create Cluster**.

### Step 2: Configure Network Security

1. Go to **Network Access** (Security section).
2. Click **Add IP Address**.
3. For staging/development: select **Allow Access from Anywhere** (`0.0.0.0/0`) — required for Heroku dynamic IPs.
4. For production: consider VPC peering or restricting to known IP ranges (more advanced).

### Step 3: Database User & Connectivity

1. Go to **Database Access** (Security section).
2. Click **Add New Database User**.
3. Select **Password** authentication.
4. Create secure username and strong password.
5. Assign **readWriteAnyDatabase** role.
6. Go to **Clusters** → **Connect** → **Drivers** → **C# / .NET**.
7. Copy the connection string.
8. Replace `<username>` and `<password>` with your credentials.
9. Append database name (e.g. `/oruk-staging?retryWrites=true&w=majority` or `/oruk-prod?...`).

Save separate connection strings for staging and production.

---

## Heroku Dashboard Configuration

### Step 1: Create Heroku Apps

1. Log in to [Heroku Dashboard](https://dashboard.heroku.com).
2. Click **New** → **Create new app**.
3. **Staging**: Name = `staging-oruk-validator`, Region = Europe
4. **Production**: Name = `oruk-validator`, Region = Europe

### Step 2: Set Container Stack

1. Open each app → **Settings** tab.
2. Scroll to **Stack** section.
3. Confirm it shows **container** (if not, the deployment will fail — Heroku must know it's a Docker-based app).

---

## Required Environment Variables

Add these **Config Vars** in the Heroku Dashboard under **Settings** → **Config Vars**. Use separate values for staging and production apps.

| Variable                        | Description                                          | Staging Example                        | Production Example                     |
|---------------------------------|------------------------------------------------------|----------------------------------------|----------------------------------------|
| `ASPNETCORE_ENVIRONMENT`        | Controls which appsettings file to load              | `Staging`                              | `Production`                           |
| `Database__ConnectionString`    | Full MongoDB Atlas connection string                 | `mongodb+srv://...`                    | `mongodb+srv://...`                    |
| `Database__DatabaseName`        | Name of the database to use                          | `oruk-staging`                         | `oruk-prod`                            |
| `Security__AllowedCorsOrigins`  | Comma-separated list of allowed frontend origins     | `https://staging.openreferraluk.org`   | `https://openreferraluk.org`           |
| `OpenTelemetry__Enabled`        | Enable observability/tracing                         | `true`                                 | `true`                                 |

### Rate Limiting Configuration (optional tuning)

| Variable                        | Default (Recommended) | Description                                          |
|---------------------------------|-----------------------|------------------------------------------------------|
| `RateLimiting__PermitLimit`     | `100`                 | Max requests allowed in the time window              |
| `RateLimiting__Window`          | `60`                  | Time window in seconds                               |
| `RateLimiting__QueueLimit`      | `0`                   | Requests to queue (0 = reject immediately)           |

---

## GitHub Branch Strategy & Environments

| Branch          | Purpose                          | Heroku App                     | Database       | Deployment Trigger                   |
|-----------------|----------------------------------|--------------------------------|----------------|--------------------------------------|
| `feature/*`     | Development                      | — (CI only)                    | —              | PR → CI checks only                  |
| `staging`       | Integration / QA / Staging       | `staging-oruk-validator`       | oruk-staging   | Push / merge → auto deploy           |
| `main`          | Production                       | `oruk-validator`               | oruk-prod      | Merge from **staging only** → deploy |

**Enforced rule**: Merges to `main` must come from `staging`.

---

## GitHub Actions CI/CD Workflows

Two workflows work together:

- `ci.yml` — Build, test, scan, push tested Docker image to GHCR (on push to staging/main + PRs)
- `deploy.yml` — Triggered only after successful CI run → pulls the tested image from GHCR and deploys it to the correct Heroku environment (no rebuild on Heroku)

### Required GitHub Secrets & Variables

| Name                     | Type      | Description                                           | Example / Where to set                          |
|--------------------------|-----------|-------------------------------------------------------|-------------------------------------------------|
| `HEROKU_API_KEY`         | **Secret** | Heroku API token                                      | Repository → Secrets → Actions                  |
| `HEROKU_STAGING_APP`     | **Variable** | Name of staging Heroku app                            | Repository → Variables → Actions                |
| `HEROKU_PROD_APP`        | **Variable** | Name of production Heroku app                         | Repository → Variables → Actions                |
| `HEALTH_STAGING_URL`     | **Variable** | Full URL to staging health/live endpoint              | e.g. `https://staging-oruk-validator.herokuapp.com/health-check/live` |
| `HEALTH_PROD_URL`        | **Variable** | Full URL to production health/live endpoint           | e.g. `https://oruk-validator.herokuapp.com/health-check/live` |

### ci.yml (Build, and run tests to ensure code quality)

```yaml
name: CI/CD Pipeline

on:
  pull_request:
    branches: [staging, main]
  push:
    branches: [staging, main]

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

env:
  DOTNET_VERSION: "10.0.x"
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

permissions:
  contents: read
  packages: write          # for pushing to GHCR
  security-events: write   # for Trivy SARIF upload
  actions: read

jobs:
  build-and-test:
    name: Build and Test (.NET)
    runs-on: ubuntu-latest

    steps:
      - name: Checkout repository
        uses: actions/checkout@v6.0.2

      - name: Setup .NET ${{ env.DOTNET_VERSION }}
        uses: actions/setup-dotnet@v5.1.0
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Cache NuGet packages
        uses: actions/cache@v5.0.3
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj', '**/*.props', '**/*.targets') }}
          restore-keys: ${{ runner.os }}-nuget-

      - name: Restore dependencies
        run: dotnet restore

      - name: Build solution (Release)
        run: dotnet build --configuration Release --no-restore

      - name: Run unit / integration tests
        run: dotnet test --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./coverage

      - name: Upload coverage to Codecov
        uses: codecov/codecov-action@v5.5.2
        with:
          directory: ./coverage
          fail_ci_if_error: false

      - name: Upload test artifacts
        if: always()
        uses: actions/upload-artifact@v6.0.0
        with:
          name: test-results
          path: ./coverage
          retention-days: 14

  codeql-analysis:
    name: CodeQL Security Analysis (C#)
    runs-on: ubuntu-latest
    needs: build-and-test   # Run after basic build/test succeeds
    permissions:
      security-events: write
      contents: read
      packages: read        # if needed for private deps

    strategy:
      fail-fast: false
      matrix:
        language: ['csharp']

    steps:
      - name: Checkout repository
        uses: actions/checkout@v6.0.2

      - name: Initialize CodeQL
        uses: github/codeql-action/init@v4.32.2
        with:
          languages: ${{ matrix.language }}
          # Optional: build-mode 'none' is default for C# in 2026 – no autobuild needed
          # build-mode: 'none'   # uncomment if you want to be explicit

      # If you ever need a custom build (e.g. for very complex projects), add:
      # - name: Perform CodeQL autobuild (optional fallback)
      #   uses: github/codeql-action/autobuild@v4

      - name: Perform CodeQL Analysis
        uses: github/codeql-action/analyze@v4.32.2

  security-scan-fs:
    name: Trivy Filesystem Scan
    runs-on: ubuntu-latest
    needs: build-and-test

    steps:
      - name: Checkout repository
        uses: actions/checkout@v6.0.2

      - name: Run Trivy (filesystem)
        uses: aquasecurity/trivy-action@0.28.0
        with:
          scan-type: 'fs'
          scan-ref: '.'
          format: 'sarif'
          output: 'trivy-fs-results.sarif'
          severity: 'CRITICAL,HIGH,MEDIUM,UNKNOWN'

      - name: Upload Trivy FS SARIF
        uses: github/codeql-action/upload-sarif@v4.32.2
        if: always()
        with:
          sarif_file: trivy-fs-results.sarif


  docker-build:
    name: Build & Push Docker Image to GHCR
    runs-on: ubuntu-latest
    needs: build-and-test
    if: github.event_name == 'push' || (github.event_name == 'pull_request' && github.base_ref == 'main')

    steps:
      - name: Checkout repository
        uses: actions/checkout@v6.0.2

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3.12.0

      - name: Login to GitHub Container Registry
        uses: docker/login-action@v3.7.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Extract Docker metadata & tags
        id: meta
        uses: docker/metadata-action@v5.10.0
        with:
          images: ghcr.io/${{ github.repository_owner }}/${{ github.event.repository.name }}
          tags: |
            type=sha,format=short
            type=ref,event=branch
            type=ref,event=pr
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}

      - name: Build and push Docker image
        uses: docker/build-push-action@v6.18.0
        with:
          context: .
          file: ./Dockerfile
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max


  security-scan-image:
    name: Trivy Image Vulnerability Scan
    runs-on: ubuntu-latest
    needs: docker-build
    if: github.event_name == 'push' || (github.event_name == 'pull_request' && github.base_ref == 'main')

    steps:
      - name: Login to GitHub Container Registry
        uses: docker/login-action@v3.7.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Run Trivy on built image
        uses: aquasecurity/trivy-action@0.28.0
        with:
          image-ref: ghcr.io/${{ github.repository_owner }}/${{ github.event.repository.name }}:sha-${{ github.sha }}
          format: 'sarif'
          output: 'trivy-image-results.sarif'
          severity: 'CRITICAL,HIGH,MEDIUM,UNKNOWN'

      - name: Upload Trivy Image SARIF
        uses: github/codeql-action/upload-sarif@v4.32.2
        if: always()
        with:
          sarif_file: trivy-image-results.sarif


  zap-scan:
    name: OWASP ZAP Baseline Scan
    runs-on: ubuntu-latest
    needs: [docker-build, security-scan-image]
    if: github.event_name == 'pull_request' && github.base_ref == 'main'

    steps:
      - name: Checkout repository
        uses: actions/checkout@v6.0.2

      - name: Start application container for ZAP
        run: |
          docker network create zap-net || true
          docker run -d --name test-app \
            --network zap-net \
            -p 8080:80 \
            ghcr.io/${{ github.repository_owner }}/${{ github.event.repository.name }}:sha-${{ github.sha }}

      - name: Wait for application readiness
        run: |
          echo "Waiting for app readiness (max 60s)..."
          for i in {1..20}; do
            if curl -f -s http://localhost:8080/health-check/live >/dev/null; then
              echo "✓ App is ready"
              break
            fi
            echo "Attempt $i/20..."
            sleep 3
          done || { echo "App not ready"; docker logs test-app; exit 1; }

      - name: Run OWASP ZAP Baseline Scan (official action)
        uses: zaproxy/action-baseline@v0.15.0
        with:
          target: 'http://test-app:80'
          docker_name: 'ghcr.io/zaproxy/zaproxy:stable'
          artifact_name: zap-report
          cmd_options: '-l WARN -m 5'   # Warn-level logging, max 5 minutes
          # fail_action: false          # set to true if you want to fail build on alerts
          # rules_file_name: '.zap/rules.tsv'   # optional – uncomment if you create a rules file

      - name: Upload ZAP reports (fallback / extra visibility)
        if: always()
        uses: actions/upload-artifact@v6.0.0
        with:
          name: zap-scan-results
          path: zap-report.*
          if-no-files-found: warn

      - name: Cleanup Docker resources
        if: always()
        run: |
          docker stop test-app || true
          docker rm test-app || true
          docker network rm zap-net || true
```

### deploy.yml (Deploy to Heroku – uses repository variables)

```yaml
name: Deploy to Heroku

on:
  workflow_run:
    workflows: ["CI/CD Pipeline"]
    types:
      - completed
    branches: [staging, main]

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

permissions:
  packages: read   # To pull from GHCR
  contents: read

jobs:
  deploy-staging:
    name: Deploy to Staging
    runs-on: ubuntu-latest
    if: >
      github.event.workflow_run.conclusion == 'success' &&
      github.event.workflow_run.head_branch == 'staging'

    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Install Heroku CLI
        run: curl https://cli-assets.heroku.com/install.sh | sh

      - name: Login to Heroku Container Registry
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
        run: heroku container:login

      - name: Ensure container stack (staging)
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
        run: heroku stack:set container --app ${{ vars.HEROKU_STAGING_APP }}

      - name: Pull tested image from GHCR & deploy to Heroku Staging
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
          IMAGE_NAME: ghcr.io/${{ github.repository_owner }}/${{ github.event.repository.name }}
        run: |
          SHORT_SHA=$(echo ${{ github.event.workflow_run.head_sha }} | cut -c1-7)
          echo "Deploying tested image: ${IMAGE_NAME}:sha-${SHORT_SHA} → ${{ vars.HEROKU_STAGING_APP }}"

          docker pull ${IMAGE_NAME}:sha-${SHORT_SHA}

          echo "Pushing to Heroku..."
          heroku container:push web --app ${{ vars.HEROKU_STAGING_APP }} --tag sha-${SHORT_SHA}

          echo "Releasing..."
          heroku container:release web --app ${{ vars.HEROKU_STAGING_APP }}

          echo "Health check..."
          sleep 12
          curl --fail "${{ vars.HEALTH_STAGING_URL }}" || echo "⚠️ Health check failed – check Heroku logs"

  deploy-production:
    name: Deploy to Production
    runs-on: ubuntu-latest
    if: >
      github.event.workflow_run.conclusion == 'success' &&
      github.event.workflow_run.head_branch == 'main'

    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Install Heroku CLI
        run: curl https://cli-assets.heroku.com/install.sh | sh

      - name: Login to Heroku Container Registry
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
        run: heroku container:login

      - name: Ensure container stack (production)
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
        run: heroku stack:set container --app ${{ vars.HEROKU_PROD_APP }}

      - name: Pull tested image from GHCR & deploy to Heroku Production
        env:
          HEROKU_API_KEY: ${{ secrets.HEROKU_API_KEY }}
          IMAGE_NAME: ghcr.io/${{ github.repository_owner }}/${{ github.event.repository.name }}
        run: |
          SHORT_SHA=$(echo ${{ github.event.workflow_run.head_sha }} | cut -c1-7)
          echo "Deploying tested image: ${IMAGE_NAME}:sha-${SHORT_SHA} → ${{ vars.HEROKU_PROD_APP }}"

          docker pull ${IMAGE_NAME}:sha-${SHORT_SHA}

          echo "Pushing to Heroku..."
          heroku container:push web --app ${{ vars.HEROKU_PROD_APP }} --tag sha-${SHORT_SHA}

          echo "Releasing..."
          heroku container:release web --app ${{ vars.HEROKU_PROD_APP }}

          echo "Health check..."
          sleep 12
          curl --fail "${{ vars.HEALTH_PROD_URL }}" || echo "⚠️ Health check failed – check Heroku logs"

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: v${{ github.event.workflow_run.run_number }}
          name: Release v${{ github.event.workflow_run.run_number }}
          draft: false
          prerelease: false
```

## Recommended Branch Protection Rule Configuration

Protecting the `main` and `staging` branches enforces the strict promotion path (feature → staging → main) and ensures that only code which passes all build, test, quality, and security checks can be merged — and therefore deployed.

### Rule for `main` (Production)

**Branch pattern**: `main`

- Require a pull request before merging → **Yes**
- Require approvals → **1–2**
- Dismiss stale approvals when new commits are pushed → **Yes**
- Require status checks to pass before merging → **Yes**  
  **Required checks**:
  - `staging-to-main-only`
  - `Build and Test (.NET)`
  - `CodeQL Security Analysis (C#)`
  - `Trivy Filesystem Scan`
  - `Build & Push Docker Image to GHCR`
  - `Trivy Image Vulnerability Scan`
  - `OWASP ZAP Baseline Scan` (recommended – only appears on PRs targeting `main`)

- Require conversation resolution before merging → **Yes**
- Include administrators → **Yes**

### Rule for `staging`

**Branch pattern**: `staging`

- Require a pull request before merging → **Yes**
- Require approvals → **1** (or 0 for small teams)
- Require status checks to pass before merging → **Yes**  
  **Required checks**:
  - `Build and Test (.NET)`
  - `CodeQL Security Analysis (C#)`
  - `Trivy Filesystem Scan`
  - `Build & Push Docker Image to GHCR`
  - `Trivy Image Vulnerability Scan`

- Require conversation resolution before merging → **Yes**
- Include administrators → **Yes**

**Enforcement note**:  
Direct pushes to `main` are blocked.  
PRs targeting `main` from branches other than `staging` are technically allowed by GitHub rules but can be rejected via team process, CODEOWNERS file, required base branch restrictions, or custom merge queue settings if your team adopts them.

### Implementation Steps

1. Go to your repository → **Settings** → **Branches** → **Branch protection rules** → **Add branch protection rule** (or edit existing ones).
2. Enter the branch pattern (`main` or `staging`).
3. Configure the settings exactly as listed above.
4. For **Required status checks**, wait until at least one successful CI run has occurred on the branch/PR — GitHub will then show the exact job names from recent workflows.
5. Save the rule.

Once applied, every PR will display the required checks in the conversation view. Merges (and therefore deployments) will be blocked until all required jobs pass successfully.

---

## Post-Deployment Verification

1. **Health Checks**  
   - Liveness: `https://<app-name>.herokuapp.com/health-check/live`  
   - Readiness: `https://<app-name>.herokuapp.com/health-check/ready`

2. **Monitoring**  
   - Heroku Dashboard → **More** → **View logs**  
   - Check **Activity** tab for successful release  
   - Confirm latest GitHub Action completed successfully

---

## Troubleshooting

### Heroku Platform Issues

- **H10 – App crashed** → Missing/invalid config var (most commonly `Database__ConnectionString`)
- **H14 – No web processes running** → **Resources** tab → ensure "web" dyno is **ON**
- **H20 – App boot timeout** → Slow MongoDB connection or heavy startup → check logs & Atlas whitelist

### Database & Connectivity

- **"no primary found in replica set"** → Ensure `0.0.0.0/0` in Network Access
- **Authentication errors** → Verify username/password in connection string

### GitHub Actions / Pipeline

- **Deploy fails** → Check secrets (`HEROKU_API_KEY`), Heroku CLI install step
- **ZAP fails** → Review container logs; increase sleep/wait time; verify port mapping (80 vs 8080)
- **Trivy findings** → Review `trivy-results.sarif` artifact

For persistent issues → review Heroku logs, MongoDB Atlas cluster logs, and GitHub Actions detailed output.