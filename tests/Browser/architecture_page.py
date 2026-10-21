"""Responsive/accessibility smoke for the admin pages.

Start the Web project on loopback, then run:
python tests/Browser/architecture_page.py --web-url http://127.0.0.1:5167

The public home page may call the isolated test API, but the script never submits a booking. It
targets only the supplied loopback URL and does not inspect or mutate a shared deployment.
"""

import argparse
import json
import re
import shutil
import sys
from urllib.parse import parse_qs, urlsplit

from playwright.sync_api import expect, sync_playwright


def check_knowledge_management(page, base):
    """Exercise destructive UI paths against in-memory HTTP fixtures only."""
    content = "Full indexed text\n" + ("Long content.\n" * 100) + "\nPREVIEW_END <img src=x>"
    entries = [
        {"id": "product", "path": "docs/knowledge/product.md", "chunkIndex": 0, "content": "Repository knowledge"},
        {"id": "notes-1", "path": "uploads/notes.txt", "chunkIndex": 1, "content": "Second block"},
        {"id": "notes-0", "path": "uploads/notes.txt", "chunkIndex": 0, "content": content},
        {"id": "other", "path": "uploads/other.txt", "chunkIndex": 0, "content": "Keep this source"},
    ]
    state = {"enabled": True, "fail_delete": False, "fail_preview": False, "delete_calls": 0, "preview_calls": 0}
    originals = {}

    def block(entry, preview=False):
        return {
            **entry, "title": f"Block {entry['chunkIndex']}", "area": "test", "sourceType": "txt",
            "characters": len(entry["content"]), "excerpt": entry["content"][:80],
            "lastReviewed": "2026-10-20", "pending": False,
            "hasOriginal": entry["path"] in originals,
            "content": entry["content"] if preview else None,
            "renderedHtml": "<h1>Repository knowledge</h1>" if preview and entry["path"].endswith(".md") else None,
        }

    def handle(route):
        request = route.request
        uri = urlsplit(request.url)
        headers = {
            "Access-Control-Allow-Origin": "*",
            "Access-Control-Allow-Methods": "GET, DELETE, OPTIONS",
            "Access-Control-Allow-Headers": "*",
        }

        def respond(payload, status=200):
            route.fulfill(status=status, content_type="application/json", headers=headers, body=json.dumps(payload))

        if request.method == "OPTIONS":
            respond({})
        elif uri.path.endswith("/capabilities"):
            respond({"canManageUploads": state["enabled"]})
        elif uri.path.endswith("/blocks"):
            respond([block(entry) for entry in entries])
        elif uri.path.endswith("/original"):
            path = parse_qs(uri.query).get("path", [""])[0]
            original = originals[path]
            route.fulfill(content_type="application/pdf", headers=headers, body=original)
        elif uri.path.endswith("/preview"):
            state["preview_calls"] += 1
            if state["fail_preview"]:
                respond({"title": "Fixture preview failed."}, 502)
                return
            path = parse_qs(uri.query).get("path", [""])[0]
            respond([block(entry, preview=True) for entry in entries if entry["path"] == path])
        elif uri.path.endswith("/rebuild") and request.method == "POST":
            respond({"rebuilt": len(entries)})
        elif uri.path.endswith("/documents") and request.method == "DELETE":
            state["delete_calls"] += 1
            if state["fail_delete"]:
                respond({"title": "Fixture deletion failed."}, 502)
                return
            path = parse_qs(uri.query).get("path", [""])[0]
            removed = sum(entry["path"] == path for entry in entries)
            entries[:] = [entry for entry in entries if entry["path"] != path]
            respond({"removed": removed})
        else:
            raise AssertionError(f"Unexpected knowledge request: {request.method} {uri.path}")

    page.route("**/api/knowledge/**", handle)
    try:
        page.goto(f"{base}/admin/knowledge", wait_until="networkidle")
        expect(page).to_have_title("Knowledge Center")
        expect(page.get_by_role("heading", name="Knowledge Center", exact=True, level=1)).to_be_visible()
        expect(page.get_by_text("4 blocks from 3 sources", exact=True)).to_be_visible()
        expect(page.get_by_role("heading", name="Knowledge center (RAG)", exact=True)).to_have_count(0)
        expect(page.get_by_role("group", name="Add content", exact=True)).to_have_count(0)
        if page.viewport_size["width"] >= 1000:
            assert page.locator(".kc-workspace").bounding_box()["y"] < 300, "workspace must not be pushed down by repeated headers"
        page.get_by_role("button", name="Add content", exact=True).click()
        expect(page.get_by_role("group", name="Add content", exact=True)).to_be_visible()
        page.get_by_role("button", name="Close add content", exact=True).click()
        expect(page.get_by_role("group", name="Add content", exact=True)).to_have_count(0)
        expect(page.get_by_role("heading", name="Conversation", exact=True)).to_have_count(0)
        assert state["preview_calls"] == 0

        preview_button = page.get_by_role("button", name="Preview notes.txt", exact=True)
        preview_button.focus()
        preview_button.press("Enter")
        preview = page.get_by_role("region", name="Indexed text preview")
        expect(preview).to_be_visible()
        expect(preview.get_by_role("heading", level=4)).to_have_count(0)
        expect(preview.locator("pre").first).to_have_text(content)
        expect(preview.locator("img")).to_have_count(0)
        assert page.evaluate("document.documentElement.scrollHeight <= window.innerHeight + 1"), "page must not scroll behind the document viewer"
        assert preview.evaluate("element => element.scrollHeight > element.clientHeight"), "long documents must scroll within the viewer"
        preview_button.press("Enter")
        expect(preview).to_be_visible()
        expect(preview_button).to_have_attribute("aria-pressed", "true")
        page.get_by_role("button", name="Indexed blocks", exact=True).click()
        expect(preview.locator(".kc-block-label")).to_have_count(2)
        expect(preview.get_by_role("heading", level=4)).to_have_text(["Block 0", "Block 1"])
        page.get_by_role("button", name="Document", exact=True).click()
        expect(preview.get_by_role("heading", level=4)).to_have_count(0)

        repository = page.get_by_role("button", name="Preview product.md", exact=True)
        repository.click()
        expect(page.get_by_text("Repository knowledge: protected from deletion")).to_have_count(0)
        expect(page.get_by_role("button", name="Delete product.md", exact=True)).to_be_enabled()
        expect(preview.get_by_role("heading", name="Repository knowledge", exact=True, level=1)).to_be_visible()
        preview_button.click()

        page.get_by_role("button", name="Delete notes.txt", exact=True).click()
        confirmation = page.get_by_role("group", name="Confirm source deletion")
        expect(confirmation).to_be_visible()
        assert state["delete_calls"] == 0
        confirmation.get_by_role("button", name="Cancel", exact=True).click()
        expect(confirmation).to_have_count(0)
        assert state["delete_calls"] == 0
        expect(page.get_by_text("4 blocks from 3 sources", exact=True)).to_be_visible()

        state["fail_delete"] = True
        page.get_by_role("button", name="Delete notes.txt", exact=True).click()
        confirmation.get_by_role("button", name="Confirm delete", exact=True).click()
        expect(page.get_by_role("alert")).to_have_text("Fixture deletion failed.")
        expect(preview_button).to_be_visible()
        state["fail_delete"] = False
        confirmation.get_by_role("button", name="Confirm delete", exact=True).click()
        expect(page.get_by_text("Deleted notes.txt and 2 indexed blocks.", exact=True)).to_be_visible()
        expect(page.get_by_text("2 blocks from 2 sources", exact=True)).to_be_visible()
        expect(preview_button).to_have_count(0)
        expect(page.get_by_role("button", name="Preview other.txt", exact=True)).to_be_visible()
        expect(repository).to_be_visible()

        page.get_by_role("button", name="Rebuild index", exact=True).click()
        rebuild = page.get_by_role("group", name="Confirm index rebuild")
        rebuild.get_by_role("button", name="Cancel", exact=True).click()
        expect(rebuild).to_have_count(0)
        page.get_by_role("button", name="Rebuild index", exact=True).click()
        rebuild.get_by_role("button", name="Confirm rebuild", exact=True).click()
        expect(page.get_by_text("Search index rebuilt from retained documents.", exact=True)).to_be_visible()
        expect(page.get_by_text("2 blocks from 2 sources", exact=True)).to_be_visible()
        repository.click()
        page.get_by_role("button", name="Delete product.md", exact=True).click()
        confirmation.get_by_role("button", name="Confirm delete", exact=True).click()
        expect(repository).to_have_count(0)
        expect(page.get_by_role("button", name="Preview other.txt", exact=True)).to_be_visible()

        state["enabled"] = False
        page.get_by_role("button", name="Refresh", exact=True).click()
        expect(page.get_by_text("Preview is available. Uploading, approval and deletion are disabled in this environment.")).to_be_visible()
        expect(page.get_by_role("button", name="Add content", exact=True)).to_be_disabled()
        expect(page.get_by_role("button", name="Preview other.txt", exact=True)).to_be_enabled()
        state["fail_preview"] = True
        page.get_by_role("button", name="Preview other.txt", exact=True).click()
        expect(page.get_by_role("button", name="Delete other.txt", exact=True)).to_be_disabled()
        expect(page.get_by_role("alert")).to_have_text("Fixture preview failed.")
        state["fail_preview"] = False
        page.get_by_role("button", name="Retry preview", exact=True).click()
        expect(page.get_by_role("region", name="Indexed text preview").locator("pre")).to_have_text("Keep this source")
        assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")

        entries.append({"id": "pdf", "path": "uploads/manual.pdf", "chunkIndex": 0, "content": "Indexed PDF text"})
        page.get_by_role("button", name="Refresh", exact=True).click()
        page.get_by_role("searchbox", name="Find a document", exact=True).fill("manual")
        expect(repository).to_have_count(0)
        page.get_by_role("button", name="Preview manual.pdf", exact=True).click()
        expect(page.get_by_text("Original PDF unavailable. Showing the complete indexed text below.")).to_be_visible()
        pdf_input = page.locator(".kc-pdf-tools input[type=file]")
        pdf_input.set_input_files({"name": "fake.pdf", "mimeType": "application/pdf", "buffer": b"not a PDF"})
        expect(page.get_by_role("alert")).to_have_text("That file is not a PDF.")

        objects = [
            b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R >>",
            b"<< /Length 0 >>\nstream\n\nendstream",
        ]
        pdf = b"%PDF-1.4\n"
        offsets = []
        for index, obj in enumerate(objects, 1):
            offsets.append(len(pdf))
            pdf += f"{index} 0 obj\n".encode() + obj + b"\nendobj\n"
        start = len(pdf)
        pdf += b"xref\n0 5\n0000000000 65535 f \n"
        pdf += b"".join(f"{offset:010} 00000 n \n".encode() for offset in offsets)
        pdf += f"trailer\n<< /Root 1 0 R /Size 5 >>\nstartxref\n{start}\n%%EOF\n".encode()
        pdf_input.set_input_files({"name": "manual.pdf", "mimeType": "application/pdf", "buffer": pdf})
        expect(page.get_by_title("PDF document preview", exact=True)).to_be_visible()
        expect(page.get_by_title("PDF document preview", exact=True)).to_have_attribute("src", re.compile(r"^data:application/pdf;base64,"))
        # Native PDF viewer internals are not exposed consistently by headless browsers.
        print("pdf: local selection, validation and iframe binding passed; native viewer internals not inspected")
        page.get_by_role("button", name="Indexed blocks", exact=True).click()
        expect(page.get_by_role("region", name="Indexed text preview").locator("pre")).to_have_text("Indexed PDF text")
        assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")

        originals[entries[-1]["path"]] = pdf
        page.get_by_role("button", name="Refresh", exact=True).click()
        page.get_by_role("button", name="Preview manual.pdf", exact=True).click()
        expect(page.get_by_text("Stored original: manual.pdf.", exact=True)).to_be_visible()
        expect(page.get_by_title("PDF document preview", exact=True)).to_have_attribute("src", re.compile(r"^data:application/pdf;base64,"))
        print("pdf: retained original retrieval and automatic iframe binding passed")

        def chat(route):
            route.fulfill(content_type="application/json", headers={"Access-Control-Allow-Origin": "*"}, body=json.dumps({
                "reply": "Fixture answer from the knowledge center.",
                "draft": {"title": "Fixture requirement", "description": "A fixture description.", "acceptanceCriteria": ["A fixture criterion."]},
                "readyForCreation": True, "openQuestions": [],
                "sources": [{"title": "Product", "path": "docs/knowledge/product.md", "excerpt": "Repository knowledge"}],
            }))

        page.route("**/api/work-item-chat", chat)
        page.get_by_role("navigation", name="Administration").get_by_role("link", name="AI Product Management", exact=True).click()
        expect(page.get_by_role("heading", name="Conversation", exact=True)).to_be_visible()
        expect(page.get_by_role("region", name="Knowledge center (RAG)")).to_have_count(0)
        page.get_by_role("textbox", name="Message to the product assistant", exact=True).fill("Draft a fixture requirement.")
        page.get_by_role("button", name="Send", exact=True).click()
        expect(page.get_by_text("Fixture answer from the knowledge center.", exact=True)).to_be_visible()
        expect(page.locator("#draft-title-input")).to_have_value("Fixture requirement")
        expect(page.locator("#draft-criteria")).to_have_value("A fixture criterion.")
        page.locator(".pm-answer-sources summary").click()
        expect(page.locator(".pm-answer-sources")).to_contain_text("Repository knowledge")
        page.get_by_role("button", name="Create in Azure Boards", exact=True).click()
        expect(page.get_by_role("group", name="Confirm creation", exact=True)).to_be_visible()
        page.get_by_role("button", name="Back to editing", exact=True).click()
        page.unroute("**/api/work-item-chat", chat)
        print("knowledge: separate page, full preview, confirm/cancel, isolated delete, failure and disabled states passed")
    finally:
        page.unroute("**/api/knowledge/**", handle)


def check_knowledge_sign_in(page, base):
    """Exercise the shared administrator login without real codes, mail or bookings."""
    state = {"session_valid": True}

    def handle(route):
        uri = urlsplit(route.request.url)
        headers = {"Access-Control-Allow-Origin": "*", "Access-Control-Allow-Methods": "GET, POST, OPTIONS", "Access-Control-Allow-Headers": "*"}

        def respond(payload, status=200):
            route.fulfill(status=status, content_type="application/json", headers=headers, body=json.dumps(payload))

        if route.request.method == "OPTIONS":
            respond({})
        elif uri.path.endswith("/capabilities"):
            respond({"canManageUploads": True, "requiresSignIn": True, "emailSignInEnabled": True})
        elif uri.path.endswith("/session"):
            if state["session_valid"] and route.request.headers.get("authorization"):
                respond({"email": "admin@example.test", "expiresAt": "2035-01-01T00:00:00Z"})
            else:
                respond({"title": "Sign in to continue."}, 401)
        elif uri.path.endswith("/request-code"):
            respond({"message": "If approved, a code will arrive shortly."}, 202)
        elif uri.path.endswith("/verify-code"):
            if route.request.post_data_json["code"] == "123456":
                respond({"token": "fixture-session", "expiresAt": "2035-01-01T00:00:00Z"})
            else:
                respond({"title": "The code is invalid, expired or already used."}, 401)
        elif uri.path.endswith("/blocks"):
            if state["session_valid"] and route.request.headers.get("authorization") == "Bearer fixture-session":
                respond([])
            else:
                respond({"title": "Sign in to continue."}, 401)
        else:
            raise AssertionError(f"Unexpected sign-in fixture route: {uri.path}")

    page.route("**/api/admin/auth/**", handle)
    page.route("**/api/knowledge/**", handle)
    try:
        for path in ("/admin", "/admin/architecture", "/admin/operations", "/architecture", "/operations"):
            page.goto(f"{base}{path}", wait_until="networkidle")
            expect(page).to_have_url(re.compile(r"/admin/login\?returnUrl="))
            expect(page.get_by_role("heading", name="Welcome back", exact=True)).to_be_visible()
            expect(page.get_by_role("navigation", name="Administration")).to_have_count(0)
        page.goto(f"{base}/", wait_until="networkidle")
        expect(page.get_by_role("heading", name="Your next stay starts here!", level=1)).to_be_visible()
        page.goto(f"{base}/admin/knowledge", wait_until="networkidle")
        expect(page.get_by_label("Administrator email", exact=True)).to_be_visible()
        expect(page).to_have_url(re.compile(r"/admin/login\?returnUrl="))
        expect(page.get_by_role("button", name="Add content", exact=True)).to_have_count(0)
        expect(page.get_by_text("Management token", exact=False)).to_have_count(0)
        expect(page.get_by_text("Nothing is indexed yet.", exact=True)).to_have_count(0)
        assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
        page.get_by_label("Administrator email", exact=True).fill("admin@example.test")
        page.get_by_role("button", name="Send sign-in code", exact=True).click()
        page.get_by_label("One-time code", exact=True).fill("000000")
        page.get_by_role("button", name="Sign in", exact=True).click()
        expect(page.get_by_role("alert")).to_contain_text("invalid, expired")
        page.get_by_label("One-time code", exact=True).fill("123456")
        page.get_by_role("button", name="Sign in", exact=True).click()
        expect(page).to_have_url(f"{base}/admin/knowledge")
        expect(page.get_by_text("admin@example.test", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Add content", exact=True)).to_be_enabled()
        page.get_by_role("navigation", name="Administration").get_by_role("link", name="Architecture", exact=True).click()
        expect(page.get_by_role("heading", name="Architecture", level=1)).to_be_visible()
        expect(page.get_by_role("button", name="Sign out", exact=True)).to_be_visible()
        page.get_by_role("navigation", name="Administration").get_by_role("link", name="Knowledge Center", exact=True).click()
        page.reload(wait_until="networkidle")
        expect(page.get_by_text("admin@example.test", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Refresh", exact=True)).to_be_enabled()
        state["session_valid"] = False
        page.get_by_role("button", name="Refresh", exact=True).click()
        expect(page.get_by_label("Administrator email", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Add content", exact=True)).to_have_count(0)
        state["session_valid"] = True
        page.get_by_label("Administrator email", exact=True).fill("admin@example.test")
        page.get_by_role("button", name="Send sign-in code", exact=True).click()
        page.get_by_label("One-time code", exact=True).fill("123456")
        page.get_by_role("button", name="Sign in", exact=True).click()
        expect(page.get_by_role("button", name="Sign out", exact=True)).to_be_visible()
        page.get_by_role("button", name="Sign out", exact=True).click()
        expect(page.get_by_label("Administrator email", exact=True)).to_be_visible()
        assert page.evaluate("sessionStorage.getItem('hotel.knowledge.session')") is None
        page.goto(f"{base}/admin/login?returnUrl=https%3A%2F%2Fevil.example%2F", wait_until="networkidle")
        page.get_by_label("Administrator email", exact=True).fill("admin@example.test")
        page.get_by_role("button", name="Send sign-in code", exact=True).click()
        page.get_by_label("One-time code", exact=True).fill("123456")
        page.get_by_role("button", name="Sign in", exact=True).click()
        expect(page).to_have_url(f"{base}/admin")
        page.get_by_role("button", name="Sign out", exact=True).click()
        expect(page.get_by_label("Administrator email", exact=True)).to_be_visible()
        assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
        print("shared login: protected deep links, public Home, wrong code, return route, shared session, refresh, expiry, logout and safe redirects passed")
    finally:
        page.unroute("**/api/admin/auth/**", handle)
        page.unroute("**/api/knowledge/**", handle)


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
            channel="msedge" if sys.platform == "win32" and executable is None else None,
            headless=True,
            args=["--no-sandbox"],
        )
        try:
            for name, width, height in [("desktop", 1440, 1000), ("mobile", 390, 844)]:
                page = browser.new_page(viewport={"width": width, "height": height})
                capability_reads = []
                def development_capabilities(route):
                    capability_reads.append(route.request.url)
                    route.fulfill(content_type="application/json",
                                  headers={"Access-Control-Allow-Origin": "*"},
                                  body=json.dumps({"requiresSignIn": False, "emailSignInEnabled": False}))
                page.route("**/api/admin/auth/capabilities", development_capabilities)
                page.emulate_media(reduced_motion="reduce")
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))

                page.goto(f"{base}/", wait_until="networkidle")
                expect(page.get_by_role("heading", name="Your next stay starts here!", level=1)).to_be_visible()
                expect(page).to_have_title("Hotel Booking")
                assert not capability_reads, "Public Home must not initialize administrator authentication."
                if name == "mobile":
                    page.get_by_role("button", name="Navigation menu").click()
                expect(page.locator(".sidebar nav").get_by_role("link", name="Home")).to_be_visible()
                assert page.locator(".sidebar nav").get_by_role("link").count() == 2
                expect(page.locator(".sidebar nav").get_by_role("link", name="AI Product Management")).to_be_visible()
                public_copy = page.locator("body").inner_text().lower()
                assert "find a room. book with confidence." not in public_copy
                for forbidden_copy in ("agentic", "agent operations", "architecture", "delivery", "demo", "agents", "sample hotel"):
                    assert forbidden_copy not in public_copy, (name, forbidden_copy)

                response = page.goto(f"{base}/admin/architecture", wait_until="networkidle")
                assert response is not None and response.status == 200, name
                expect(page).to_have_title("Architecture")
                expect(page.get_by_role("heading", name="Architecture", level=1)).to_be_visible()
                expect(page.get_by_role("navigation", name="Administration")).to_be_visible()
                if name == "mobile":
                    page.get_by_role("button", name="Navigation menu").click()
                expect(page.locator(".architecture-journey, .architecture-metrics")).to_have_count(0)
                delivery_path = page.get_by_role("list", name="Requirement to deployment")
                expect(delivery_path.get_by_role("listitem")).to_have_count(9)
                expected_stages = (
                    "Describe what you need", "Your request is picked up", "The AI builds your change",
                    "Automatic checks run", "The AI reviews the code", "Check the agreed outcome",
                    "You approve publishing", "Publish to the demo site", "Optional: release to production",
                )
                assert delivery_path.get_by_role("heading", level=3).all_text_contents() == list(expected_stages)
                cards = delivery_path.get_by_role("listitem")
                for index in range(cards.count()):
                    card = cards.nth(index)
                    expect(card.locator(".node-type")).to_be_visible()
                    expect(card.get_by_text("What you get", exact=True)).to_be_visible()
                    expect(card.get_by_text("Your part", exact=True)).to_be_visible()
                    if index:
                        previous = cards.nth(index - 1).bounding_box()
                        current = card.bounding_box()
                        assert abs(current["x"] - previous["x"]) <= 1, (name, index)
                        assert current["y"] > previous["y"] + previous["height"], (name, index)
                technical = page.locator(".architecture-technical")
                expect(technical).not_to_have_attribute("open", "")
                expect(page.get_by_role("heading", name="System map", exact=True)).not_to_be_visible()
                expect(page.get_by_role("heading", name="Which workflow does what?", exact=True)).to_have_count(0)
                technical.locator(":scope > summary").click()
                page.locator(".flow-supporting-tools > summary").click()
                for details in page.locator(".stage-technical").all():
                    details.locator(":scope > summary").click()
                for workflow in (
                    "Agentic intake", "Copilot setup steps", "PR validation", "Hotel browser evidence",
                    "Hotel code review", "QA evidence", "Deploy development", "Sync development lifecycle",
                    "Release proposal", "Deploy production", "Microsoft grounded evaluation",
                    "Diagnose development SQL permissions", "Deploy Knowledge MCP POC",
                ):
                    link = page.get_by_role("link", name=workflow, exact=True)
                    expect(link).to_be_visible()
                    assert "/actions/workflows/" in link.get_attribute("href")
                expect(page.get_by_role("list", name="Declared Azure services").get_by_role("listitem")).to_have_count(9)
                expect(page.get_by_role("list", name="Runtime routing safety gates").get_by_role("listitem")).to_have_count(4)
                for heading in ("System map", "A safe agent decision", "Where are the boundaries?"):
                    expect(page.get_by_role("heading", name=heading, exact=True)).to_be_visible()
                assert page.locator(".architecture-canvas").evaluate(
                    "e => getComputedStyle(e).backgroundColor") == "rgb(248, 239, 223)"
                for selector in (".architecture-canvas", ".delivery-sheet", ".runtime-map",
                                 ".architecture-technical", ".service-catalogue"):
                    assert page.locator(selector).evaluate("e => e.scrollWidth <= e.clientWidth + 1"), (name, selector)
                assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), name

                public_nav = page.locator(".sidebar nav")
                expect(public_nav.get_by_role("link", name="Home")).to_be_visible()
                expect(public_nav.get_by_role("link", name="AI Product Management")).to_be_visible()
                assert public_nav.get_by_role("link").count() == 2, "top bar must contain only Home and AI Product Management"

                connectors = page.locator(".map-packets")
                assert connectors.count() > 0
                for index in range(connectors.count()):
                    animation = connectors.nth(index).evaluate(
                        "element => getComputedStyle(element).animationName"
                    )
                    assert animation == "none", (name, animation)

                page.get_by_role("link", name="AI Product Management").last.focus()
                page.keyboard.press("Tab")
                focus_outline = page.get_by_role("link", name="Knowledge Center", exact=True).evaluate(
                    "element => getComputedStyle(element).outlineStyle"
                )
                assert focus_outline != "none", (name, focus_outline)

                overview = page.goto(f"{base}/admin", wait_until="networkidle")
                assert overview is not None and overview.status == 200, name
                expect(page.get_by_role("heading", name="AI Product Management", level=1)).to_be_visible()
                expect(page.get_by_role("link", name="Manage sources in Knowledge Center")).to_have_count(0)
                expect(page.get_by_role("navigation", name="Administration").get_by_role("link", name="Knowledge Center", exact=True)).to_be_visible()
                expect(page.get_by_role("region", name="Knowledge center (RAG)")).to_have_count(0)
                check_knowledge_management(page, base)
                check_knowledge_sign_in(page, base)
                expect(page.get_by_text("access gap", exact=False)).to_have_count(0)

                no_horizontal_scroll = page.evaluate(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                )
                assert no_horizontal_scroll, name
                assert not errors, errors
                print(f"{name}: routes, accessibility, responsive layout, and reduced motion passed")
                page.close()

            redirect_page = browser.new_page(viewport={"width": 1280, "height": 900})
            redirect_page.route("**/api/admin/auth/capabilities", development_capabilities)
            redirect_page.goto(f"{base}/architecture", wait_until="networkidle")
            expect(redirect_page).to_have_url(f"{base}/admin/architecture")
            print("redirect: /architecture resolves to /admin/architecture")

            not_found_page = browser.new_page(viewport={"width": 1280, "height": 900})
            not_found_page.goto(f"{base}/an-unknown-route", wait_until="networkidle")
            expect(not_found_page.get_by_role("heading", name="Not Found")).to_be_visible()
            print("routing: unknown paths continue to show Not Found")

            motion_page = browser.new_page(viewport={"width": 1440, "height": 1000})
            motion_page.route("**/api/admin/auth/capabilities", development_capabilities)
            motion_page.emulate_media(reduced_motion="no-preference")
            motion_page.goto(f"{base}/admin/architecture", wait_until="networkidle")
            motion_page.locator(".architecture-technical > summary").click()
            for selector, expected in ((".map-packets", "architecture-flow"),):
                animation = motion_page.locator(selector).first.evaluate(
                    "element => getComputedStyle(element).animationName")
                assert animation == expected or animation.startswith(f"{expected}-b-"), animation
            initial_offset = motion_page.locator(".map-packets").first.evaluate(
                "e => getComputedStyle(e).strokeDashoffset")
            motion_page.wait_for_function(
                "initial => getComputedStyle(document.querySelector('.map-packets')).strokeDashoffset !== initial",
                arg=initial_offset)
            for width in (1024, 768):
                motion_page.set_viewport_size({"width": width, "height": 900})
                assert motion_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
                for selector in (".architecture-canvas", ".delivery-sheet", ".runtime-map",
                                 ".architecture-technical", ".service-catalogue"):
                    assert motion_page.locator(selector).evaluate(
                        "e => e.scrollWidth <= e.clientWidth + 1"), (width, selector)
            print("motion/layout: technical-map animation enabled; tablet widths fit without horizontal scrolling")
        finally:
            browser.close()


if __name__ == "__main__":
    main()
