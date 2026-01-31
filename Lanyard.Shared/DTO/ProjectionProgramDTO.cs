using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Shared.DTO;

public class ProjectionProgramDTO
{
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;

    public List<ProjectionProgramStepDTO> ProjectionProgramSteps { get; set; } = [];
}

public class ProjectionProgramStepDTO
{
    public int SortOrder { get; set; }

    public List<ProjectionProgramParameterValueDTO> ParameterValues { get; set; } = [];
}

public class ProjectionProgramParameterValueDTO
{
    public required ProjectionProgramStepTemplateParameterDTO Parameter { get; set; }

    public string? Value { get; set; }
}

public class ProjectionProgramStepTemplateParameterDTO
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    public bool IsRequired { get; set; }

    public required string DataType { get; set; }
}