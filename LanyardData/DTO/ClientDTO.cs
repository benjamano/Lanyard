using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Infrastructure.DTO;

public class ClientConnectedDTO : Client
{
    public bool IsCurrentlyConnected { get; set; }
}

public class ClientConnectedWithCapabilitiesDTO : Client
{
    public bool IsCurrentlyConnected { get; set; }

    public bool ProjectionEnabled { get; set; }
}