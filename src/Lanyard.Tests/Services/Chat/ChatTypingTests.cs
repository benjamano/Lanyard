using Lanyard.Application.Services.Chat;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Chat;

// The "Tom is typing" line: who's typing is kept per conversation, lapses on its own when the
// pings stop, and only a change in who's typing is announced.
[TestClass]
public class ChatTypingTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Typing_ShowsOnlyInThatConversation()
    {
        ChatTyping typing = new(new TestClock(Now));
        Guid here = Guid.NewGuid();
        Guid elsewhere = Guid.NewGuid();

        typing.Typing("tom", here);

        CollectionAssert.AreEquivalent(new[] { "tom" }, typing.WhoIsTyping(here).ToList());
        Assert.AreEqual(0, typing.WhoIsTyping(elsewhere).Count);
    }

    [TestMethod]
    public void Stopped_RemovesThePerson()
    {
        ChatTyping typing = new(new TestClock(Now));
        Guid conversation = Guid.NewGuid();

        typing.Typing("tom", conversation);
        typing.Typing("amy", conversation);
        typing.Stopped("tom", conversation);

        CollectionAssert.AreEquivalent(new[] { "amy" }, typing.WhoIsTyping(conversation).ToList());
    }

    [TestMethod]
    public void Typing_LapsesWhenThePingsStop()
    {
        TestClock clock = new(Now);
        ChatTyping typing = new(clock);
        Guid conversation = Guid.NewGuid();

        typing.Typing("tom", conversation);
        clock.Advance(ChatTyping.Lifetime + TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, typing.WhoIsTyping(conversation).Count);
    }

    [TestMethod]
    public void Typing_EachPingExtendsIt()
    {
        TestClock clock = new(Now);
        ChatTyping typing = new(clock);
        Guid conversation = Guid.NewGuid();

        typing.Typing("tom", conversation);
        clock.Advance(ChatTyping.Lifetime - TimeSpan.FromSeconds(1));
        typing.Typing("tom", conversation);
        clock.Advance(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEquivalent(new[] { "tom" }, typing.WhoIsTyping(conversation).ToList());
    }

    [TestMethod]
    public void OnChanged_OnlyWhenWhoIsTypingChanges()
    {
        ChatTyping typing = new(new TestClock(Now));
        Guid conversation = Guid.NewGuid();
        List<Guid> changes = [];
        typing.OnChanged += changes.Add;

        typing.Typing("tom", conversation);
        typing.Typing("tom", conversation);
        typing.Typing("tom", conversation);
        typing.Stopped("tom", conversation);
        typing.Stopped("tom", conversation);

        CollectionAssert.AreEqual(new[] { conversation, conversation }, changes);
    }
}
