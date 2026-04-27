using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.SignalR
{
    public interface ISignalRProjectionControlHub
    {
        Task<Result<bool>> SendProjectionProgramInfoToClientAsync(Guid clientId);

        event Action<Result<IEnumerable<CachedSongDTO>>>? OnReceiveCachedSongs; 
    }
}
