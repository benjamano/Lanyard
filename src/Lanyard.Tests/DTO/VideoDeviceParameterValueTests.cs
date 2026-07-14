using System;
using Lanyard.Infrastructure.DTO.VideoDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.DTO
{
    [TestClass]
    public class VideoDeviceParameterValueTests
    {
        [TestMethod]
        public void TryParseRemote_ParsesFormattedValue()
        {
            Guid clientId = Guid.NewGuid();

            string value = VideoDeviceParameterValue.Format(clientId, "USB 2.0 Camera 2K");

            bool parsed = VideoDeviceParameterValue.TryParseRemote(value, out Guid parsedClientId, out string deviceName);

            Assert.IsTrue(parsed);
            Assert.AreEqual(clientId, parsedClientId);
            Assert.AreEqual("USB 2.0 Camera 2K", deviceName);
        }

        [TestMethod]
        public void TryParseRemote_ReturnsFalseForLegacyPlainDeviceName()
        {
            bool parsed = VideoDeviceParameterValue.TryParseRemote("USB 2.0 Camera 2K", out Guid _, out string _);

            Assert.IsFalse(parsed);
        }

        [TestMethod]
        public void TryParseRemote_ReturnsFalseForEmptyOrNull()
        {
            Assert.IsFalse(VideoDeviceParameterValue.TryParseRemote(string.Empty, out Guid _, out string _));
            Assert.IsFalse(VideoDeviceParameterValue.TryParseRemote(null, out Guid _, out string _));
            Assert.IsFalse(VideoDeviceParameterValue.TryParseRemote("   ", out Guid _, out string _));
        }

        [TestMethod]
        public void TryParseRemote_ReturnsFalseForMalformedGuid()
        {
            Assert.IsFalse(VideoDeviceParameterValue.TryParseRemote("not-a-guid::Webcam 1", out Guid _, out string _));
        }

        [TestMethod]
        public void TryParseRemote_KeepsSeparatorLikeTextInsideDeviceName()
        {
            Guid clientId = Guid.NewGuid();

            string value = VideoDeviceParameterValue.Format(clientId, "Odd::Device Name");

            bool parsed = VideoDeviceParameterValue.TryParseRemote(value, out Guid parsedClientId, out string deviceName);

            Assert.IsTrue(parsed);
            Assert.AreEqual(clientId, parsedClientId);
            Assert.AreEqual("Odd::Device Name", deviceName);
        }
    }
}
