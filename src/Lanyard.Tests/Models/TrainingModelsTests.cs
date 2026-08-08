using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Models;

[TestClass]
public class TrainingModelsTests
{
    [TestMethod]
    public async Task Course_SavesAndReloadsSectionsAndQuestionsWithOptions()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Guid courseId = Guid.NewGuid();
        Guid questionId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            Course course = new()
            {
                Id = courseId,
                Name = "Play2Day Induction",
                PassMarkPercent = 80,
                IsActive = true,
                Sections =
                [
                    new CourseSection { Id = Guid.NewGuid(), CourseId = courseId, Title = "Shoes", BodyHtml = "<p>Closed toe only.</p>", SortOrder = 0, IsActive = true }
                ],
                Questions =
                [
                    new CourseQuestion
                    {
                        Id = questionId,
                        CourseId = courseId,
                        QuestionText = "If I am ill I should...",
                        SortOrder = 0,
                        IsActive = true,
                        Options =
                        [
                            new CourseQuestionOption { Id = Guid.NewGuid(), QuestionId = questionId, OptionText = "Ring Play2Day and tell a manager.", IsCorrect = true, SortOrder = 0, IsActive = true },
                            new CourseQuestionOption { Id = Guid.NewGuid(), QuestionId = questionId, OptionText = "Say nothing.", IsCorrect = false, SortOrder = 1, IsActive = true }
                        ]
                    }
                ]
            };

            ctx.Courses.Add(course);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            Course reloaded = await ctx.Courses
                .Include(x => x.Sections)
                .Include(x => x.Questions).ThenInclude(x => x.Options)
                .SingleAsync(x => x.Id == courseId);

            Assert.HasCount(1, reloaded.Sections);
            Assert.AreEqual("Shoes", reloaded.Sections[0].Title);
            Assert.HasCount(1, reloaded.Questions);
            Assert.HasCount(2, reloaded.Questions[0].Options);
            Assert.AreEqual("Ring Play2Day and tell a manager.", reloaded.Questions[0].Options.Single(x => x.IsCorrect).OptionText);
        }
    }
}
