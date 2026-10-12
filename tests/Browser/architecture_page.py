"""Focused responsive/accessibility smoke for the Hotel Architecture page.

Start the Web project on loopback, then run:
python tests/Browser/architecture_page.py --web-url http://127.0.0.1:5167

The page is static and makes no API calls. The script targets only the supplied loopback URL.
"""

import argparse
import shutil
from urllib.parse import urlsplit

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
                response = page.goto(f"{base}/architecture", wait_until="networkidle")
                assert response is not None and response.status == 200, name

                expect(page.get_by_role("heading", name="How Hotel works", level=1)).to_be_visible()
                if name == "mobile":
                    page.get_by_role("button", name="Navigation menu").click()
                expect(page.get_by_role("link", name="Architecture")).to_be_visible()
                expect(page.get_by_role("list", name="Guest booking request flow")).to_be_visible()
                expect(page.get_by_role("list", name="Runtime routing flow")).to_be_visible()
                expect(page.get_by_role("list", name="Live operations event flow")).to_be_visible()
                expect(page.get_by_role("list", name="Software delivery flow")).to_be_visible()
                expect(page.get_by_text("This is a system overview, not a live traffic monitor.")).to_be_visible()

                flow_links = page.locator(".flow-link")
                assert flow_links.count() > 0
                for index in range(flow_links.count()):
                    expect(flow_links.nth(index)).to_have_attribute("aria-hidden", "true")
                    animation = flow_links.nth(index).evaluate(
                        "element => getComputedStyle(element, '::before').animationName"
                    )
                    assert animation == "none", (name, animation)

                if name == "desktop":
                    cards = page.locator(".flow-track").first.locator(".node-card")
                    first = cards.nth(0).bounding_box()
                    second = cards.nth(1).bounding_box()
                    assert first and second and second["x"] > first["x"], (first, second)
                else:
                    cards = page.locator(".flow-track").first.locator(".node-card")
                    first = cards.nth(0).bounding_box()
                    second = cards.nth(1).bounding_box()
                    assert first and second and second["y"] > first["y"], (first, second)

                no_horizontal_scroll = page.evaluate(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                )
                assert no_horizontal_scroll, name
                assert not errors, errors
                print(f"{name}: architecture headings, named flows, reduced motion, and layout passed")
                page.close()

            motion_page = browser.new_page(viewport={"width": 1440, "height": 1000})
            motion_page.emulate_media(reduced_motion="no-preference")
            motion_page.goto(f"{base}/architecture", wait_until="networkidle")
            animation = motion_page.locator(".flow-link").first.evaluate(
                "element => getComputedStyle(element, '::before').animationName"
            )
            assert animation.startswith("flow-dot"), animation
            print("motion: decorative flow dot animation is available when motion is allowed")
            motion_page.close()
        finally:
            browser.close()
if __name__ == "__main__":
    main()
