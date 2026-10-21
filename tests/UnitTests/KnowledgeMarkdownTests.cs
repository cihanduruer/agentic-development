using AgenticHotelBooking.Infrastructure;

namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeMarkdownTests
{
    [Fact]
    public void RendersHeadingsListsTablesAndCodeAsDocumentMarkup()
    {
        var html = KnowledgeMarkdownRenderer.Render("""
            # Product

            - **Available** rooms
            - `EUR` prices

            | Room | Rate |
            | --- | --- |
            | King | 189 |

            ```text
            Booking rules
            ```
            """);

        Assert.Contains("<h1>Product</h1>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<strong>Available</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<pre><code", html);
    }

    [Fact]
    public void DoesNotExecuteHtmlOrAllowActiveUrlsAndRemoteImages()
    {
        var html = KnowledgeMarkdownRenderer.Render("""
            <script>alert('unsafe')</script>
            <iframe src="https://untrusted.example"></iframe>
            <img src=x onerror=alert(1)>

            [Unsafe](javascript:alert%281%29)
            ![Tracking](https://untrusted.example/tracker.png)
            [Safe](https://learn.microsoft.com/)
            """);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://learn.microsoft.com/\"", html);
    }
}
