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

        public virtual ICollection<ProjectionProgramStepTemplateParameter> Parameters { get; set; } = new List<ProjectionProgramStepTemplateParameter>();
    }

    public class ProjectionProgramStepTemplateParameter
    {
        public Guid Id { get; set; }

        public required Guid TemplateId { get; set; }
        public ProjectionProgramStepTemplate? Template { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public required string DataType { get; set; }

        public bool IsActive { get; set; }
    }
}
