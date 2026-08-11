using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.ReleaseNotes;

[TestClass]
public class ReleaseNotesServiceTests
{
    private ReleaseNotesService _releaseNotesService = null!;

    [TestInitialize]
    public void Setup()
    {
        _releaseNotesService = new ReleaseNotesService();
    }

    [TestMethod]
    public async Task GetReleaseNotesAsync_WhenCalledThenReturnsEmbeddedReleaseNotes()
    {
        Result<IEnumerable<ReleaseNote>> result = await _releaseNotesService.GetReleaseNotesAsync();

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Data);
        Assert.IsTrue(result.Data.Any());
    }

    [TestMethod]
    public async Task GetReleaseNotesAsync_WhenCalledThenReturnsNewestReleaseFirst()
    {
        Result<IEnumerable<ReleaseNote>> result = await _releaseNotesService.GetReleaseNotesAsync();

        List<ReleaseNote> releaseNotes = result.Data!.ToList();

        List<ReleaseNote> expectedOrder = releaseNotes
            .OrderByDescending(x => x.ReleaseDate)
            .ToList();

        CollectionAssert.AreEqual(expectedOrder, releaseNotes);
    }

    [TestMethod]
    public async Task GetReleaseNotesAsync_WhenCalledThenEachReleaseHasVersionAndSummary()
    {
        Result<IEnumerable<ReleaseNote>> result = await _releaseNotesService.GetReleaseNotesAsync();

        foreach (ReleaseNote releaseNote in result.Data!)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(releaseNote.Version));
            Assert.IsFalse(string.IsNullOrWhiteSpace(releaseNote.Summary));
        }
    }
}
