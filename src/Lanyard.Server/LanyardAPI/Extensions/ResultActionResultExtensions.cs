using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Extensions;

public static class ResultActionResultExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result) =>
        result.IsSuccess ? new OkObjectResult(result) : new BadRequestObjectResult(result);
}
