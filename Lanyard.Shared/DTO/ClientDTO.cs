using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Shared.DTO;

public class ClientAvailableScreenDTO
{
    public Guid ClientId { get; set; }

    public required string Name { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    public int Index { get; set; }
}

public class ClientAvailableAudioDeviceDTO
{
    public Guid ClientId { get; set; }

    public required string Id { get; set; }
    public required string Name { get; set; }
}

public class ClientProjectionSettingsDTO
{
    public int DisplayIndex { get; set; } = 0;

    public required ProjectionProgramDTO ProjectionProgram { get; set; }

    public bool IsFullScreen { get; set; } = true;
    public bool IsBorderless { get; set; } = true;

    public int Width { get; set; }
    public int Height { get; set; }
}