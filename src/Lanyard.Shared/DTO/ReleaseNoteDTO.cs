using System;
using System.Collections.Generic;

namespace Lanyard.Shared.DTO;

public class ReleaseNote
{
    public string Version { get; set; } = "";
    public DateOnly ReleaseDate { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Details { get; set; } = [];
}
