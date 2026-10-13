"""Responsive/accessibility smoke for admin pages, with a fully mocked delivery API.

Start the Web project on loopback, then run:
python tests/Browser/architecture_page.py --web-url http://127.0.0.1:5167

Delivery API calls are intercepted and answered from an in-memory fixture; no GitHub, Azure Boards,
or other live service is accessed. The public home page may call the isolated test API, but the
script never submits a booking. It targets only the supplied loopback URL and does not inspect or
mutate a shared deployment.
"""

import argparse
import json
import shutil
from urllib.parse import parse_qs, urlsplit

from playwright.sync_api import expect, sync_playwright


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--web-url", required=True)
    args = parser.parse_args()
    base = args.web_url.rstrip("/")
    parsed = urlsplit(base)
    if parsed.scheme != "http" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
        raise ValueError("--web-url must use an isolated loopback HTTP address")

    executable = shutil.which("chromium") or shutil.which("chromium-browser")
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(
            executable_path=executable,
            headless=True,
            args=["--no-sandbox"],
        )
        try:
            for name, width, height in [("desktop", 1440, 1000), ("mobile", 390, 844)]:
                page = browser.new_page(viewport={"width": width, "height": height})
                page.emulate_media(reduced_motion="reduce")
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))

                def mock_delivery_status(route):
                    status_names = [
                        "Passed", "Not started", "Running", "Passed", "Waiting", "Failed",
                        "Waiting", "Not started", "Skipped", "Not started", "Waiting",
                    ]
                    stage_names = [
                        "Boards intake", "GitHub issue and cloud agent", "Pull request", "PR validation",
                        "Copilot review gate", "QA evidence", "Merge", "Development deployment",
                        "Lifecycle sync and delivery evidence", "Release proposal", "Human production approval",
                    ]
                    stages = [
                        {
                            "name": stage,
                            "status": status,
                            "updatedAtUtc": "2026-10-13T10:00:00Z" if index < 7 else None,
                            "durationMilliseconds": 30000 if index == 3 else None,
                            "url": "https://github.com/cihanduruer/agentic-development/actions/runs/123" if index == 3 else None,
                            "headSha": "0123456789abcdef0123456789abcdef01234567" if index == 3 else None,
                            "relatedEvidence": [],
                        }
                        for index, (stage, status) in enumerate(zip(stage_names, status_names))
                    ]
                    delivery = {
                        "workItemId": 965,
                        "title": "AB#965: delivery status",
                        "gitHubIssueTitle": "AB#965: delivery status",
                        "gitHubIssueUrl": "https://github.com/cihanduruer/agentic-development/issues/42",
                        "gitHubIssueNumber": 42,
                        "updatedAtUtc": "2026-10-13T10:00:00Z",
                        "overallStatus": "Running",
                        "pullRequestTitle": "AB#965: delivery status",
                        "pullRequestNumber": 43,
                        "pullRequestUrl": "https://github.com/cihanduruer/agentic-development/pull/43",
                        "stages": stages,
                        "boardsWorkItem": None,
                    }
                    query = parse_qs(urlsplit(route.request.url).query)
                    selected = delivery if query.get("workItemId") == ["965"] else None
                    payload = {
                        "refreshedAtUtc": "2026-10-13T10:20:00Z",
                        "isStale": False,
                        "unreachableMessage": None,
                        "boardsConnection": "Not connected (reason)",
                        "deliveries": [delivery],
                        "selectedDelivery": selected,
                    }
                    route.fulfill(status=200, content_type="application/json", body=json.dumps(payload))

                page.goto(f"{base}/", wait_until="networkidle")
                expect(page.get_by_role("heading", name="Find a room. Book with confidence.", level=1)).to_be_visible()
                expect(page).to_have_title("Hotel Booking")
                if name == "mobile":
                    page.get_by_role("button", name="Navigation menu").click()
                expect(page.locator(".sidebar nav").get_by_role("link", name="Home")).to_be_visible()
                assert page.locator(".sidebar nav").get_by_role("link").count() == 1
                public_copy = page.locator("body").inner_text().lower()
                for forbidden_copy in ("agentic", "agent operations", "architecture", "delivery", "demo", "agents", "sample hotel"):
                    assert forbidden_copy not in public_copy, (name, forbidden_copy)

                response = page.goto(f"{base}/admin/architecture", wait_until="networkidle")
                assert response is not None and response.status == 200, name
                expect(page.get_by_role("heading", name="Hotel services and delivery path", level=1)).to_be_visible()
                expect(page.get_by_role("navigation", name="Administration")).to_be_visible()
                expect(page.get_by_text("no sign-in or authorization configured", exact=False)).to_be_visible()
                if name == "mobile":
                    page.get_by_role("button", name="Navigation menu").click()
                expect(page.get_by_role("list", name="Guest booking service path")).to_be_visible()
                telemetry_path = page.get_by_role("list", name="API telemetry configuration path")
                expect(telemetry_path).to_be_visible()
                expect(telemetry_path.get_by_role("listitem")).to_have_count(3)
                for node in ("App Service API", "Application Insights", "Log Analytics workspace"):
                    expect(telemetry_path.get_by_role("heading", name=node)).to_be_visible()
                expect(page.get_by_role("list", name="Runtime routing service path")).to_be_visible()
                delivery_path = page.get_by_role("list", name="Azure Boards and GitHub delivery path")
                expect(delivery_path).to_be_visible()
                expect(delivery_path.get_by_role("listitem")).to_have_count(5)
                for node in (
                    "Azure Boards", "GitHub Actions intake", "GitHub issue + cloud agent",
                    "Pull request + evidence gates", "Development deployment"
                ):
                    expect(delivery_path.get_by_role("heading", name=node)).to_be_visible()
                service_cards = page.get_by_role("list", name="Repository-declared service tiers and responsibilities")
                expect(service_cards).to_be_visible()
                expect(service_cards.get_by_role("listitem")).to_have_count(7)
                expect(page.get_by_text("Numbered flow explanations")).to_be_visible()
                expect(page.get_by_text("End-to-end delivery flow")).to_be_visible()

                public_nav = page.locator(".sidebar nav")
                expect(public_nav.get_by_role("link", name="Home")).to_be_visible()
                assert public_nav.get_by_role("link").count() == 1, "public navigation must remain booking-only"

                connectors = page.locator(".service-connector")
                assert connectors.count() > 0
                for index in range(connectors.count()):
                    label_element = connectors.nth(index).locator(".connector-label")
                    expect(label_element).to_be_visible()
                    label = label_element.inner_text().strip()
                    assert " · " in label and label.split(" · ", maxsplit=1)[0].isdigit(), label
                    animation = connectors.nth(index).evaluate(
                        "element => getComputedStyle(element, '::after').animationName"
                    )
                    assert animation == "none", (name, animation)

                page.get_by_role("link", name="Delivery overview").focus()
                page.keyboard.press("Tab")
                focus_outline = page.get_by_role("link", name="Hotel architecture").evaluate(
                    "element => getComputedStyle(element).outlineStyle"
                )
                assert focus_outline != "none", (name, focus_outline)

                page.route("**/api/delivery/status*", mock_delivery_status)
                overview = page.goto(f"{base}/admin", wait_until="networkidle")
                assert overview is not None and overview.status == 200, name
                expect(page.get_by_role("heading", name="Delivery overview", level=1)).to_be_visible()
                expect(page.get_by_label("Select a recent delivery")).to_be_visible()
                expect(page.locator(".delivery-stage")).to_have_count(11)
                expect(page.locator(".delivery-message").get_by_text("Not connected (reason)", exact=False)).to_be_visible()
                flow = page.get_by_role("list", name="End-to-end status flow")
                expect(flow.get_by_role("listitem")).to_have_count(11)
                expect(flow.get_by_text("Running", exact=True)).to_be_visible()
                expect(flow.get_by_text("Failed", exact=True)).to_be_visible()
                expect(flow.get_by_text("Skipped", exact=True)).to_be_visible()
                running_animation = flow.locator(".delivery-status-badge.running").evaluate(
                    "element => getComputedStyle(element).animationName"
                )
                assert running_animation == "none", (name, running_animation)
                expect(flow.get_by_role("link", name="Open evidence")).to_be_visible()
                expect(page.get_by_role("link", name="GitHub issue #42")).to_be_visible()
                expect(page.get_by_role("link", name="Pull request #43 · AB#965: delivery status")).to_be_visible()
                expect(flow.locator("time").first).to_have_attribute("datetime", "2026-10-13T10:00:00.0000000+00:00")

                no_horizontal_scroll = page.evaluate(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                )
                assert no_horizontal_scroll, name
                assert not errors, errors
                print(f"{name}: route, mocked live status, accessibility, responsive layout, and reduced motion passed")
                page.close()

            redirect_page = browser.new_page(viewport={"width": 1280, "height": 900})
            redirect_page.goto(f"{base}/architecture", wait_until="networkidle")
            expect(redirect_page).to_have_url(f"{base}/admin/architecture")
            print("redirect: /architecture resolves to /admin/architecture")

            not_found_page = browser.new_page(viewport={"width": 1280, "height": 900})
            not_found_page.goto(f"{base}/an-unknown-route", wait_until="networkidle")
            expect(not_found_page.get_by_role("heading", name="Not Found")).to_be_visible()
            print("routing: unknown paths continue to show Not Found")

            motion_page = browser.new_page(viewport={"width": 1440, "height": 1000})
            motion_page.emulate_media(reduced_motion="no-preference")
            motion_page.goto(f"{base}/admin/architecture", wait_until="networkidle")
            animation = motion_page.locator(".service-connector").first.evaluate(
                "element => getComputedStyle(element, '::after').animationName"
            )
            assert animation.startswith("service-dot"), animation
            print("motion: decorative service connector animation is available when motion is allowed")
        finally:
            browser.close()


if __name__ == "__main__":
    main()
