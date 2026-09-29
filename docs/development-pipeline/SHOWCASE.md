# Agentic development: 10-15 minute showcase

**Audience:** product owners, developers, and delivery teams.
**Say:** "A guest requirement becomes code, independently checked evidence, and a
release proposal. Agents do bounded work; people retain production authority."

**Knowledge revision:** `32eaca68b80737bd715d928138ee2ca763d022e1`.
**Sourced** means repository behavior at that revision or the dated evidence below;
**Derived** means this facilitator sequence. **Assumption:** the facilitator has
repository/Board read access and an approved disposable local booking environment.
Platform documentation has Azure Boards ID **N/A**; AB#959 is a historical example.

## Prepare before the meeting

- Read [the pipeline overview](README.md), [agent contract](../../AGENTS.md), and
  [delivery rules](../knowledge/agentic-delivery.md); use [the presentation](PRESENTATION.md)
  for the longer explanation, not during this short demo.
- Open [AB#959](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/959),
  [issue #5](https://github.com/cihanduruer/agentic-development/issues/5),
  [Copilot PR #6](https://github.com/cihanduruer/agentic-development/pull/6), and
  [Actions](https://github.com/cihanduruer/agentic-development/actions) in separate tabs.
- Prefer local booking using the [two-terminal setup](README.md#local-development).
  Keep model-assisted routing disabled. Start before the audience arrives; prepare
  an available room and future dates spanning exactly three nights.
- For hosted booking, use the development
  [web](https://ambitious-bay-0daea7d03.2.azurestaticapps.net) and
  [API health](https://ahb-dev-bj5rmi3w3ntgq-api.azurewebsites.net/health)
  only after this read-only preflight passes:

```powershell
$web = 'https://ambitious-bay-0daea7d03.2.azurestaticapps.net'
$api = 'https://ahb-dev-bj5rmi3w3ntgq-api.azurewebsites.net'
curl.exe --fail --compressed -i "$web/appsettings.json"
curl.exe --fail -I "$web/operations"
curl.exe --fail "$api/health"
curl.exe --fail "$api/api/hotels"
```

In browser DevTools, disable cache and reload. Inspect the runtime configuration
actually fetched by the deployed client (if refactored, inspect its new file too):
compressed responses must resolve `ApiBaseUrl` to the hosted API, never localhost.
Open `/operations` directly in a fresh tab and refresh; require the rendered
dashboard, not just HTTP 200. Confirm booking network requests reach the hosted
API without CORS errors. Record deployment run, source SHA, time, and results.
**Stop the hosted segment if any check fails; do not deploy to repair a demo.**

**Sourced snapshot, 2026-09-29 09:39 UTC:** after frontend fix
[#21](https://github.com/cihanduruer/agentic-development/pull/21), Brotli
`appsettings.json` returned the hosted API URL and `/operations` returned 200.
[Development run 36549810300](https://github.com/cihanduruer/agentic-development/actions/runs/36549810300)
succeeded at the knowledge revision above, including its authorization/persistence
smoke step. Public history exposed its synthetic event with that revision.
These checks supersede the earlier localhost/404 failure; repeat the full browser
preflight for each meeting. Smoke hardening [#20](https://github.com/cihanduruer/agentic-development/pull/20)
and SQL/Search/Prompt Shields work [#12](https://github.com/cihanduruer/agentic-development/pull/12)
were still pending; their integration claims require separate evidence.

## 1. Show the guest outcome (3 minutes)

**Derived walkthrough; sourced behavior:** [booking UI](../../src/Web/Pages/Home.razor),
[domain rules](../knowledge/domain.md), and
[seed catalog](../../src/Infrastructure/HotelBookingPersistence.cs).

1. In the preflight-approved browser, choose **Canal House**, two guests, and
   future check-in/check-out dates three nights apart. Click **Check availability**.
2. Select **Canal King** if available. Point to nightly rate **EUR 189**, **3 nights**,
   and total **EUR 567** before confirmation (check euro amounts, not decimal separators).
   If unavailable, choose another free three-night interval; never erase bookings.
3. Use a synthetic guest name such as `Showcase Guest 20260929-A`.
   Confirm only in the approved disposable local environment, or with explicit
   permission for one persistent development booking. Show its reference and the
   same total on confirmation. Otherwise stop at the price preview.
4. For the negative path, set check-out equal to check-in and search again:
   expect an error, not an accepted stay. Use the automated overlap/capacity cases
   in [booking tests](../../tests/UnitTests/BookingServiceTests.cs) rather than creating more bookings.

**Expected output:** visible three-night total and, only if approved, one synthetic
confirmation. There is **no reservation delete/cancel API** in
[API routes](../../src/Api/Program.cs). Record the demo room/dates/reference; shared
development data persists. Do not promise cleanup, restart shared services, or run
SQL deletes. A default local in-memory instance can be discarded when it stops;
first ensure no persistent connection string is configured.
**Fallback:** show `Home.razor`, the pricing rule, and existing test evidence.

## 2. Replay requirement to Copilot change (2 minutes)

**Sourced:** AB#959 revision 4 was **Closed** on 2026-09-29; issue #5 is closed and
PR #6 is merged at `19876349865f5f7c388f446dfd7186834f947ce7`.

Open those three tabs in order: the accepted price requirement, the linked issue
and agent contract, then the Copilot diff covering UI, model, tests, and knowledge.
Explain that [intake](../../scripts/Start-AgenticWork.ps1) links one work item
idempotently before assignment/state updates. **Do not redispatch AB#959.**

**Optional, outside the timed replay:** a maintainer may deliberately select one
accepted **New/Active** demo item and use
[manual intake](../../.github/workflows/agentic-intake.yml) with that exact ID.
Review its scope/permissions first; never use a blanket query or recycle closed work.
This starts real repository work and is not needed to explain the existing example.

## 3. Follow immutable evidence, not green badges alone (3 minutes)

**Sourced:** inspect [validation](../../.github/workflows/pr-validation.yml),
[review](../../.github/workflows/hotel-code-review.yml), and
[QA](../../.github/workflows/qa-evidence.yml).

For a current **open, same-repository PR to main**, show its full head SHA,
validation, review eligibility/findings, and QA artifact. A changed head needs new
evidence. Documentation-only changes use the explicit not-eligible review path;
that is not a Copilot review of code. Eligible changes require current-head review;
unresolved High or unclassified findings block.

If a maintainer needs fresh evidence, trusted manual **PR validation** must run
from `main` for that PR number and exact SHA first. Require the successful,
unexpired `pr-validation-evidence-<SHA>` artifact before manual **QA evidence**
for the same open PR/head. Do not rerun merged PR #6 or #18 as a live fallback.
The run's workflow SHA can differ from its tested PR SHA: read the artifacts.

For the no-wait baseline, open successful
[main validation 36548644968](https://github.com/cihanduruer/agentic-development/actions/runs/36548644968)
and [QA 36548837929](https://github.com/cihanduruer/agentic-development/actions/runs/36548837929).
In `qa-result.json`, show `headSha` equal to `c6c4e0de0b4e8a21c0db561baea732fd42be73e2`,
`status: passed`, and **22/22 tests**. This is later platform evidence for PR #17,
not a claim that historical PR #6 passed today's gates. Product acceptance fields
are N/A for that non-product change. The workflow applies the evidence contract;
`hotel-qa` custom-agent execution is explicitly **`not-run`**.

## 4. Show operations and the authorization boundary (2 minutes)

**Sourced:** [dashboard](../../src/Web/Pages/Operations.razor),
[API](../../src/Api/Program.cs), and
[authorization tests](../../tests/IntegrationTests/OperationsAuthorizationTests.cs).

Open preflight-approved `/operations`; point out work-item correlation, route,
effective worker, confidence, latency, and knowledge revision. Public history is
bounded; an empty table is a valid result, not evidence that agents ran.
Routing/recording API calls produce events. GitHub Actions activity is **not**
automatically transported to this table; show workflow history in Actions.

Use existing test results to explain anonymous writes **401**, a valid identity
without `Operations.Ingest` **403**, and authorized ingestion **201**. These are
test expectations; the dated hosted smoke above separately proves its writer. Public reads remain
available. Local Development intentionally bypasses writer authentication, so it
cannot demonstrate hosted Entra enforcement. Never display tokens or real guest data.
**Fallback:** show the test/source view if the dashboard or hosted smoke is failing.
Do not run the deployment smoke script live: it writes events and restarts the API.

## 5. End with a release proposal, not production (2 minutes)

**Sourced baseline:** [release 36548968437](https://github.com/cihanduruer/agentic-development/actions/runs/36548968437)
contains `release-c6c4e0de0b4e8a21c0db561baea732fd42be73e2`.
Its `release-evidence.json` names QA run `36548837929`, that same source commit,
and `productionDeployment: not-started`. All four SHA-256 entries were matched
on 2026-09-29 (API zip, web zip, evidence JSON, proposal Markdown).

For future meetings select a successful [release proposal run](https://github.com/cihanduruer/agentic-development/actions/workflows/release-proposal.yml)
whose artifact still exists; inspect its actual source/QA chain, not just the latest
run or this historical SHA. Download its named artifact to a new local folder.
Read `release-evidence.json` and `checksums.sha256`; compare every listed file using
`Get-FileHash <downloaded-file> -Algorithm SHA256`. No rebuild or deployment is needed.
An expired/missing artifact means choose another verified run, not fabricate proof.

Explain [manual production controls](../runbooks/release.md):
exact release run/SHA, typed confirmation, provenance, and checksum verification.
**Manual:** production promotion is outside this showcase; do not dispatch it.
**Plan-blocked:** the private GitHub plan does not enforce required branch checks or
environment reviewers. The production environment/resource group being configured
is not proof of production deployment; no production workflow run was observed.
**Implemented is not live-verified:** SQL, Search, Prompt Shields, and model routing
need their own integration/runtime evidence; provisioned services alone prove neither
retrieval nor evaluation nor end-to-end GitHub telemetry.

**Close:** "We saw a guest outcome, traceable work, exact-revision evidence, an
observable API boundary, and a release artifact. Human authority still gates production."
