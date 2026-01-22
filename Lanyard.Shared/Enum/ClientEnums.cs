using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Shared.Enum;

public enum ClientGroup
{
    Music = 1,
    PacketSniffer = 2,
    Projector = 3
}

public enum ProjectionType
{
    Webpage = 1,
    CaptureSource = 2,
    StaticText = 3,
    VideoFile = 4,
    ImageFile = 5
}