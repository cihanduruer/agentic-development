"""AB#960, AB#961, and AB#966 browser evidence. Build the solution and start only the Web dev server first.

python -m pip install -r tests/Browser/requirements.txt
python tests/Browser/brand_palette.py --web-url http://127.0.0.1:5167 --output <artifact-directory>

Uses installed Edge by default (--channel chromium uses Playwright's installed Chromium).
Starts and stops its own API process with an explicit empty SQL connection, in-memory
seeding, and disabled cloud routing. No shared API or database is used. Fixtures are
identified separately from real booking/API interactions in the JSON evidence.
"""

import argparse
import json
import re
import socket
import subprocess
import time
import traceback
from datetime import date, timedelta
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright

expect.set_options(timeout=30_000)

ROOT = Path(__file__).resolve().parents[2]
COLORS = {
    "dark": "rgb(22, 74, 97)",
    "blue": "rgb(4, 102, 140)",
    "light": "rgb(141, 196, 230)",
    "cream": "rgb(249, 246, 239)",
    "taupe": "rgb(212, 205, 191)",
}


def contrast(first, second):
    def luminance(value):
        channels = [float(n) / 255 for n in re.findall(r"[\d.]+", value)[:3]]
        return sum((n / 12.92 if n <= .04045 else ((n + .055) / 1.055) ** 2.4) * w
                   for n, w in zip(channels, [.2126, .7152, .0722]))
    a, b = luminance(first), luminance(second)
    return (max(a, b) + .05) / (min(a, b) + .05)


def styles(locator):
    return locator.evaluate("""e => {
        const s = getComputedStyle(e);
        return Object.fromEntries(['color', 'backgroundColor', 'borderTopColor',
            'outlineColor', 'outlineStyle', 'outlineWidth', 'outlineOffset',
            'boxShadow', 'opacity', 'textDecorationLine', 'maskImage'].map(k => [k, s[k]]));
    }""")


def check_pair(locator, foreground, background, minimum=4.5):
    state = styles(locator)
    ratio = contrast(state[foreground], state[background])
    assert ratio >= minimum, (locator, state, ratio)
    return {"styles": state, "contrast": round(ratio, 3), "minimum": minimum}


def check_focus(locator):
    state = styles(locator)
    separation = re.findall(r"rgb\([^)]+\)", state["boxShadow"])
    assert separation and state["outlineStyle"] == "solid" and state["outlineWidth"] == "3px", state
    ratio = contrast(state["outlineColor"], separation[-1])
    assert ratio >= 3, state
    return {"state": "keyboard focus", "styles": state, "contrast": round(ratio, 3), "minimum": 3}


def audit_text(page):
    samples = page.evaluate("""() => [...document.querySelectorAll('body *')].filter(e =>
        e.getClientRects().length && getComputedStyle(e).visibility === 'visible' &&
        ([...e.childNodes].some(n => n.nodeType === Node.TEXT_NODE && n.textContent.trim()) ||
            !['none', 'normal', '""'].includes(getComputedStyle(e, '::after').content))
    ).map(e => {
        const s = getComputedStyle(e);
        let backgrounds = [];
        for (let p = e; p; p = p.parentElement) {
            const ps = getComputedStyle(p);
            if (ps.backgroundImage.includes('gradient')) {
                backgrounds = ps.backgroundImage.match(/rgb\\([^)]+\\)/g);
                break;
            }
            if (ps.backgroundColor !== 'rgba(0, 0, 0, 0)') {
                backgrounds = [ps.backgroundColor];
                break;
            }
        }
        return {text: (e.textContent.trim() || getComputedStyle(e, '::after').content).slice(0, 80), color: s.color, backgrounds,
            size: parseFloat(s.fontSize), weight: parseFloat(s.fontWeight), opacity: s.opacity};
    })""")
    for sample in samples:
        assert sample["color"] in COLORS.values(), sample
        assert sample["opacity"] == "1", sample
        assert sample["backgrounds"], sample
        minimum = 3 if sample["size"] >= 24 or (sample["size"] >= 18.66 and sample["weight"] >= 700) else 4.5
        sample["minimum"] = minimum
        sample["ratios"] = [round(contrast(sample["color"], bg), 3) for bg in sample["backgrounds"]]
        assert min(sample["ratios"]) >= minimum, sample
    assert samples
    return samples


def run(args, api, output):
    evidence = {"schemaVersion": 1, "workItem": "AB#960,AB#961,AB#966,AB#967",
                "sourceSha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "workingTreeDiff": subprocess.check_output(["git", "-c", "core.safecrlf=false", "diff", "--stat"], cwd=ROOT, text=True),
                "web": args.web_url, "api": api, "isolation": "Owned process; EF InMemory; SQL connection empty",
                "screenshots": [], "checks": [], "unexpectedRequests": []}
    origins = {args.web_url, api}
    with sync_playwright() as p:
        browser = p.chromium.launch(channel=args.channel, headless=True)
        evidence["browser"] = {"channel": args.channel, "version": browser.version}

        def context(width, height):
            ctx = browser.new_context(viewport={"width": width, "height": height}, reduced_motion="reduce")

            def guard(route):
                url = urlsplit(route.request.url)
                origin = f"{url.scheme}://{url.netloc}"
                if origin not in origins:
                    evidence["unexpectedRequests"].append(route.request.url)
                    route.abort()
                elif url.path == "/appsettings.json":
                    route.fulfill(json={"ApiBaseUrl": api + "/"})
                else:
                    route.continue_()
            ctx.route("**/*", guard)
            return ctx

        def capture(page, name, fixture=False):
            page.mouse.move(0, 0)
            page.evaluate("window.scrollTo(0, 0)")
            page.wait_for_timeout(200)  # Let Bootstrap's color transitions finish before sampling.
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), name
            samples = audit_text(page)
            filename = name + ".png"
            page.screenshot(path=str(output / filename), full_page=True)
            evidence["screenshots"].append({"file": filename, "fixture": fixture, "textSamples": samples})

        try:
            for size, width, height, days in [("desktop", 1440, 1000, 30), ("mobile", 390, 844, 40)]:
                ctx = context(width, height)
                page = ctx.new_page()
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                page.goto(args.web_url)
                expect(page.locator(".hotel-card")).to_have_count(2)
                expect(page.locator(".booking-panel")).to_have_count(0)
                expect(page.get_by_label("Hotel or destination", exact=True)).to_be_visible()
                expect(page.get_by_label("Check-in", exact=True)).to_be_visible()
                expect(page.get_by_label("Check-out", exact=True)).to_be_visible()
                expect(page.get_by_label("Guests", exact=True)).to_be_visible()
                expect(page.get_by_role("button", name="Check availability")).to_be_visible()
                assert page.get_by_label("Hotel or destination", exact=True).input_value()
                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
                assert all(page.get_by_label(name, exact=True).evaluate(
                    "e => e.getBoundingClientRect().bottom <= innerHeight")
                    for name in ["Hotel or destination", "Check-in", "Check-out", "Guests"])
                assert page.get_by_role("button", name="Check availability").evaluate(
                    "e => e.getBoundingClientRect().bottom <= innerHeight")
                capture(page, size + "-catalog")
                card_context = context(width, height)
                card_page = card_context.new_page()
                card_page.goto(args.web_url)
                expect(card_page.locator(".hotel-card")).to_have_count(2)
                expect(card_page.locator(".booking-panel")).to_have_count(0)
                hotel_id = card_page.get_by_label("Hotel or destination", exact=True).input_value()
                harbor_id = card_page.get_by_label("Hotel or destination", exact=True).locator("option").nth(1).get_attribute("value")
                card_check_in = (date.today() + timedelta(days=days + 2)).isoformat()
                card_check_out = (date.today() + timedelta(days=days + 5)).isoformat()
                card_page.get_by_label("Check-in", exact=True).fill(card_check_in)
                card_page.get_by_label("Check-out", exact=True).fill(card_check_out)
                card_page.get_by_label("Guests", exact=True).fill("3")
                held_availability = []
                card_page.route(
                    f"**/api/hotels/{harbor_id}/availability**",
                    lambda route: held_availability.append(route))
                with card_page.expect_request(
                    lambda request: f"/api/hotels/{harbor_id}/availability?" in request.url
                ):
                    card_page.get_by_role("button", name="View rooms at Harbor Light").click()
                assert len(held_availability) == 1
                expect(card_page.locator(".booking-panel")).to_have_count(0)
                with card_page.expect_response(
                    lambda response: f"/api/hotels/{harbor_id}/availability?" in response.url
                ) as card_availability:
                    held_availability.pop().continue_()
                card_response = card_availability.value
                assert card_response.status == 200
                assert parse_qs(urlsplit(card_response.url).query) == {
                    "checkIn": [card_check_in],
                    "checkOut": [card_check_out],
                    "guests": ["3"],
                }
                expect(card_page.get_by_label("Hotel or destination", exact=True)).to_have_value(harbor_id)
                expect(card_page.locator(".booking-panel h2")).to_have_text("Available rooms at Harbor Light")
                expect(card_page.locator(".room-option")).to_have_count(1)
                evidence["checks"].append({
                    "hotelCardAvailability": {
                        "hotel": "Harbor Light",
                        "hotelId": harbor_id,
                        "status": card_response.status,
                        "roomsShown": 1,
                        "usesCurrentStayAndGuestCount": True,
                    },
                    "availabilityPanelHiddenBeforeSearch": True,
                })
                with card_page.expect_request(
                    lambda request: f"/api/hotels/{harbor_id}/availability?" in request.url
                ):
                    card_page.get_by_role("button", name="View rooms at Harbor Light").click()
                assert len(held_availability) == 1
                expect(card_page.locator(".booking-panel")).to_have_count(0)
                card_page.get_by_label("Hotel or destination", exact=True).select_option(hotel_id)
                card_page.get_by_label("Guests", exact=True).fill("2")
                expect(card_page.locator(".booking-panel")).to_have_count(0)
                with card_page.expect_response(
                    lambda response: f"/api/hotels/{hotel_id}/availability?" in response.url
                ) as current_availability:
                    card_page.get_by_role("button", name="Check availability").click()
                assert current_availability.value.status == 200
                expect(card_page.locator(".booking-panel h2")).to_have_text("Available rooms at Canal House")
                expect(card_page.locator(".room-option")).to_have_count(2)
                with card_page.expect_response(
                    lambda response: f"/api/hotels/{harbor_id}/availability?" in response.url
                ) as stale_availability:
                    held_availability.pop().continue_()
                assert stale_availability.value.status == 200
                card_page.wait_for_timeout(100)
                expect(card_page.locator(".booking-panel h2")).to_have_text("Available rooms at Canal House")
                expect(card_page.locator(".room-option")).to_have_count(2)
                evidence["checks"].append({
                    "outOfOrderAvailability": {
                        "newerHotel": "Canal House",
                        "lateOlderResponseDiscarded": True,
                    }
                })
                capture(card_page, size + "-hotel-card-results")
                card_context.close()
                if size == "mobile":
                    toggle = page.get_by_role("button", name="Navigation menu")
                    toggle.focus()
                    page.keyboard.press("Enter")
                    expect(toggle).to_have_attribute("aria-expanded", "true")
                    expect(page.locator(".nav-link.active")).to_be_visible()
                    capture(page, size + "-navigation")
                active = page.locator(".nav-link.active")
                assert "underline" in styles(active)["textDecorationLine"]
                icon = styles(active.locator(".bi"))
                assert icon["backgroundColor"] == COLORS["dark"] and icon["maskImage"] != "none"
                page.keyboard.press("Tab")
                active.focus()
                assert styles(active)["outlineColor"] == COLORS["cream"]
                evidence["checks"].append(check_focus(active))
                page.keyboard.press("Tab")
                hotel_destination = page.get_by_label("Hotel or destination", exact=True)
                expect(hotel_destination).to_be_focused()
                evidence["checks"].append(check_focus(hotel_destination))
                assert hotel_destination.locator("option").count() == 2
                page.keyboard.press("ArrowDown")
                expect(hotel_destination).to_have_value("0e7b82cc-4ded-4707-82a4-1e80db408af9")
                page.keyboard.press("ArrowUp")
                expect(hotel_destination).to_have_value("3d41ab14-06c3-4c9a-bf29-61f4392f2227")
                page.keyboard.press("Tab")
                check_in = (date.today() + timedelta(days=days)).isoformat()
                check_out = (date.today() + timedelta(days=days + 3)).isoformat()
                check_in_input = page.get_by_label("Check-in", exact=True)
                expect(check_in_input).to_be_focused()
                check_in_input.fill(check_in)
                expect(check_in_input).to_be_focused()
                check_out_input = page.get_by_label("Check-out", exact=True)
                check_in_input.press("Tab")
                expect(check_out_input).to_be_focused()
                check_out_input.fill(check_out)
                expect(check_out_input).to_be_focused()
                guest_input = page.get_by_label("Guests", exact=True)
                check_out_input.press("Tab")
                expect(guest_input).to_be_focused()
                state = styles(guest_input)
                assert state["outlineColor"] == COLORS["blue"] and state["outlineWidth"] == "3px"
                evidence["checks"].append(check_focus(guest_input))
                evidence["checks"].append(check_pair(guest_input, "borderTopColor", "backgroundColor", 3))
                search = page.get_by_role("button", name="Check availability")
                guest_input.press("Tab")
                expect(search).to_be_focused()
                evidence["checks"].append(check_focus(search))
                capture(page, size + "-form-focus")
                page.keyboard.press("Enter")
                expect(page.locator(".room-option")).to_have_count(2)
                amenity_group = page.get_by_role("group", name="Filter available rooms by amenities")
                coffee = amenity_group.get_by_label("Coffee maker", exact=True)
                kitchenette = amenity_group.get_by_label("Kitchenette", exact=True)
                coffee.focus()
                page.keyboard.press("Space")
                expect(coffee).to_be_checked()
                expect(page.locator(".room-option")).to_have_count(1)
                kitchenette.focus()
                page.keyboard.press("Space")
                expect(kitchenette).to_be_checked()
                expect(page.locator(".room-option")).to_have_count(0)
                no_amenity_match = page.locator(".empty-state").filter(
                    has_text="Available rooms were found, but none match all selected amenities")
                expect(no_amenity_match).to_be_visible()
                clear_filters = page.get_by_role("button", name="Clear all filters")
                clear_filters.focus()
                page.keyboard.press("Enter")
                expect(coffee).not_to_be_checked()
                expect(kitchenette).not_to_be_checked()
                expect(page.locator(".room-option")).to_have_count(2)
                expect(clear_filters).to_be_disabled()
                evidence["checks"].append({
                    "selector": ".btn-outline-primary",
                    "state": "disabled empty-filter control",
                    **check_pair(clear_filters, "color", "backgroundColor"),
                })
                evidence["checks"].append({
                    "amenityFilters": "catalog-labeled, keyboard-operable, match-all, clear-all restores both rooms",
                    "noMatchMessageDistinctFromUnavailableDates": True})
                page.mouse.move(0, 0)
                room = page.locator(".room-option").first
                evidence["checks"].append(check_pair(room, "borderTopColor", "backgroundColor", 3))
                room.hover()
                assert styles(room)["backgroundColor"] == COLORS["taupe"]
                room.focus()
                page.keyboard.press("Space")
                expect(room).to_have_attribute("aria-pressed", "true")
                expect(room.locator(".room-selection")).to_have_text("Selected")
                assert styles(room)["backgroundColor"] == COLORS["light"]
                assert styles(room)["outlineColor"] == COLORS["blue"]
                evidence["checks"].append(check_focus(room))
                evidence["checks"].append(check_pair(room, "borderTopColor", "backgroundColor", 3))
                expect(page.locator(".stay-total")).to_contain_text(re.compile(r"567[,.]00"))
                vehicle = page.get_by_label("Vehicle preference", exact=True)
                expect(vehicle.locator("option")).to_have_text(
                    ["No preference", "Economy", "Compact", "SUV", "Luxury"])
                vehicle.select_option("Compact")
                vehicle.select_option("SUV")
                vehicle.select_option("")
                vehicle.select_option("Luxury")
                capture(page, size + "-search-selected")

                for selector in [".btn-primary", ".btn-success"]:
                    button = page.locator(selector).first
                    page.mouse.move(0, 0)
                    button.evaluate("e => e.blur()")
                    page.wait_for_timeout(200)
                    evidence["checks"].append({"selector": selector, "state": "default",
                                              **check_pair(button, "color", "backgroundColor")})
                    button.hover()
                    page.wait_for_timeout(200)
                    evidence["checks"].append({"selector": selector, "state": "hover",
                                              **check_pair(button, "color", "backgroundColor")})
                    page.mouse.down()
                    page.wait_for_timeout(200)
                    active_state = styles(button)
                    assert active_state["backgroundColor"] == COLORS["dark"], active_state
                    page.mouse.move(0, 0)
                    page.mouse.up()
                    button.focus()
                    page.keyboard.press("Tab")
                    page.keyboard.press("Shift+Tab")
                    focused = styles(button)
                    assert focused["outlineColor"] == COLORS["blue"] and focused["outlineWidth"] == "3px", focused
                    evidence["checks"].append(check_focus(button))
                    button.evaluate("e => e.disabled = true")
                    page.wait_for_timeout(200)
                    disabled = check_pair(button, "color", "backgroundColor")
                    assert disabled["styles"]["opacity"] == "1"
                    assert disabled["styles"]["backgroundColor"] == COLORS["taupe"]
                    evidence["checks"].append({"selector": selector, "state": "disabled DOM fixture", **disabled})
                    button.evaluate("e => e.disabled = false")

                confirm = page.get_by_role("button", name="Confirm booking")
                with page.expect_response(lambda r: r.url.endswith("/api/reservations")) as invalid:
                    confirm.click()
                assert invalid.value.status == 400
                expect(page.get_by_role("alert")).to_be_visible()
                capture(page, size + "-validation")
                page.get_by_label("Guest name", exact=True).fill("Isolated Brand Test")
                with page.expect_response(lambda r: r.url.endswith("/api/reservations")) as booked:
                    confirm.click()
                assert booked.value.status == 201
                reservation = booked.value.json()
                assert reservation["totalStayPrice"] == 567 and reservation["nights"] == 3
                assert reservation["vehiclePreference"] == "Luxury"
                expect(page.locator(".confirmation")).to_contain_text(reservation["reference"])
                expect(page.locator(".confirmation")).to_contain_text("Vehicle preference: Luxury")
                expect(page.locator(".confirmation")).to_contain_text("not a guaranteed rental")
                expect(vehicle).to_be_disabled()
                expect(confirm).to_be_disabled()
                capture(page, size + "-confirmation")
                duplicate = ctx.request.post(api + "/api/reservations", data={
                    "hotelId": reservation["hotelId"], "roomId": reservation["roomId"],
                    "checkIn": reservation["checkIn"], "checkOut": reservation["checkOut"],
                    "guests": reservation["guests"], "guestName": "Duplicate Browser Test",
                    "vehiclePreference": "Luxury"})
                assert duplicate.status == 409
                search.click()
                expect(page.locator(".room-option")).to_have_count(1)
                page.locator(".room-option").first.click()
                expect(page.get_by_label("Vehicle preference", exact=True)).to_be_enabled()
                expect(page.get_by_label("Vehicle preference", exact=True)).to_have_value("")
                expect(page.get_by_role("button", name="Confirm booking")).to_be_enabled()
                page.get_by_label("Guests", exact=True).fill("8")
                search.click()
                expect(page.locator(".empty-state")).to_contain_text("No rooms")
                capture(page, size + "-empty-availability")
                page.get_by_label("Check-out", exact=True).fill(check_in)
                search.click()
                expect(page.get_by_role("alert")).to_contain_text("Availability could not be checked")
                capture(page, size + "-invalid-dates")
                evidence["checks"].append({"viewport": size, "realApi": {
                    "validation": 400, "booking": 201, "overlap": 409,
                    "nights": 3, "totalStayPrice": 567, "reference": reservation["reference"],
                    "availabilityExcludesBookedRoom": True, "emptyAvailability": True}})

                page.goto(args.web_url + "/operations")
                expect(page.locator(".connection.live")).to_have_text("Live")
                capture(page, size + "-operations-before-event")
                response = ctx.request.post(api + "/api/operations/events", data={
                    "kind": 1, "correlationId": "brand-browser-" + size, "workItemId": "AB#960",
                    "agent": "browser-fixture", "summary": "Isolated visual test event, not agent execution",
                    "decision": "visual-test", "outcome": "fixture", "confidence": .95,
                    "durationMilliseconds": 20, "knowledgeRevision": evidence["sourceSha"]})
                assert response.status == 201
                expect(page.locator(".event-table")).to_contain_text("Isolated visual test")
                capture(page, size + "-operations-live")
                page.goto(args.web_url + "/unknown-brand-test")
                expect(page.get_by_role("heading", name="Not Found")).to_be_visible()
                capture(page, size + "-not-found")
                assert not errors, errors
                ctx.close()

                fixtures = context(width, height)
                fp = fixtures.new_page()
                held = []
                fixtures.route("**/api/hotels", lambda route: held.append(route))
                fp.goto(args.web_url)
                expect(fp.get_by_text("Loading hotels...", exact=True)).to_be_visible()
                capture(fp, size + "-loading-hotels", True)
                assert len(held) == 1
                held.pop().fulfill(json=[])
                expect(fp.locator(".empty-state")).to_contain_text("No hotels")
                capture(fp, size + "-empty-catalog", True)
                fixtures.unroute("**/api/hotels")
                fixtures.route("**/api/hotels", lambda r: r.fulfill(status=503, body="Isolated failure"))
                fp.reload()
                expect(fp.get_by_role("alert")).to_contain_text("Hotels could not be loaded")
                expect(fp.get_by_text("Loading hotels...", exact=True)).to_have_count(0)
                capture(fp, size + "-catalog-error", True)
                fixtures.route("**/api/operations/events?*", lambda r: r.fulfill(json=[]))
                held_negotiate = []
                fixtures.route("**/hubs/operations/negotiate?*", lambda r: held_negotiate.append(r))
                fp.goto(args.web_url + "/operations")
                expect(fp.locator(".connection.connecting")).to_have_text("Connecting")
                capture(fp, size + "-operations-connecting", True)
                fp.wait_for_timeout(200)
                assert held_negotiate
                held_negotiate.pop().fulfill(status=503, body="Isolated offline fixture")
                expect(fp.locator(".connection.offline")).to_have_text("Offline")
                capture(fp, size + "-operations-offline", True)
                fixtures.route("**/api/operations/events?*", lambda r: r.fulfill(status=500, body="Isolated render failure"))
                fp.reload()
                expect(fp.locator("#blazor-error-ui")).to_be_visible()
                fp.get_by_role("button", name="Dismiss error").focus()
                capture(fp, size + "-template-error", True)
                fp.get_by_role("button", name="Dismiss error").press("Enter")
                expect(fp.locator("#blazor-error-ui")).not_to_be_visible()
                # These template classes have no routed component in this MVP.
                fp.locator("main article.content").evaluate("""e => e.innerHTML = `
                    <h1>Template state fixtures</h1>
                    <div class="blazor-error-boundary"></div>
                    <div class="booking-fields">
                        <label>Invalid field<input class="invalid" value="Invalid input" aria-invalid="true"></label>
                        <label>Valid field<input class="valid modified" value="Valid input"></label>
                    </div><p class="validation-message">Please correct the invalid field.</p>`""")
                capture(fp, size + "-template-validation-boundary", True)
                fixtures.close()

                loader_context = context(width, height)
                loader_context.route("**/_framework/**", lambda r: r.abort())
                lp = loader_context.new_page()
                lp.goto(args.web_url, wait_until="domcontentloaded")
                expect(lp.locator(".loading-progress")).to_be_visible()
                assert lp.locator(".loading-progress circle").last.evaluate("e => getComputedStyle(e).stroke") == COLORS["blue"]
                capture(lp, size + "-startup-loader", True)
                loader_context.close()
            assert not evidence["unexpectedRequests"], evidence["unexpectedRequests"]
            narrow_context = context(320, 780)
            narrow_page = narrow_context.new_page()
            narrow_page.goto(args.web_url)
            narrow_fields = [
                narrow_page.get_by_label(field, exact=True)
                for field in ["Hotel or destination", "Check-in", "Check-out", "Guests"]
            ]
            narrow_fields.append(narrow_page.get_by_role("button", name="Check availability"))
            for field in narrow_fields:
                expect(field).to_be_visible()
            assert narrow_page.evaluate("document.documentElement.scrollWidth <= innerWidth")
            bounds = [field.bounding_box() for field in narrow_fields]
            assert all(
                box and box["x"] >= 0 and box["x"] + box["width"] <= 320 and
                box["y"] >= 0 and box["y"] + box["height"] <= 780
                for box in bounds
            ), bounds
            assert all(
                first["y"] + first["height"] <= second["y"]
                for first, second in zip(bounds, bounds[1:])
            ), bounds
            assert all(
                first["y"] + first["height"] <= second["y"] or
                second["y"] + second["height"] <= first["y"] or
                first["x"] + first["width"] <= second["x"] or
                second["x"] + second["width"] <= first["x"]
                for index, first in enumerate(bounds)
                for second in bounds[index + 1:]
            ), bounds
            evidence["checks"].append({
                "viewport": "narrow-320px",
                "fiveSearchControlsWithinViewport": True,
                "searchControlsInVerticalOrder": True,
                "searchControlsDoNotOverlap": True,
                "documentHorizontalOverflow": False,
            })
            capture(narrow_page, "narrow-search-first-viewport")
            narrow_context.close()
            tablet_context = context(1024, 900)
            tablet_page = tablet_context.new_page()
            tablet_page.goto(args.web_url)
            expect(tablet_page.get_by_label("Hotel or destination", exact=True)).to_be_visible()
            expect(tablet_page.get_by_role("button", name="Check availability")).to_be_visible()
            assert tablet_page.evaluate("document.documentElement.scrollWidth <= innerWidth")
            tablet_fields = [
                tablet_page.get_by_label(field, exact=True)
                for field in ["Hotel or destination", "Check-in", "Check-out", "Guests"]
            ]
            tablet_fields.append(tablet_page.get_by_role("button", name="Check availability"))
            bounds = [field.bounding_box() for field in tablet_fields]
            assert all(box and box["x"] >= 0 and box["x"] + box["width"] <= 1024 for box in bounds), bounds
            assert all(
                first["y"] + first["height"] <= second["y"] or
                second["y"] + second["height"] <= first["y"] or
                first["x"] + first["width"] <= second["x"] or
                second["x"] + second["width"] <= first["x"]
                for index, first in enumerate(bounds)
                for second in bounds[index + 1:]
            ), bounds
            evidence["checks"].append({
                "viewport": "tablet-1024px",
                "searchFieldsWithinViewport": True,
                "searchFieldsDoNotOverlap": True,
                "documentHorizontalOverflow": False,
            })
            capture(tablet_page, "tablet-search-first-viewport")
            tablet_context.close()
            evidence["status"] = "passed"
        except Exception as error:
            evidence["status"] = "failed"
            evidence["error"] = repr(error)
            evidence["errorMessage"] = str(error)
            evidence["traceback"] = traceback.format_exc()
            for index, ctx in enumerate(browser.contexts):
                for page_index, open_page in enumerate(ctx.pages):
                    open_page.screenshot(path=str(output / f"failure-{index}-{page_index}.png"), full_page=True)
                    evidence.setdefault("failurePages", []).append(open_page.locator("body").inner_text())
            raise
        finally:
            (output / "browser-evidence.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
            browser.close()
    print(f"PASS: {len(evidence['screenshots'])} screenshots; {len(evidence['checks'])} state/API checks")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--web-url", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--channel", default="msedge")
    args = parser.parse_args()
    args.web_url = args.web_url.rstrip("/")
    url = urlsplit(args.web_url)
    if url.scheme != "http" or url.hostname not in ("localhost", "127.0.0.1") or not url.port or url.port == 5051:
        parser.error("Use a separate loopback HTTP web server, never the user-owned port 5051.")
    args.output.mkdir(parents=True, exist_ok=True)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    api = f"http://127.0.0.1:{port}"
    dll = ROOT / "src/Api/bin/Debug/net10.0/AgenticHotelBooking.Api.dll"
    if not dll.is_file():
        parser.error("Build the solution in Debug before running browser verification.")
    with (args.output / "isolated-api.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen([
            "dotnet", str(dll), "--environment=Development", "--urls=" + api,
            "--ConnectionStrings:HotelBooking=", "--Database:ApplyMigrations=true",
            "--AllowedOrigins:0=" + args.web_url, "--MicrosoftRouting:ModelEnabled=false",
            "--RoutingEvaluation:Enabled=false"], cwd=ROOT / "src/Api", stdout=log, stderr=log)
        try:
            for attempt in range(100):
                if process.poll() is not None:
                    raise RuntimeError("Isolated API exited; inspect isolated-api.log.")
                try:
                    with urlopen(api + "/health", timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    time.sleep(.1)
            else:
                raise RuntimeError("Isolated API did not become healthy.")
            run(args, api, args.output)
        finally:
            process.terminate()
            process.wait(timeout=15)


if __name__ == "__main__":
    main()
