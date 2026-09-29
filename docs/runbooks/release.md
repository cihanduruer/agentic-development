# Release runbook

1. Confirm every included Azure Boards item has acceptance criteria, pull request, knowledge revision, review resolution, and QA evidence.
2. Confirm `dotnet restore --locked-mode`, formatting, build, tests, security checks, and Bicep validation pass.
3. Build application artifacts once and record their SHA-256 digests.
4. Review the Azure what-if result, database migration impact, operational risks, and rollback plan.
5. Create a release proposal containing the immutable artifact digests and evidence links.
6. Obtain approval through the protected GitHub `production` environment.
7. Promote the tested artifacts without rebuilding.
8. Run health, booking, telemetry, and dashboard smoke tests.
9. On failure, stop promotion, roll back to the previous artifact, and update the Azure Boards item with evidence.
10. Mark work Released only after successful smoke tests and telemetry verification.
