using Lanyard.API.Controllers;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lanyard.Tests.Services.Files
{
    [TestClass]
    public class FilesControllerTests
    {
        [TestMethod]
        public async Task WhenUploadingFileThenReturnsOk()
        {
            // TODO: Implement integration test for FilesController.Upload
        }

        [TestMethod]
        public async Task WhenUploadingInvalidFileThenReturnsBadRequest()
        {
            // TODO: Implement integration test for FilesController.Upload with invalid file
        }
    }
}
