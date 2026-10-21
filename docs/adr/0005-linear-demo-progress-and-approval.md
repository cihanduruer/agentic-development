# ADR 0005: Linear demo progress and product-owner approval

## Status

Accepted by the product owner on 2026-10-21. Implementation and local verification were explicitly authorized without creating another Azure Boards item. Supersedes the automatic development-merge authorization in the delivery policy, not the separate production approval boundary.

## Decision

Agent Operations presents one product-owner path:

`Received -> Being built -> Checking -> Your approval -> Publishing -> Ready`

The first row identifies the current request and linked GitHub issue. The second row contains those six steps with evidence links. Only the first incomplete step is current; later steps remain upcoming. Internal checks may run concurrently, but they are one Checking step, not competing product-owner actions. Failures, skipped checks and stale data never become green. Details retains actual workflow names, run links and source revisions.

The approved-email administrator session is required for both approval endpoints, even in local Development. Management, MCP and OperationsWriter credentials cannot authorize this action. `DevelopmentApproval:GitHubToken` is a separate, server-only credential; missing configuration disables approval explicitly. Do not reuse the delivery-status read credential. Provision a fine-grained token or GitHub App identity with this repository's Actions read, checks read, metadata read, contents write and pull-requests write permissions. Its identity must be able to approve the target PR; GitHub does not permit an author to approve their own PR. Never put the credential in WebAssembly, static settings, source, URLs or logs.

Approval policy verifies a fresh, unambiguous canonical request and a non-draft same-repository change to main. It requires successful current-head code-review steps and browser checks, successful latest revision checks, full main-dispatched validation provenance, and independent QA bound to the PR head and current title/body digest. Evidence must come from the expected repository/workflow, remain unexpired and be no older than 24 hours. Missing, oversized, ambiguous, partial, skipped or malformed evidence fails closed.

The server records a GitHub approval review containing the work identity, exact revision, acceptance-metadata digest, pseudonymous administrator identifier and timestamp. It revalidates before a SHA-conditional squash merge. An in-process lock serializes requests; GitHub's conditional merge is the cross-instance atomic boundary. A repeat request recovers the trusted review receipt and merge state. Receipt creation without a confirmed merge is not a publishing success. No email address is published in the receipt.

The existing main-push development workflow owns deployment; no competing deployment workflow is introduced. Publishing becomes green only from that merged revision's deployment run. Ready additionally requires successful lifecycle/delivery verification. An external merge without a trusted in-site receipt is explicitly unverified, not retroactively approved. Optional production release stages do not block demo readiness or become implicitly approved.

## Grounding and rollout

- Implementation base: `22a74959f0017b117bb0fc2b07b3d2853c90a10a`.
- Sourced canonical architecture: `docs/knowledge/architecture.md`, revision `5ae54ad9765ee3098a28ce551e7e417e2f45c701-archmap-20261021`.
- Sourced canonical product: `docs/knowledge/product.md`, revision `5ae54ad9765ee3098a28ce551e7e417e2f45c701-sharedadmin-20261021`.
- Those records were retrieved through the authenticated hosted Knowledge Center MCP. This ADR records the explicitly accepted change; it does not claim that an unmerged branch is deployed.
- Configure the write credential only in the intended environment after review. Local mocked merges are test evidence, never real deployment evidence. Production promotion remains separate and manually approved.

## Consequences

The simpler interface does not remove operations history, relax SQL/trusted-validation requirements, change scheduled intake, or automatically merge existing work. An administrator must refresh after an updated revision or acceptance description. A pending or failed publishing run stays pending or failed even after successful approval.
