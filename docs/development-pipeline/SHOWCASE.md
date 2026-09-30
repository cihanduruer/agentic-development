# Short Hotel showcase

Use the [team demo guide](../../demo/TEAM-DEMO-GUIDE.md) as the single presenter script and the optional [six-slide deck](../../demo/Agentic-Development-Team-Demo.pptx).

**Five steps, 5-10 minutes:** show the Hotel, ask a cited knowledge question, agree a small improvement, follow one existing change, and show its development result.

Use a completed change to avoid waiting for new implementation during the meeting. A new story creates real work; it starts in `New` and the scheduled intake owns subsequent assignment. The five-minute schedule is not a completion promise.

Development-demo delivery needs no additional human release approval after the required checks pass. The coordinator still verifies exact-revision review, validation, QA, and successful deployment. Production remains manual and outside the showcase.

Keep these distinctions explicit:

- A repository-grounded answer is not an Azure Search MCP tool call. Verify chat and developer-agent connections separately.
- QA workflow evidence does not mean the custom `hotel-qa` agent executed.
- A skipped alternative review job is normal; it is not duplicate work or proof of a completed code review.
- A local documentation commit is neither a deployment nor an update to the Search index.
- Agent Operations shows ingested events, not every chat, Board change, or GitHub workflow.

Preview availability and price without submitting a new shared-development booking. Do not reset reservations, restart shared services, dispatch production, or bypass a failing gate to keep a demo moving.

The [technical presentation](PRESENTATION.md) is an optional deep dive, not the live-demo script. See the [delivery contract](../knowledge/agentic-delivery.md) for policy and the [release runbook](../runbooks/release.md) for production operations.
