using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lanyard.Tests.Services.Files
{
    [TestClass]
    public class FileServiceTests
    {
        [TestMethod]
        public async Task WhenUploadingValidFileThenFileIsSaved()
        {
            // TODO: Implement test for UploadFileAsync
        }

        [TestMethod]
        public async Task WhenDeletingFileThenFileIsRemoved()
        {
            // TODO: Implement test for DeleteFileAsync
        }

        [TestMethod]
        public async Task WhenRenamingFileThenNameIsUpdated()
        {
            // TODO: Implement test for RenameFileAsync
        }

        [TestMethod]
        public async Task WhenGeneratingThumbnailForVideoThenThumbnailIsCreated()
        {
            // TODO: Implement test for GenerateVideoThumbnailAsync
        }
    }
}
