using Lanyard.Shared.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Diagnostics;

[TestClass]
public class LogRedactorTests
{
    [TestMethod]
    public void Redact_RemovesSharedSecretQueryParameter()
    {
        string redacted = LogRedactor.Redact("Connecting to https://lanyard.example/websocket?clientId=abc&secret=hunter22&dmxBatch=1");

        Assert.AreEqual("Connecting to https://lanyard.example/websocket?clientId=abc&secret=[REDACTED]&dmxBatch=1", redacted);
    }

    [TestMethod]
    [DataRow("Opening https://host/kiosk/video?token=eyJhbGciOi.payload.sig", "Opening https://host/kiosk/video?token=[REDACTED]")]
    [DataRow("Navigating to https://host/staff/kiosk?display=1&token=abc123", "Navigating to https://host/staff/kiosk?display=1&token=[REDACTED]")]
    [DataRow("password=letmein and api_key=XYZ", "password=[REDACTED] and api_key=[REDACTED]")]
    [DataRow("SECRET=Shouty", "SECRET=[REDACTED]")]
    public void Redact_RemovesSensitiveKeyValuePairs(string input, string expected)
    {
        Assert.AreEqual(expected, LogRedactor.Redact(input));
    }

    [TestMethod]
    public void Redact_RemovesBearerTokens()
    {
        string redacted = LogRedactor.Redact("Authorization: Bearer abc.DEF-123_xyz==");

        Assert.AreEqual("Authorization: Bearer [REDACTED]", redacted);
    }

    [TestMethod]
    public void Redact_RemovesKnownSecretsWhereverTheyAppear()
    {
        // The secret may show up outside a key=value pair, e.g. in an exception message.
        string redacted = LogRedactor.Redact("Handshake failed for s3cr3t/value+1 (s3cr3t%2Fvalue%2B1)", ["s3cr3t/value+1"]);

        Assert.AreEqual("Handshake failed for [REDACTED] ([REDACTED])", redacted);
    }

    [TestMethod]
    public void Redact_IgnoresVeryShortKnownSecrets()
    {
        string redacted = LogRedactor.Redact("Playing track a on deck b", ["a", null, ""]);

        Assert.AreEqual("Playing track a on deck b", redacted);
    }

    [TestMethod]
    public void Redact_LeavesOrdinaryTextAlone()
    {
        const string text = "Monkey=3 and keyboard=uk-layout, playlist 'Secret Garden' started";

        Assert.AreEqual(text, LogRedactor.Redact(text));
    }

    [TestMethod]
    public void Redact_IsIdempotent()
    {
        // The server redacts again what the kiosk already redacted.
        string once = LogRedactor.Redact("url?secret=abc&token=def", ["abcdef"]);
        string twice = LogRedactor.Redact(once, ["abcdef"]);

        Assert.AreEqual(once, twice);
    }

    [TestMethod]
    public void Redact_ReturnsEmptyForNull()
    {
        Assert.AreEqual(string.Empty, LogRedactor.Redact(null));
    }

    [TestMethod]
    public void Redact_RemovesAKioskCredential_KeepingItsId()
    {
        string redacted = LogRedactor.Redact("Saved credential lk1.0123456789abcdef0123456789abcdef.Zx9_-abcDEF123 for later");

        Assert.AreEqual("Saved credential lk1.0123456789abcdef0123456789abcdef.[REDACTED] for later", redacted);
    }

    [TestMethod]
    public void Redact_RemovesMediaTokens()
    {
        string redacted = LogRedactor.Redact("Streaming https://host/api/music/audio/7c9e6679-7425-40de-944b-e07fc1f90ae7?mt=CfDJ8Abc-def_123");

        Assert.AreEqual("Streaming https://host/api/music/audio/7c9e6679-7425-40de-944b-e07fc1f90ae7?mt=[REDACTED]", redacted);
    }
}
