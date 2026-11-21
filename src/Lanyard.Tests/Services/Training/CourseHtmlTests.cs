using Lanyard.Application.Services.Training;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Training;

// Security Issue 9.5: a course section is written by a Manager and rendered as raw markup to every
// Admin and staff member who takes the course.
[TestClass]
public class CourseHtmlTests
{
    [TestMethod]
    [DataRow("<script>alert(1)</script><p>ok</p>", "<script")]
    [DataRow("<img src=\"/api/files/download/1\" onerror=\"alert(1)\">", "onerror")]
    [DataRow("<a href=\"javascript:alert(1)\">click</a>", "javascript:")]
    [DataRow("<p onclick=\"alert(1)\">hi</p>", "onclick")]
    [DataRow("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [DataRow("<p style=\"background:url(javascript:alert(1))\">x</p>", "style")]
    [DataRow("<img src=\"data:image/svg+xml;base64,PHN2Zz4=\">", "data:")]
    [DataRow("<svg><script>alert(1)</script></svg>", "<svg")]
    public void Clean_StripsAnythingThatCanRunScript(string html, string mustNotContain)
    {
        string cleaned = CourseHtml.Clean(html);

        Assert.IsFalse(cleaned.Contains(mustNotContain, StringComparison.OrdinalIgnoreCase), cleaned);
    }

    [TestMethod]
    public void Clean_KeepsWhatTheCourseEditorProduces()
    {
        string html = "<h2>Before your shift</h2><p><strong>Always</strong> wear <em>closed</em> shoes.</p>"
            + "<ol><li data-list=\"bullet\">Check the arena</li></ol>"
            + "<p><img src=\"/api/files/download/3f2c\" alt=\"Arena\" width=\"320\"></p>";

        string cleaned = CourseHtml.Clean(html);

        StringAssert.Contains(cleaned, "<h2>Before your shift</h2>");
        StringAssert.Contains(cleaned, "<strong>Always</strong>");
        StringAssert.Contains(cleaned, "<em>closed</em>");
        StringAssert.Contains(cleaned, "data-list=\"bullet\"");
        StringAssert.Contains(cleaned, "src=\"/api/files/download/3f2c\"");
        StringAssert.Contains(cleaned, "width=\"320\"");
    }

    [TestMethod]
    public void Clean_OpensLinksInANewTabWithoutAnOpener()
    {
        string cleaned = CourseHtml.Clean("<a href=\"https://example.com\">guide</a>");

        StringAssert.Contains(cleaned, "target=\"_blank\"");
        StringAssert.Contains(cleaned, "noopener");
    }
}
