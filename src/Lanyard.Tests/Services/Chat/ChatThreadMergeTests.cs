using Lanyard.Application.Services.Chat;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Chat;

// Adding new messages to an open conversation: each once, the newest copy kept, in time order,
// and messages sent in the same instant in the order the database gives them.
[TestClass]
public class ChatThreadMergeTests
{
    private static readonly DateTime At = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

    private static ChatMessageView View(Guid id, DateTime createUtc, string text = "Hi") => new(
        new ChatMessage { Id = id, AuthorUserId = "amy", BodyHtml = $"<p>{text}</p>", BodyText = text, CreateUtc = createUtc },
        "Amy", false, null);

    [TestMethod]
    public void Merge_AddsNewOnes_InTimeOrder()
    {
        ChatMessageView first = View(Guid.NewGuid(), At);
        ChatMessageView third = View(Guid.NewGuid(), At.AddMinutes(2));
        ChatMessageView second = View(Guid.NewGuid(), At.AddMinutes(1));

        List<ChatMessageView> merged = ChatThreadMerge.Merge([first, third], [second]);

        CollectionAssert.AreEqual(new[] { first, second, third }, merged);
    }

    [TestMethod]
    public void Merge_KeepsEachMessageOnce_UsingTheFetchedCopy()
    {
        Guid id = Guid.NewGuid();
        ChatMessageView shown = View(id, At, "Before edit");
        ChatMessageView fetched = View(id, At, "After edit");

        List<ChatMessageView> merged = ChatThreadMerge.Merge([shown], [fetched, fetched]);

        Assert.AreEqual("After edit", merged.Single().Message.BodyText);
        Assert.AreEqual(0, ChatThreadMerge.Added([shown], [fetched]).Count, "Not new, so not announced");
    }

    [TestMethod]
    public void Merge_SameInstant_OrdersByIdAsPostgresDoes()
    {
        Guid a = Guid.Parse("00000001-0000-0000-0000-0000000000ff");
        Guid b = Guid.Parse("ff000000-0000-0000-0000-000000000001");

        List<ChatMessageView> merged = ChatThreadMerge.Merge([View(b, At)], [View(a, At)]);

        CollectionAssert.AreEqual(new[] { a, b }, merged.Select(x => x.Message.Id).ToList());
    }

    [TestMethod]
    public void GuidOrder_MatchesTextOrder_WhichIsHowPostgresOrdersUuids()
    {
        List<Guid> ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();

        List<Guid> byGuid = ids.OrderBy(x => x).ToList();
        List<Guid> byText = ids.OrderBy(x => x.ToString("N"), StringComparer.Ordinal).ToList();

        CollectionAssert.AreEqual(byText, byGuid);
    }

    [TestMethod]
    public void Added_IsOnlyWhatWasntShown_OldestFirst()
    {
        ChatMessageView shown = View(Guid.NewGuid(), At);
        ChatMessageView later = View(Guid.NewGuid(), At.AddMinutes(2));
        ChatMessageView sooner = View(Guid.NewGuid(), At.AddMinutes(1));

        CollectionAssert.AreEqual(new[] { sooner, later }, ChatThreadMerge.Added([shown], [shown, later, sooner]));
    }
}
