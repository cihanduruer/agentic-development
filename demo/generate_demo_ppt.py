from pathlib import Path

from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN
from pptx.util import Inches, Pt


OUTPUT = Path(__file__).with_name("Agentic-Development-Team-Demo.pptx")

NAVY = RGBColor(18, 32, 56)
BLUE = RGBColor(0, 120, 212)
TEAL = RGBColor(0, 153, 153)
GREEN = RGBColor(16, 124, 65)
AMBER = RGBColor(202, 80, 16)
RED = RGBColor(196, 43, 28)
LIGHT = RGBColor(243, 247, 251)
MID = RGBColor(90, 103, 119)
WHITE = RGBColor(255, 255, 255)


def add_title(slide, title, subtitle=None):
    title_box = slide.shapes.add_textbox(Inches(0.65), Inches(0.35), Inches(12), Inches(0.7))
    paragraph = title_box.text_frame.paragraphs[0]
    paragraph.text = title
    paragraph.font.name = "Aptos Display"
    paragraph.font.size = Pt(30)
    paragraph.font.bold = True
    paragraph.font.color.rgb = NAVY
    if subtitle:
        subtitle_box = slide.shapes.add_textbox(
            Inches(0.68), Inches(1.02), Inches(11.8), Inches(0.45)
        )
        paragraph = subtitle_box.text_frame.paragraphs[0]
        paragraph.text = subtitle
        paragraph.font.name = "Aptos"
        paragraph.font.size = Pt(14)
        paragraph.font.color.rgb = MID


def add_footer(slide, number):
    line = slide.shapes.add_shape(
        MSO_SHAPE.RECTANGLE, Inches(0), Inches(7.28), Inches(13.333), Inches(0.22)
    )
    line.fill.solid()
    line.fill.fore_color.rgb = BLUE
    line.line.fill.background()
    page = slide.shapes.add_textbox(Inches(12.6), Inches(7.02), Inches(0.4), Inches(0.25))
    paragraph = page.text_frame.paragraphs[0]
    paragraph.text = str(number)
    paragraph.alignment = PP_ALIGN.RIGHT
    paragraph.font.size = Pt(10)
    paragraph.font.color.rgb = MID


def add_bullets(slide, items, left=0.8, top=1.65, width=11.8, height=4.9):
    box = slide.shapes.add_textbox(
        Inches(left), Inches(top), Inches(width), Inches(height)
    )
    frame = box.text_frame
    frame.word_wrap = True
    frame.clear()
    for index, item in enumerate(items):
        paragraph = frame.paragraphs[0] if index == 0 else frame.add_paragraph()
        paragraph.text = item
        paragraph.level = 0
        paragraph.font.name = "Aptos"
        paragraph.font.size = Pt(23)
        paragraph.font.color.rgb = NAVY
        paragraph.space_after = Pt(14)
        paragraph.text = f"•  {item}"


def add_card(slide, x, y, width, height, title, body, color=BLUE):
    card = slide.shapes.add_shape(
        MSO_SHAPE.ROUNDED_RECTANGLE,
        Inches(x),
        Inches(y),
        Inches(width),
        Inches(height),
    )
    card.fill.solid()
    card.fill.fore_color.rgb = LIGHT
    card.line.color.rgb = color
    card.line.width = Pt(2)

    title_box = slide.shapes.add_textbox(
        Inches(x + 0.25), Inches(y + 0.2), Inches(width - 0.5), Inches(0.45)
    )
    paragraph = title_box.text_frame.paragraphs[0]
    paragraph.text = title
    paragraph.font.name = "Aptos Display"
    paragraph.font.size = Pt(18)
    paragraph.font.bold = True
    paragraph.font.color.rgb = color

    body_box = slide.shapes.add_textbox(
        Inches(x + 0.25), Inches(y + 0.72), Inches(width - 0.5), Inches(height - 0.9)
    )
    paragraph = body_box.text_frame.paragraphs[0]
    paragraph.text = body
    paragraph.font.name = "Aptos"
    paragraph.font.size = Pt(15)
    paragraph.font.color.rgb = NAVY
    paragraph.space_after = Pt(4)


def add_flow(slide, labels, y=3.0):
    count = len(labels)
    gap = 0.14
    width = (12.0 - gap * (count - 1)) / count
    colors = [BLUE, TEAL, GREEN, AMBER, BLUE, TEAL, GREEN, NAVY]
    for index, label in enumerate(labels):
        x = 0.65 + index * (width + gap)
        shape = slide.shapes.add_shape(
            MSO_SHAPE.ROUNDED_RECTANGLE,
            Inches(x),
            Inches(y),
            Inches(width),
            Inches(1.05),
        )
        shape.fill.solid()
        shape.fill.fore_color.rgb = colors[index % len(colors)]
        shape.line.fill.background()
        paragraph = shape.text_frame.paragraphs[0]
        paragraph.text = label
        paragraph.alignment = PP_ALIGN.CENTER
        paragraph.font.name = "Aptos"
        paragraph.font.size = Pt(14)
        paragraph.font.bold = True
        paragraph.font.color.rgb = WHITE
        if index < count - 1:
            arrow = slide.shapes.add_textbox(
                Inches(x + width - 0.02), Inches(y + 0.32), Inches(gap + 0.05), Inches(0.35)
            )
            paragraph = arrow.text_frame.paragraphs[0]
            paragraph.text = ">"
            paragraph.alignment = PP_ALIGN.CENTER
            paragraph.font.bold = True
            paragraph.font.color.rgb = MID


def new_slide(prs, title, subtitle=None):
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    slide.background.fill.solid()
    slide.background.fill.fore_color.rgb = WHITE
    add_title(slide, title, subtitle)
    add_footer(slide, len(prs.slides))
    return slide


def build_deck():
    prs = Presentation()
    prs.slide_width = Inches(13.333)
    prs.slide_height = Inches(7.5)

    slide = prs.slides.add_slide(prs.slide_layouts[6])
    slide.background.fill.solid()
    slide.background.fill.fore_color.rgb = NAVY
    accent = slide.shapes.add_shape(
        MSO_SHAPE.RECTANGLE, Inches(0), Inches(0), Inches(0.24), Inches(7.5)
    )
    accent.fill.solid()
    accent.fill.fore_color.rgb = BLUE
    accent.line.fill.background()
    title = slide.shapes.add_textbox(Inches(0.9), Inches(2.0), Inches(11.4), Inches(1.4))
    paragraph = title.text_frame.paragraphs[0]
    paragraph.text = "Agentic Hotel Development"
    paragraph.font.name = "Aptos Display"
    paragraph.font.size = Pt(38)
    paragraph.font.bold = True
    paragraph.font.color.rgb = WHITE
    subtitle = slide.shapes.add_textbox(Inches(0.94), Inches(3.35), Inches(10.8), Inches(1.2))
    paragraph = subtitle.text_frame.paragraphs[0]
    paragraph.text = "Team demo: requirement to verified development deployment"
    paragraph.font.name = "Aptos"
    paragraph.font.size = Pt(23)
    paragraph.font.color.rgb = RGBColor(190, 219, 245)
    footer = slide.shapes.add_textbox(Inches(0.94), Inches(6.5), Inches(8), Inches(0.4))
    paragraph = footer.text_frame.paragraphs[0]
    paragraph.text = "Hotel Web + API are the product. Agents are the delivery system."
    paragraph.font.name = "Aptos"
    paragraph.font.size = Pt(16)
    paragraph.font.color.rgb = WHITE

    slide = new_slide(
        prs,
        "What we are demonstrating",
        "A controlled, traceable software-delivery cycle",
    )
    add_card(
        slide,
        0.75,
        1.65,
        3.8,
        3.9,
        "Product",
        "Hotel Web UI\n.NET booking API\nAzure SQL persistence\nUser-visible booking behavior",
        BLUE,
    )
    add_card(
        slide,
        4.78,
        1.65,
        3.8,
        3.9,
        "Delivery system",
        "Azure Boards intake\nCopilot implementation\nGitHub Actions gates\nIndependent review and QA",
        TEAL,
    )
    add_card(
        slide,
        8.82,
        1.65,
        3.8,
        3.9,
        "Human control",
        "Requirement acceptance\nReview decisions\nRelease approval\nProduction authorization",
        GREEN,
    )

    slide = new_slide(prs, "1. Start with product intent", "Use the latest main branch")
    add_bullets(
        slide,
        [
            "Open the repository in GitHub Copilot Desktop.",
            "Ask Copilot to read AGENTS.md and docs/knowledge/.",
            "Refine one Hotel Web/API vertical slice.",
            "Confirm user outcome, failure behavior, and testable acceptance criteria.",
        ],
    )
    add_card(
        slide,
        1.0,
        5.55,
        11.3,
        1.05,
        "Starter prompt",
        "Help me refine a Hotel Web/API requirement into one bounded vertical slice with testable acceptance criteria.",
        BLUE,
    )

    slide = new_slide(prs, "2. Create the Azure Boards item")
    add_bullets(
        slide,
        [
            "Create a User Story for new behavior or a Bug for a correction.",
            "Target the sample-project Azure DevOps project.",
            "Leave the item in New.",
            "Do not add github-synced and do not manually create a GitHub issue.",
        ],
    )
    add_card(
        slide,
        1.0,
        5.55,
        11.3,
        1.05,
        "Creation prompt",
        "Create this in Azure Boards, leave it in New, and do not add github-synced.",
        TEAL,
    )

    slide = new_slide(prs, "3. Watch automatic intake", "Polling runs every five minutes")
    add_flow(slide, ["New AB item", "Intake", "GitHub issue", "Agent assigned", "AB Active"])
    add_bullets(
        slide,
        [
            "The linked GitHub issue contains the acceptance criteria and AB# reference.",
            "github-synced prevents duplicate dispatch.",
            "The Hotel developer agent receives the bounded implementation contract.",
        ],
        top=4.55,
        height=1.7,
    )

    slide = new_slide(prs, "4. Follow implementation and evidence")
    add_flow(
        slide,
        ["Knowledge", "Branch", "Code + tests", "Pull request", "CI", "Review", "QA"],
        y=2.15,
    )
    add_bullets(
        slide,
        [
            "Every claim must be grounded in repository knowledge or explicit evidence.",
            "Source or PR metadata changes require fresh validation, review, and QA.",
            "The QA agent verifies acceptance criteria and negative paths independently.",
        ],
        top=4.1,
        height=2.1,
    )

    slide = new_slide(prs, "5. Approve and deploy")
    add_bullets(
        slide,
        [
            "Review the exact pull-request revision and resolve all findings.",
            "Confirm CI and immutable QA evidence bind to the same source.",
            "A human approves merge and release; bot recommendations are not approval.",
            "Development deploys automatically after merge.",
            "Production remains manual and protected.",
        ],
    )

    slide = new_slide(prs, "Possible demo results", "Show the outcome honestly")
    add_card(
        slide,
        0.65,
        1.7,
        3.9,
        4.5,
        "Success",
        "Work item becomes Active.\nIssue and PR are linked.\nCI, review, and QA pass.\nDevelopment deploys.\nWeb/API behavior is verified.",
        GREEN,
    )
    add_card(
        slide,
        4.72,
        1.7,
        3.9,
        4.5,
        "Needs correction",
        "A gate finds a defect.\nAgent updates code or metadata.\nAll affected evidence is regenerated.\nNo stale result is reused.",
        AMBER,
    )
    add_card(
        slide,
        8.79,
        1.7,
        3.9,
        4.5,
        "Safe stop",
        "Missing evidence or risky action routes to human review.\nDeployment failure preserves the previous live version.\nProduction is not touched.",
        RED,
    )

    slide = new_slide(prs, "End-to-end evidence chain")
    add_flow(
        slide,
        [
            "Requirement",
            "AB item",
            "Issue",
            "PR",
            "CI",
            "Review",
            "QA",
            "Deploy",
        ],
        y=2.2,
    )
    add_card(
        slide,
        1.0,
        4.35,
        11.3,
        1.4,
        "Key takeaway",
        "The system accelerates implementation while retaining evidence, traceability, least privilege, and explicit human control over release.",
        BLUE,
    )

    prs.save(OUTPUT)
    return OUTPUT


if __name__ == "__main__":
    print(build_deck())
