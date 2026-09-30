---
owner: Product owner
last_reviewed: 2026-09-29
---
# Product

## Objective

Demonstrate an evidence-grounded, fully agentic software-delivery lifecycle with a usable hotel-booking application.

## MVP users and outcomes

- A guest browses hotels and rooms.
- A guest searches availability using dates and party size.
- A guest creates one reservation and receives a stable reference.
- An operator watches agent routes, evidence gates, tool activity, failures, and human checkpoints in real time.
- A release approver reviews evidence before any production deployment.

## Exclusions

Payments, loyalty, dynamic pricing, third-party inventory, cancellation, and production customer identity are outside the first MVP.

## Hotel brand palette

The Hotel web application uses these shared CSS design tokens:

| Token | Hex | Use |
|---|---|---|
| `--brand-dark-blue` | `#164A61` | Body text, navigation, and high-contrast actions |
| `--brand-blue` | `#04668C` | Primary actions, links, and selected-state borders |
| `--brand-light-blue` | `#8DC4E6` | Accent and selected/active surfaces |
| `--brand-cream` | `#F9F6EF` | Page, card, form, and table surfaces |
| `--brand-taupe` | `#D4CDBF` | Secondary surfaces, borders, and disabled states |

Dark blue and blue are used for text, controls, and focus indicators on cream surfaces. Light blue and taupe remain backgrounds or borders, not low-contrast text. Focus indicators use the blue token with a cream separation ring, and state meaning is also conveyed by labels, text, or icons.
