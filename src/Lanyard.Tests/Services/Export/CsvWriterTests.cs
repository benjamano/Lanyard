using System.Text;
using Lanyard.Application.Services.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Export;

[TestClass]
public class CsvWriterTests
{
    [TestMethod]
    public void Text_PlainValue_IsUnchanged()
    {
        Assert.AreEqual("Amy Clark", CsvWriter.Text("Amy Clark"));
    }

    [TestMethod]
    public void Text_Null_IsEmpty()
    {
        Assert.AreEqual(string.Empty, CsvWriter.Text(null));
        Assert.AreEqual(string.Empty, CsvWriter.Text(string.Empty));
    }

    [TestMethod]
    [DataRow("Clark, Amy", "\"Clark, Amy\"")]
    [DataRow("Amy \"Ace\" Clark", "\"Amy \"\"Ace\"\" Clark\"")]
    [DataRow("Line one\nLine two", "\"Line one\nLine two\"")]
    public void Text_SpecialCharacters_AreQuoted(string value, string expected)
    {
        Assert.AreEqual(expected, CsvWriter.Text(value));
    }

    [TestMethod]
    [DataRow("=1+1")]
    [DataRow("+44 7700 900000")]
    [DataRow("-5")]
    [DataRow("@SUM(A1)")]
    [DataRow("\tTabbed")]
    public void Text_FormulaTrigger_IsNeutralised(string value)
    {
        Assert.AreEqual($"'{value}", CsvWriter.Text(value));
    }

    [TestMethod]
    public void Text_CarriageReturnTrigger_IsNeutralisedAndQuoted()
    {
        Assert.AreEqual("\"'\rcmd\"", CsvWriter.Text("\rcmd"));
    }

    [TestMethod]
    public void Text_FormulaThatAlsoNeedsQuoting_IsNeutralisedInsideTheQuotes()
    {
        Assert.AreEqual("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"", CsvWriter.Text("=HYPERLINK(\"http://x\",\"y\")"));
    }

    [TestMethod]
    public void Text_TriggerLaterInTheValue_IsLeftAlone()
    {
        Assert.AreEqual("Mary-Jane", CsvWriter.Text("Mary-Jane"));
    }

    [TestMethod]
    public void Number_UsesInvariantCulture()
    {
        Assert.AreEqual("7.50", CsvWriter.Number(7.5m, "0.00"));
        Assert.AreEqual("480", CsvWriter.Number(480));
        Assert.AreEqual(string.Empty, CsvWriter.Number((int?)null));
    }

    [TestMethod]
    public void ToBytes_StartsWithUtf8ByteOrderMark_AndUsesCrLf()
    {
        StringBuilder builder = new();
        CsvWriter.AppendRow(builder, ["a", "b"]);

        byte[] bytes = CsvWriter.ToBytes(builder);

        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.AreEqual("a,b\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }
}
