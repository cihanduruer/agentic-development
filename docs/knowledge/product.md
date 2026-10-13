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
| `--brand-dark-blue` | `#164A61` | Body text, navigation, and high-contrast actions |
| `--brand-blue` | `#04668C` | Primary actions, links, and selected-state borders |
| `--brand-light-blue` | `#8DC4E6` | Accent and selected/active surfaces |
| `--brand-cream` | `#F9F6EF` | Page, card, form, and table surfaces |
| `--brand-taupe` | `#D4CDBF` | Secondary surfaces, borders, and disabled states |
| `--brand-orange` | `#FFA500` | Home page background |

Dark blue and blue are used for text, controls, and focus indicators on cream surfaces. Light blue and taupe remain backgrounds or decorative borders, not low-contrast text or required control boundaries. Required room and input boundaries use blue or dark blue. Disabled controls retain dark blue text on taupe without reduced opacity.

The home page uses orange as its page background; dark-blue body text maintains WCAG AA contrast against it. Hotel cards, booking panels, forms, and other shared surfaces retain their existing cream backgrounds.

Keyboard focus uses a three-pixel blue outline with cream separation on light surfaces, and a cream outline with dark blue separation in navigation. Navigation icons inherit the link color; the current page is underlined. Selected rooms retain a distinct inset border and a visible `Selected` label in addition to `aria-pressed`. Empty catalog and availability results have explicit status text. Errors, connection states, and confirmations retain their text meaning rather than relying on color.

`tests/UnitTests/BrandPaletteTests.cs` checks the exact tokens and their use in application-owned rules. `tests/Browser/brand_palette.py` checks computed browser styles, contrast, keyboard/interaction states, responsive layouts, and the booking journey against a separately started local in-memory API. `tests/Browser/architecture_page.py` runs in the same browser-evidence job and checks the admin console routes, mocked live delivery status, accessibility, responsive layout, and reduced motion without calling any live service. Its screenshots and JSON results are evidence artifacts, not committed images; it never targets the shared development database.

## Public and administrative pages

The public home/navigation is booking-only and must not expose administrator links or copy. The delivery overview, Hotel service map, and Agent Operations dashboard are directly addressable at `/admin`, `/admin/architecture`, and `/admin/operations`. `/architecture` and `/operations` redirect to their corresponding `/admin/` routes. Admin screens visibly disclose that no sign-in or authorization is configured; do not claim they are protected.

The delivery overview at `/admin` reads a bounded, read-only delivery snapshot from `GET /api/delivery/status`. An operator can select a recent AB# delivery, inspect the projected Boards state/tags/links and the GitHub issue, PR, workflow, deployment, lifecycle, and release evidence, and see one of Passed, Running, Failed, Waiting, Skipped, or Not started per stage. The responsive diagram is also an ordered-list screen-reader alternative, includes real event timestamps/durations and evidence links, and refreshes every 20 seconds only while visible. A 45-second server cache (5 minutes when no GitHub token is configured) serves status; last-good status remains clearly marked stale on upstream errors. Unknown, missing, or unconnected evidence is never invented.

This is public read-only operational metadata: admin routes and `GET /api/delivery/status` have no sign-in or authorization, and UI hiding or CORS is not an access control. Anyone able to reach the API can request a recent delivery's fixed Boards projection, so only use work items approved for public disclosure. Azure Boards connection remains explicitly unverified until a human provisions the App Service system identity in the Azure DevOps organization and project; see `docs/runbooks/boards-status-connector.md`. The feature's fake-HTTP tests prove projection and failure behavior, not live service access or credential provisioning.

The architecture page describes the declared Hotel Azure services and repository delivery path. Numbered connectors carry visible labels naming the call or delivery action; moving dots are decorative and do not represent live traffic. Written flow explanations, a legend, service responsibilities and declared tiers, and an end-to-end delivery flow provide the same information without animation. Respect reduced-motion preferences, use semantic ordered lists and card headings, retain keyboard-visible focus and responsive layouts, and do not expose unrelated platform services as Hotel architecture.
