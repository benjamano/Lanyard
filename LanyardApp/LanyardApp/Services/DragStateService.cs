using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Services
{
    public sealed class DragStateService
    {
        public ProjectionProgramStepTemplate? CurrentTemplate { get; private set; }
        public object? Source { get; private set; }

        public void StartDrag(ProjectionProgramStepTemplate template, object? source = null)
        {
            ArgumentNullException.ThrowIfNull(template);
            CurrentTemplate = template;
            Source = source;
        }

        public bool HasDrag => CurrentTemplate is not null;

        public void Clear()
        {
            CurrentTemplate = null;
            Source = null;
        }
    }
}
