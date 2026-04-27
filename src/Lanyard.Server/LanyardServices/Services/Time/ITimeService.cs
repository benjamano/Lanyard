using System.Globalization;
using Lanyard.Infrastructure.DTO;

public interface ITimeService
{
    Task<Result<CultureInfo>> GetUserCultureAsync();
    Task<Result<bool>> SetUserCultureAsync(string cultureCode);
}