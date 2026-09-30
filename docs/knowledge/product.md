---
owner: Product owner
last_reviewed: 2026-09-30
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

Dark blue and blue are used for text, controls, and focus indicators on cream surfaces. Light blue and taupe remain backgrounds or decorative borders, not low-contrast text or required control boundaries. Required room and input boundaries use blue or dark blue. Disabled controls retain dark blue text on taupe without reduced opacity.

Keyboard focus uses a three-pixel blue outline with cream separation on light surfaces, and a cream outline with dark blue separation in navigation. Navigation icons inherit the link color; the current page is underlined. Selected rooms retain a distinct inset border and a visible `Selected` label in addition to `aria-pressed`. Empty catalog and availability results have explicit status text. Errors, connection states, and confirmations retain their text meaning rather than relying on color.

`tests/UnitTests/BrandPaletteTests.cs` checks the exact tokens and their use in application-owned rules. `tests/Browser/brand_palette.py` checks computed browser styles, contrast, keyboard/interaction states, responsive layouts, and the booking journey against a separately started local in-memory API. Its screenshots and JSON results are evidence artifacts, not committed images; it never targets the shared development database.
