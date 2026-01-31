using Microsoft.AspNetCore.Identity;
using System.Data;

namespace Lanyard.Infrastructure.Models
{
    public class ProjectionProgramStepTemplate
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public bool IsActive { get; set; }

        public virtual ICollection<ProjectionProgramStepTemplateParameter> Parameters { get; set; } = [];
    }

    public class ProjectionProgramStepTemplateParameter
    {
        public Guid Id { get; set; }

        public required Guid TemplateId { get; set; }
        public ProjectionProgramStepTemplate? Template { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public bool IsRequired { get; set; }

        public required string DataType { get; set; }

        public bool IsActive { get; set; }
    }

    public class ProjectionProgram
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public string Description { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public virtual List<ProjectionProgramStep> ProjectionProgramSteps { get; set; } = [];
    }

    public class ProjectionProgramStep
    {
        public Guid Id { get; set; }

        public Guid ProjectionProgramId { get; set; }
        public ProjectionProgram? ProjectionProgram { get; set; }

        public Guid TemplateId { get; set; }
        public ProjectionProgramStepTemplate? Template { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; }

        public virtual List<ProjectionProgramParameterValue> ParameterValues { get; set; } = [];
    }

    public class ProjectionProgramParameterValue
    {
        public Guid Id { get; set; }

        public Guid ProjectionProgramStepId { get; set; }
        public ProjectionProgramStep? ProjectionProgramStep { get; set; }

        public Guid ParameterId { get; set; }
        public ProjectionProgramStepTemplateParameter? Parameter { get; set; }

        public string? Value { get; set; }
    }
}
