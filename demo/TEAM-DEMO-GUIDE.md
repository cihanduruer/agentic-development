# Hotel demo: ask, track, see the result

**Audience:** a nontechnical product owner. **Length:** 5-10 minutes using existing evidence.

For click-by-click facilitation, copy-paste prompts, expected results, and troubleshooting, use the [step-by-step showcase](../docs/development-pipeline/SHOWCASE.md).

**Say:** "We describe a guest need, let cloud agents develop it, and follow the result into our development site. Checks stay mandatory; production stays manual."

## Before the meeting

Open Copilot Desktop with this repository, plus these three browser tabs:

- [Hotel development site](https://ambitious-bay-0daea7d03.2.azurestaticapps.net/)
- [Azure Boards: sample-project](https://dev.azure.com/ai-enabled-ado-org/sample-project/_boards/board/)
- [GitHub pull requests](https://github.com/cihanduruer/agentic-development/pulls)

Choose a completed example for the walkthrough. A new feature can take longer than the meeting; do not promise live completion. Use the six-slide [deck](Agentic-Development-Team-Demo.pptx) only if helpful.

## 1. Show the Hotel (1 minute)

Choose a hotel, future dates, and two guests. Check availability, select a room, and show the total price.

**Stop before Confirm booking.** This is shared development data; no new reservation is needed for this demo. If the site is unavailable, show existing cloud screenshots and clearly call them recorded evidence.

## 2. Ask a knowledge question (2 minutes)

Ask Copilot:

> Read the repository knowledge. Explain our booking rules in plain language and cite the source documents and revision. Do not change anything.

Point to the cited source, not just the answer. Repository documents are the source of truth; Azure AI Search is a searchable copy.

**Only when MCP is connected and verified in this chat**, use:

> Use the knowledge search tool to explain our booking rules at the verified indexed revision. Show the retrieved passage, document link, and revision. If evidence is missing, say so. Do not change anything.

Show the actual tool call and returned citations. If MCP is not ready, use repository knowledge and explicitly say, "This answer did not use Azure Search." Connecting chat does not prove that the development agent can use MCP; that needs its own tool-call evidence.

## 3. Agree one small improvement (1 minute)

Ask:

> Help me describe one small improvement to the Hotel booking experience. Check whether it already exists, and help me agree how we will know it works. Do not create work yet.

Once the requirement is agreed, say:

> That looks good. Create the story.

The default is a User Story in `sample-project`, initially `New`, without `github-synced`. Copilot returns the story link. This creates real work: skip this step for a read-only replay.

The scheduled intake later links a GitHub issue, assigns a cloud developer, and moves the story to `Active`. It is scheduled every five minutes, but pickup can be delayed. Do not manually start a second agent or reset an existing story.

## 4. Follow one existing change (2 minutes)

Open its story, linked issue, and pull request. Show the change and its checks:

| Check | Plain-language meaning |
|---|---|
| PR validation | The change builds and passes automated checks. |
| Copilot findings gate | Copilot reviewed this exact version for blocking findings. |
| QA evidence | A separate workflow checks the result and saves evidence. |

The actual review and "review not required" paths are alternatives: one is normally skipped. That is not a duplicate execution. The naming change makes them easier to distinguish; older runs can retain identical labels.

QA is automated workflow evidence today, not a claim that the custom QA agent ran. If a check is running, explain that the system is still working; if it fails, stop rather than bypass it.

## 5. Show the development result (1 minute)

After the required checks pass, the coordinator can merge and release to development without another human approval. Merge triggers the existing development deployment; wait for its success before showing the new behavior.

No production release is part of this demo. A GitHub **Approve and run workflows** prompt is a separate repository execution setting, not a development release approval.

**Close:** "Azure Boards tells us what is being built. GitHub shows the work and checks. The Hotel site shows the delivered result."

## Presenter-only readiness notes

Snapshot on **2026-09-30**; refresh PR and deployment status before presenting:

| Change | Evidence boundary |
|---|---|
| Brand palette, [PR #35](https://github.com/cihanduruer/agentic-development/pull/35) | Cloud browser evidence exists; do not call the palette live until its development deployment succeeds. |
| Default capture instructions, [PR #36](https://github.com/cihanduruer/agentic-development/pull/36) | Open at this snapshot. The default is agreed in this session; a fresh checkout needs the instructions available before relying on it automatically. |
| Search MCP, [PR #38](https://github.com/cihanduruer/agentic-development/pull/38) | In progress; do not claim chat or developer-agent connectivity without actual tool calls. |
| Clearer check names, [PR #40](https://github.com/cihanduruer/agentic-development/pull/40) | Proposed; new labels apply to future runs after merge, not historical runs. |

Agent Operations is optional: it shows ingested runtime events, not a mirror of chat, Boards, or Actions. Do not wait there for a requirement-capture decision.

Application implementation and tests stay in the cloud. Local documentation work needs an explicit exception. This guide follows [the delivery contract](../docs/knowledge/agentic-delivery.md); technical detail is in [the pipeline documentation](../docs/development-pipeline/README.md).
