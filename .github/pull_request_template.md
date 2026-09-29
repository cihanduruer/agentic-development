## Work tracking

<!-- Product changes require an AB# reference. Platform-only changes may state "Platform change: true". -->

- Azure Boards: AB#
- Platform change: false

## Acceptance criteria evidence

- [ ] Criterion and evidence location

## Negative-path evidence

- Negative path and test or other evidence

## Validation

- [ ] `dotnet restore --locked-mode`
- [ ] `dotnet format --verify-no-changes --no-restore`
- [ ] `dotnet build --no-restore`
- [ ] `dotnet test --no-build`
- [ ] `az bicep build --file infra/main.bicep`

## Knowledge revision

<!-- Commit SHA used when reading docs/knowledge/. -->

## Review, gaps, and impact

- Review status:
- Unresolved gaps:
- Knowledge changes:
- Security impact:
- Deployment impact:
