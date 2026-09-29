# ADR 0002: Microsoft-native agent routing

**Status:** Accepted  
**Date:** 2026-09-29  
**Supersedes:** ADR 0001 routing-provider decision

## Decision

Replace Jev/TypeSafe routing with deterministic C# policy plus Microsoft Agent Framework and Azure OpenAI structured output. Deterministic policy owns evidence, risk, authorization, and human-approval decisions. The model is used only to select among multiple safe, currently available workers.

Azure AI Services uses local authentication disabled. App Service authenticates with an Entra managed identity granted Cognitive Services OpenAI User. A strict typed response is checked against the live worker menu and a confidence threshold. Any incomplete evidence, elevated risk, invalid response, low confidence, missing configuration, or model failure routes to `human_review`.

Only bounded routing metadata is sent to the model. Repository content, prompts, secrets, personal data, work-item bodies, and retrieved document bodies are excluded.

## Consequences

Identity, access control, telemetry, model hosting, and orchestration remain in the Microsoft platform. The external routing dependency and API secret are removed. Deterministic routes avoid unnecessary model latency and cost. Model-assisted routing remains probabilistic, so it cannot authorize actions and must retain conservative human fallback and continuous Foundry evaluation.
