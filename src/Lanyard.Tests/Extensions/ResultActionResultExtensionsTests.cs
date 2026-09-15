using Lanyard.API.Extensions;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Extensions;

[TestClass]
public class ResultActionResultExtensionsTests
{
    [TestMethod]
    public void ToActionResult_SuccessResult_ReturnsOkObjectResultWrappingTheSameResult()
    {
        Result<string> result = Result<string>.Ok("data");

        IActionResult actionResult = result.ToActionResult();

        OkObjectResult okResult = (OkObjectResult)actionResult;
        Assert.AreSame(result, okResult.Value);
    }

    [TestMethod]
    public void ToActionResult_FailedResult_ReturnsBadRequestObjectResultWrappingTheSameResult()
    {
        Result<string> result = Result<string>.Fail("something went wrong");

        IActionResult actionResult = result.ToActionResult();

        BadRequestObjectResult badRequestResult = (BadRequestObjectResult)actionResult;
        Assert.AreSame(result, badRequestResult.Value);
    }
}
