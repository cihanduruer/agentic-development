# Hotel showcase: step-by-step facilitator guide

Use this guide to run the demonstration, not just explain the architecture. Each step says **what to open, what to do, what to say, what to expect, and when to stop**.

The [short team guide](../../demo/TEAM-DEMO-GUIDE.md) and [six-slide deck](../../demo/Agentic-Development-Team-Demo.pptx) are presentation aids. The [Azure responsibility map and diagrams](AZURE-SERVICES-AND-HARNESS.md) explain the engineering behind this walkthrough.

## Choose the demonstration mode

| Mode | What you do | Suggested time |
|---|---|---|
| Replay a completed change | Browse the Hotel, ask knowledge questions, and follow an existing story through its evidence and deployment. No new work or reservation. | 10-15 minutes |
| Start a real requirement | Agree a new requirement, create its story, and show intake. Return later for implementation, checks, and deployment. | 15-20 minutes for kickoff; completion is asynchronous |
| Prove Azure Search MCP | Demonstrate real retrieval in chat, then separately in a cloud development task. Only run this segment after the integration is ready. | Add 5-10 minutes after setup |

Do not promise a new feature will finish during the meeting. The intake schedule is every five minutes, but GitHub scheduling, agents, and checks can take longer.

**Release rule:** after required checks pass, the coordinator can merge and release to development without asking for another human release approval. Production is not part of this demo. This does not bypass failed checks or GitHub's separate workflow-execution approval setting.

## 0. Prepare the tabs and example before presenting

1. Open this repository in GitHub Copilot Desktop. Use the current `main` documentation; do not switch a dirty working tree or discard someone's changes to prepare a demo.
2. Open the [Hotel development site](https://ambitious-bay-0daea7d03.2.azurestaticapps.net/).
3. Open [Azure Boards in sample-project](https://dev.azure.com/ai-enabled-ado-org/sample-project/_boards/board/).
4. Open the repository's [pull requests](https://github.com/cihanduruer/agentic-development/pulls) and [Actions](https://github.com/cihanduruer/agentic-development/actions) in separate tabs.
5. Confirm the presenter can read Boards, issues, PRs, and workflow artifacts. Creating a story requires write access; replay mode does not.
6. Choose one example and open its story, linked issue, PR, and successful deployment before the meeting.

For a historical completed requirement, use [AB#959](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/959), [issue #5](https://github.com/cihanduruer/agentic-development/issues/5), and [PR #6](https://github.com/cihanduruer/agentic-development/pull/6), which introduced the total stay price. Do not reopen or redispatch that closed work. Historical delivery is not a claim that it passed every gate added later.

For the newer brand-palette example, use [AB#960](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/960), [issue #34](https://github.com/cihanduruer/agentic-development/issues/34), and [PR #35](https://github.com/cihanduruer/agentic-development/pull/35). Check its current status first. An open PR is an in-progress example, not a delivered feature.

**Prepare an evidence record:** note the story ID, issue, PR, exact source commit, validation run, review, QA artifact, development deployment, and knowledge revision. Keep secret values and real guest details out of the shared screen.

**Ready when:** the Hotel loads, the selected example is accessible, and you know which parts are live versus historical or still running.

## 1. Show the guest experience

**Open:** the Hotel development tab.

1. Choose **Canal House** and click **View rooms**.
2. Enter future check-in/check-out dates three nights apart and set **Guests** to `2`.
3. Click **Check availability**.
4. Select **Canal King** if it is available. The seeded example rate is EUR 189 per night, so three nights total EUR 567. Read the actual displayed rate rather than assuming prices never change.
5. Point to the nightly rate, number of nights, and total stay price before confirmation.
6. Stop before **Confirm booking**. No new shared-development reservation is needed.

**Say:** "This is the product. The guest sees the result of the work; Azure Boards and GitHub show how we delivered it."

**Expected:** available rooms, a clear selected state, and a total matching rate times nights. If the room is unavailable, choose another future interval or explain the existing booking constraint. Do not delete reservations to make it available.

**Optional negative path:** set check-out equal to check-in and click **Check availability**. Show the validation message, then restore valid dates. This is a read-only demonstration, not permission to run a local test suite.

**If unavailable:** show previously recorded cloud browser screenshots and label them as recorded evidence. Do not restart shared services, change SQL, or deploy an unreviewed repair during the presentation.

## 2. Establish the knowledge source in chat

**Open:** Copilot Desktop in the repository.

Paste:

> Read AGENTS.md and docs/knowledge/. Explain the Hotel booking rules in plain language. Cite the document paths and the Git revision you used. Distinguish facts from assumptions. Do not edit files, create work items, or make a booking.

1. Let Copilot read the knowledge.
2. Pick one claim, such as "check-out must be after check-in."
3. Open its cited document and show the matching rule.
4. Point to the revision used for the answer.
5. If the answer has no supporting source, ask it to identify the evidence gap instead of presenting the claim as fact.

**Say:** "The shared knowledge is versioned in the repository. The agent must show where an answer came from; confidence alone is not evidence."

**Expected:** a plain-language answer with source paths and a revision, not invented booking features. Payments and cancellation functionality are outside the current product scope.

This step uses repository knowledge. **It does not prove Azure Search was called.** Use the separate MCP procedure in step 10 when that connection is ready.

## 3. Agree one bounded requirement

**Skip this step and step 4 in replay mode.** Creating a story starts real work through the scheduled intake.

Paste:

> I want to improve the Hotel booking experience. Help me describe one small guest-facing change. Check whether it already exists, ask for the missing business details, and propose clear acceptance criteria. Do not create a work item or start implementation yet.

1. Describe the desired guest outcome in ordinary language.
2. Answer only the product questions: what the guest should see, when it should happen, and what happens when something is wrong.
3. Read the proposed scope and acceptance criteria.
4. Keep the requirement to one independently deliverable change. Separate unrelated ideas.
5. Confirm that existing reservations, booking rules, and other screens should keep working unless the requirement explicitly changes them.

**A useful requirement has:** who benefits, the visible change, the failure/empty behavior, and observable success criteria. "Make it better" alone is not sufficient.

**Say:** "The product owner decides the outcome. The agent turns that into a clear, bounded implementation task."

**Expected:** an agreed requirement and criteria, with no branch, implementation issue, agent assignment, or code change yet.

## 4. Capture the Azure Boards story

Once the requirement is agreed, paste:

> That requirement and its acceptance criteria look good. Create the story using our default Hotel intake process. Return its link. Do not start development manually.

1. Open the returned story link in `sample-project`.
2. Verify the title and description reflect the agreed outcome.
3. Verify the acceptance criteria are present.
4. At creation, the item should be a **User Story**, state **New**, without `github-synced`.
5. Do not manually add `github-synced`, assign a development agent, or create a competing GitHub issue.

If a fresh client has not picked up the repository/default instructions, use the explicit wording:

> Create this as an Azure Boards User Story in `sample-project`. Leave it in `New` and do not add `github-synced`.

The organization is `https://dev.azure.com/ai-enabled-ado-org`. Explicit user overrides are allowed; do not silently substitute another project or reset an existing Active story.

**Expected:** one story and its link. If the scheduler has already picked it up before you open it, `Active` can be correct; show its history and linked issue rather than changing it back.

**If capture fails:** show the actual access/error message. A drafted description in chat is not a successfully created work item.

## 5. Watch scheduled intake, without starting duplicates

**Open:** [Actions -> Agentic intake](https://github.com/cihanduruer/agentic-development/actions/workflows/agentic-intake.yml).

1. Wait for a scheduled run after the story was created. The five-minute cron is not a guaranteed start time.
2. Open that run and expand **Start Copilot work for eligible hotel items**.
3. Look for the selected work-item ID and the linked GitHub issue.
4. Return to the story and refresh it.
5. Open the GitHub issue from its link and confirm the acceptance criteria and `AB#<id>` reference.
6. Show the assigned Copilot identity and the story's transition to `Active` with `github-synced`.

**Say:** "The scheduler picks up agreed work. It links the requirement to GitHub and verifies agent assignment before marking the story Active."

**Expected:** one story linked to one implementation issue. Retrying intake should reuse the existing issue, not create another.

**If nothing happens:** check whether a scheduled run actually started, then inspect its result and the story state/tags. Do not reset the story or create another issue. A maintainer may use the documented single-item recovery path only after diagnosing a partial failure; recovery is not part of normal requirement capture.

**For a short meeting:** switch to the prepared completed example now. Say explicitly that the new story continues asynchronously.

## 6. Follow the cloud developer's work

**Open:** the linked GitHub issue, then its Copilot task or pull request.

1. Show the issue's requirement, acceptance criteria, and source-knowledge references.
2. Open the agent activity to show that implementation is running in the cloud.
3. Open the PR's **Files changed** tab. Pick one user-visible change and its corresponding test or documentation change.
4. Show the `AB#<id>` tracking field and evidence sections in the PR description.
5. Record the full current head commit from the PR's **Commits** tab.

**Say:** "The agent owns a bounded change, not the whole system. Other agents can work in parallel when their file ownership and dependencies do not conflict."

**Expected:** a linked PR with real source changes and evidence. A draft PR or a task that says it is working is not completion.

All application implementation and testing remain in cloud agents/runners. Local documentation editing is a separate explicit exception, not a local application-testing fallback.

## 7. Read checks and independent evidence

**Open:** the PR's **Checks** tab and its review conversation.

1. Open **PR validation / validate**. Inspect its result and the tested revision.
2. Open **Hotel code review**. Show the actual Copilot review and its current-head findings, not only the eligibility job.
3. Open **QA evidence / Independent QA evidence gate**. Read the job result.
4. On the corresponding Actions run page, download the QA artifact from **Artifacts**.
5. Open `qa-result.json`. Check `status`, `headSha`, `metadataDigest`, test results, and failures. For a product change, also check acceptance/negative-path evidence and the cited knowledge revision.
6. Show the exact-head trusted validation artifact's `validation.json`: both `targetSha` and `checkedOutSha` must identify the intended PR head. A trusted workflow's own `main` SHA can differ from the PR SHA it tests.
7. For UI changes, show actual browser evidence/screenshots for that revision. Do not substitute a local precommit image or an old run.

| What you see | What it means | What to do |
|---|---|---|
| Queued / in progress | Work has not finished | Wait or use recorded evidence; do not merge around it. |
| Failed | A test, review, or evidence contract failed | Read the failing step; let the owner fix it and regenerate affected evidence. |
| Action required / Approve and run workflows | GitHub is holding execution | Use the workflow-approval procedure below; it is not a product release approval. |
| Skipped review alternative | Only one of actual review / not-required should run | Inspect the eligible path; do not interpret the skipped job as a defect. |
| Review eligibility succeeded | The workflow classified the PR | This alone does not prove Copilot reviewed the code. |
| QA passed with `agent.execution: not-run` | Independent workflow evidence passed | Explain honestly that the custom QA agent was not invoked. |
| Missing or expired artifact | Required evidence is unavailable | Do not claim success from a green badge alone. |

The clearer names are **Review eligibility**, **Copilot findings gate**, and **Copilot review not required** once the naming change is merged. Older runs can retain duplicate display names.

**Metadata rule:** finalize the PR title/body before QA. Later source or metadata changes require the affected evidence to be refreshed; QA must match the current metadata digest. Add subsequent result links in comments rather than repeatedly editing the body.

**Say:** "An agent's statement is not enough. Another review and a separate workflow must provide evidence for this version."

### When GitHub asks for workflow approval

For a held PR run, an authorized maintainer can use **Approve and run workflows** in GitHub. To remove recurring Copilot-specific prompts, a repository administrator can open [Settings -> Copilot -> Cloud agent](https://github.com/cihanduruer/agentic-development/settings/copilot/coding_agent) and disable **Require approval for workflow runs**, if repository policy permits.

This permits Copilot-authored workflows to run without that manual hold, so their permissions and available secrets still matter. It is not permission to bypass tests, grant Actions PR-approval rights, or deploy production. Do not report the setting as changed until verified.

## 8. Follow development deployment

**Open:** [Actions -> Deploy development](https://github.com/cihanduruer/agentic-development/actions/workflows/deploy-development.yml).

1. Wait until the coordinator has accepted the required current-revision evidence and merged the change.
2. Open the deployment triggered by the merged `main` revision. Record that revision; the merge commit can differ from the earlier PR head.
3. Follow the deployment job. A merge or a started job is not a successful release.
4. Require a successful terminal result, including its API/SQL-backed readiness and operations checks.
5. Refresh the Hotel development site and show the changed behavior. If needed, reload with browser caching disabled.
6. Compare the visible outcome with the original acceptance criteria.

**Say:** "For this development demo, no additional human release approval is needed after the checks pass. Production remains a different, manual path."

**Expected:** the deployed revision is identified and the guest-facing behavior is visible. If the deployment fails, stop the delivered-feature claim and show the failure; do not weaken controls or create another live booking to prove progress.

Do not open or dispatch **Deploy production** during this walkthrough.

## 9. Close the traceability story and explain Agent Operations

**Open:** the original Azure Boards story.

1. Show the linked issue and PR.
2. Show deployment and evidence links added by lifecycle synchronization after successful delivery.
3. Explain that synchronization updates evidence links/tags; it does not itself set the story to Closed.
4. Only describe the work as complete when its agreed outcome and deployment evidence support that claim.

Optionally open the Hotel's **Agent operations** page:

1. Point to an existing event's correlation, route, effective worker, and knowledge revision.
2. Explain that the page reads persisted operations events and receives SignalR updates.
3. Make clear that Desktop discussions, new Boards stories, and every GitHub Actions run do not automatically appear here.
4. Do not invoke ingestion or routing just to populate the screen; an empty view is an honest result.

**Close:** "We can follow the requirement to code, independent evidence, and the development result. Boards tracks product work; GitHub tracks delivery; Agent Operations shows the runtime events we explicitly ingest."

## 10. Optional: prove Azure Search MCP, then developer use

This is a separate milestone, not a mandatory claim in the basic showcase. A service PR, a running Search resource, or a successful routing existence check does not prove a connected chat client.

### Prepare the connection outside the presentation

1. Confirm the read-only MCP service has been deployed through its reviewed cloud path and has an authenticated endpoint.
2. Follow the implementation's current runbook to configure the actual Copilot chat client. Do not guess the endpoint, header names, or client settings; never display the secret.
3. Confirm the intended knowledge revision was indexed. A recent documentation push is not itself proof of indexing.
4. Verify that the client exposes `search_knowledge` before presenting.
5. If any prerequisite is missing, label MCP **not ready** and use step 2's repository-grounded answer.

### Demonstrate chat retrieval

Paste, replacing the revision placeholder with the verified full indexed commit SHA:

> Use search_knowledge to retrieve the Hotel booking rules at revision <verified-indexed-revision>. Show the relevant returned passage, repository path, source link, and revision. Base the answer only on that evidence. If no matching evidence is returned, say so. Do not change code or create work.

1. Expand the actual tool call in the chat activity.
2. Show that the query requested the intended revision.
3. Open one returned source link and compare the cited passage.
4. Confirm the answer does not add claims absent from the retrieved text.
5. Record the client, endpoint identity without credentials, tool name, requested/returned revision, and citation.

**Expected:** visible tool-call evidence and a cited answer. A response that only says "I used Search" without the actual result is insufficient.

### Demonstrate development-agent retrieval separately

1. Configure the cloud development agent's MCP access through its supported repository/agent configuration, with only the read-only knowledge tool allowed and credentials stored as secrets.
2. On the next authorized bounded cloud task, request:

> Before implementation, call search_knowledge at the verified indexed revision for the relevant Hotel rules. Cite the returned paths and revision in your reasoning and PR evidence. Report missing knowledge explicitly; do not invent it.

3. Inspect that task's actual tool activity and returned evidence.
4. Confirm the cited knowledge appears in the development evidence and applies to its requirement.
5. Do not treat successful Desktop retrieval as proof of this separate connection.

This step starts no extra task by itself. Do not duplicate an already-running development task solely for a presentation.

## Troubleshooting and safe stopping points

| Symptom | Check first | Safe response |
|---|---|---|
| Story is already Active immediately after creation | History and linked issue | The scheduler may have picked it up; do not reset it. |
| No agent activity | Latest intake result and confirmed Copilot assignment | Diagnose the existing task; do not create another issue/session. |
| QA failed while tests passed | Failed evidence step and PR acceptance checklist | Correct truthful evidence formatting; generate fresh digest-bound QA. |
| Site still shows the old feature | Deployment revision/result and browser cache | Do not call an open PR deployed. |
| New docs are absent from Search | Indexed revision, not just `main` | Index/verify through the approved cloud path; do not silently query a different revision. |
| Agent Operations lacks the story | Whether that event was ever ingested | Show Boards/Actions instead; do not fabricate an event. |
| MCP connection or credentials are unavailable | Hosted setup and the actual client configuration | Present repository retrieval honestly and mark the MCP segment pending. |
| A permission/protection blocks an operation | The explicit GitHub/Azure error | Stop or use the documented authorized recovery; do not weaken access controls. |

## Presenter completion checklist

- [ ] I chose replay or real-work mode and told the audience which one.
- [ ] I showed the Hotel without creating an unnecessary persistent reservation.
- [ ] The knowledge answer has sources and a revision; I named its actual retrieval method.
- [ ] The story, issue, PR, and evidence refer to the same work.
- [ ] I distinguished real review, skipped alternatives, and workflow QA from a custom QA-agent invocation.
- [ ] I identified the deployed revision before claiming the change is live.
- [ ] I did not claim automatic closure of Boards items or automatic mirroring into Agent Operations.
- [ ] MCP chat and developer use are separately proven, or clearly marked pending.
- [ ] No production action, secret disclosure, data reset, or local application-testing fallback occurred.

Policy details live in [the delivery contract](../knowledge/agentic-delivery.md), [the pipeline overview](README.md), and [the production release runbook](../runbooks/release.md).
