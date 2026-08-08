namespace Lanyard.Infrastructure.DTO.Training
{
    public record QuizQuestionResult(Guid QuestionId, bool WasCorrect);

    public record QuizGradeResult(int ScorePercent, bool Passed, int AttemptNumber, List<QuizQuestionResult> QuestionResults);
}
