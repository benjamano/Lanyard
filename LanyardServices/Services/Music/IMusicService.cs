using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IMusicService
{
    Task<Result<IEnumerable<Song>>> GetSongsAsync();
}
