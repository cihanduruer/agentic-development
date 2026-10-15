---
owner: Product owner
last_reviewed: 2026-10-13
---
# Product

## Objective

Demonstrate an evidence-grounded, fully agentic software-delivery lifecycle with a usable hotel-booking application.

## MVP users and outcomes

- A guest browses hotels and rooms.
- A guest searches availability using dates and party size.
- A guest can refine available rooms using accessible amenity filters drawn from the selected hotel's current room catalog; multiple amenities use match-all semantics, and all filters can be cleared together.
- A guest creates one reservation and receives a stable reference.
- A guest may optionally choose Economy, Compact, SUV, or Luxury as a vehicle
  preference while creating a reservation, or proceed with no preference.
- An operator watches agent routes, evidence gates, tool activity, failures, and human checkpoints in real time.
- A release approver reviews evidence before any production deployment.

## Exclusions

Payments, loyalty, dynamic pricing, third-party inventory, cancellation, production customer identity,
vehicle pricing, and rental fulfillment are outside the first MVP. A vehicle preference is not a
guaranteed rental and cannot be added to or changed on an existing reservation.

## Hotel brand palette

The Hotel web application uses these shared CSS design tokens:

| Token | Hex | Use |
|---|---|---|
| `--brand-ink` | `#2B0A0D` | Body text, navigation, and high-contrast actions |
| `--brand-crimson` | `#C4000C` | Primary actions, links, selected-state borders, and the hotel-loading spinner |
| `--brand-blush` | `#FFD6D9` | Accent and selected/active surfaces |
| `--brand-cream` | `#F9F6EF` | Page, card, form, and table surfaces |
| `--brand-taupe` | `#D4CDBF` | Secondary surfaces, borders, and disabled states |
| `--brand-red` | `#FF000F` | Signature brand red: accent bars, indicators, decorative surfaces, and the primary search action |

The home page headline is “Your next getaway starts here.”.

Ink and crimson are used for text, controls, and focus indicators on cream surfaces. The hotel-loading status uses a cream surface and ink text, with a decorative crimson spinner; it remains visible without animation for reduced-motion preferences. Blush and taupe remain backgrounds or decorative borders, not low-contrast text or required control boundaries. Required room and input boundaries use crimson or ink. Disabled controls retain ink text on taupe without reduced opacity.

The signature red `#FF000F` reaches only 3.99:1 against white and cream, so it never carries cream or white text. Where red is a text background (the home page search action) the text is ink, which reaches 4.5:1. Crimson is the darker red used wherever cream text or red text on cream is required.

The home page keeps the cream page background; ink body text maintains WCAG AA contrast against it. Hotel cards, booking panels, forms, and other shared surfaces retain their existing cream backgrounds.

Keyboard focus uses a three-pixel crimson outline with cream separation on light surfaces, and a cream outline with ink separation in navigation. Navigation icons inherit the link color; the current page is underlined. Selected rooms retain a distinct inset border and a visible `Selected` label in addition to `aria-pressed`. Empty catalog and availability results have explicit status text. Errors, connection states, and confirmations retain their text meaning rather than relying on color.

`tests/UnitTests/BrandPaletteTests.cs` checks the exact tokens and their use in application-owned rules. `tests/Browser/brand_palette.py` checks computed browser styles, contrast, keyboard/interaction states, responsive layouts, and the booking journey against a separately started local in-memory API. `tests/Browser/architecture_page.py` runs in the same browser-evidence job and checks the admin console routes, mocked live delivery status, accessibility, responsive layout, and reduced motion without calling any live service. Its screenshots and JSON results are evidence artifacts, not committed images; it never targets the shared development database.

## Public and administrative pages

The public home is booking-only. Its top bar contains Home and an "AI Product Management" link to `/admin`; the home page itself must not expose other administrator links or copy. `/admin` is the AI Product Management assistant (ADR 0003); the Hotel service map and Agent Operations dashboard are directly addressable at `/admin/architecture` and `/admin/operations`. `/architecture` and `/operations` redirect to their corresponding `/admin/` routes. The admin routes have no sign-in or authorization configured; do not claim they are protected.

The product assistant lets a product manager describe a requirement in plain language. It answers from the knowledge center, drafts a User Story with acceptance criteria, and creates it in Azure Boards only after the manager confirms. The story starts in `New` and the existing scheduled intake owns later states. The Knowledge center panel (collapsible) lists indexed blocks and highlights those used in the latest answer. Managers can upload PDF, Word, Excel, text, Markdown, or CSV files (up to 5 MB) or paste text; each upload starts pending approval and is not used until a person selects Approve. Uploads can be removed.

The `GET /api/delivery/status` read path returns a bounded, read-only delivery snapshot. An operator can select a recent AB# delivery, inspect the projected Boards state/tags/links and the GitHub issue, PR, workflow, deployment, lifecycle, and release evidence, and see one of Passed, Running, Failed, Waiting, Skipped, or Not started per stage. A 45-second server cache (5 minutes when no GitHub token is configured) serves status; last-good status remains clearly marked stale on upstream errors. Unknown, missing, or unconnected evidence is never invented. The `/admin` page no longer renders this overview.

This is public read-only operational metadata: admin routes and `GET /api/delivery/status` have no sign-in or authorization, and UI hiding or CORS is not an access control. Anyone able to reach the API can request a recent delivery's fixed Boards projection, so only use work items approved for public disclosure. Azure Boards connection remains explicitly unverified until a human provisions the App Service system identity in the Azure DevOps organization and project; see `docs/runbooks/boards-status-connector.md`. The feature's fake-HTTP tests prove projection and failure behavior, not live service access or credential provisioning.

The architecture page describes the declared Hotel Azure services and repository delivery path. Numbered connectors carry visible labels naming the call or delivery action; moving dots are decorative and do not represent live traffic. Written flow explanations, a legend, service responsibilities and declared tiers, and an end-to-end delivery flow provide the same information without animation. Respect reduced-motion preferences, use semantic ordered lists and card headings, retain keyboard-visible focus and responsive layouts, and do not expose unrelated platform services as Hotel architecture.
