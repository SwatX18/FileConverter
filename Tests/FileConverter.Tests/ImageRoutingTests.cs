// <copyright file="ImageRoutingTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;

    using FileConverter.ConversionJobs;
    using FileConverter.Imaging;

    using Xunit;

    /// <summary>
    /// Which job converts a file (factory), and for images whether libvips or ImageMagick does it (ConversionJob_Vips).
    /// The cases routed to libvips need the libvips DLLs next to the test assembly (copied by the build).
    /// </summary>
    public class ImageRoutingTests : IDisposable
    {
        public ImageRoutingTests()
        {
            TestServices.Settings.ImageEngine = Helpers.ImageEngine.Auto;
        }

        public void Dispose()
        {
            TestServices.Settings.ImageEngine = Helpers.ImageEngine.Auto;
        }

        [Theory]
        [InlineData("a.jpg", OutputType.Png, typeof(ConversionJob_Vips))]
        [InlineData("a.cr2", OutputType.Jpg, typeof(ConversionJob_Vips))]
        [InlineData("a.png", OutputType.Avif, typeof(ConversionJob_Vips))]
        [InlineData("a.png", OutputType.Webp, typeof(ConversionJob_Vips))]
        [InlineData("a.png", OutputType.Ico, typeof(ConversionJob_Ico))]
        [InlineData("a.mp4", OutputType.Gif, typeof(ConversionJob_Gif))]
        [InlineData("a.png", OutputType.Pdf, typeof(ConversionJob_ImageMagick))]
        [InlineData("a.mkv", OutputType.Mp4, typeof(ConversionJob_FFMPEG))]
        [InlineData("a.flac", OutputType.Mp3, typeof(ConversionJob_FFMPEG))]
        [InlineData("a.docx", OutputType.Pdf, typeof(ConversionJob_Word))]
        [InlineData("a.xlsx", OutputType.Pdf, typeof(ConversionJob_Excel))]
        [InlineData("a.pptx", OutputType.Pdf, typeof(ConversionJob_PowerPoint))]
        public void FactoryPicksTheJobType(string inputFile, OutputType outputType, Type expectedJobType)
        {
            TestServices.Initialize();

            ConversionJob job = ConversionJobFactory.Create(new ConversionPreset("test", outputType), inputFile);

            Assert.IsType(expectedJobType, job);
        }

        [Theory]
        [InlineData("a.jpg", OutputType.Png)]
        [InlineData("a.JPEG", OutputType.Webp)]
        [InlineData("a.png", OutputType.Jpg)]
        [InlineData("a.webp", OutputType.Png)]
        [InlineData("a.tif", OutputType.Avif)]
        [InlineData("a.avif", OutputType.Jpg)]
        [InlineData("a.heic", OutputType.Jpg)]
        public void LibvipsConvertsCommonFormats(string inputFile, OutputType outputType)
        {
            Assert.True(VipsEngine.IsAvailable, $"libvips is not loaded: {VipsEngine.UnavailableReason}");

            Assert.Null(CreateImageJob(inputFile, outputType).GetBlocker());
        }

        [Theory]
        [InlineData("a.cr2", OutputType.Jpg, ".cr2 files are read by ImageMagick")]
        [InlineData("a.dng", OutputType.Jpg, ".dng files are read by ImageMagick")]
        [InlineData("a.gif", OutputType.Png, ".gif files are read by ImageMagick")]
        [InlineData("a.psd", OutputType.Png, ".psd files are read by ImageMagick")]
        [InlineData("a.svg", OutputType.Png, ".svg files are read by ImageMagick")]
        public void ImageMagickConvertsOtherFormats(string inputFile, OutputType outputType, string expectedReason)
        {
            Assert.Equal(expectedReason, CreateImageJob(inputFile, outputType).GetBlocker());
        }

        [Fact]
        public void ImageMagickOnlySettingDisablesLibvips()
        {
            TestServices.Settings.ImageEngine = Helpers.ImageEngine.ImageMagick;

            Assert.Equal("image engine set to ImageMagick only", CreateImageJob("a.jpg", OutputType.Png).GetBlocker());
        }

        [Theory]
        [InlineData("90", true)]
        [InlineData("180", true)]
        [InlineData("270", true)]
        [InlineData("45", false)]
        public void OnlyQuarterTurnsAreRotatedByLibvips(string rotation, bool libvips)
        {
            Assert.True(VipsEngine.IsAvailable, $"libvips is not loaded: {VipsEngine.UnavailableReason}");
            ConversionJob_Vips job = CreateImageJob("a.jpg", OutputType.Png);
            job.ConversionPreset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageRotation, rotation);

            string blocker = job.GetBlocker();

            Assert.Equal(libvips, blocker == null);
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(90f, 1)]
        [InlineData(180f, 2)]
        [InlineData(270f, 3)]
        [InlineData(-90f, 3)]
        [InlineData(450f, 1)]
        [InlineData(90.01f, 1)]
        [InlineData(90.1f, -1)]
        [InlineData(45f, -1)]
        public void QuarterTurns(float degrees, int expected)
        {
            Assert.Equal(expected, ConversionJob_Vips.GetQuarterTurns(degrees));
        }

        [Theory]
        [InlineData("1", "False", "0", 6000, 4000)]
        [InlineData("0.25", "False", "0", 1500, 1000)]
        [InlineData("0.333", "False", "0", 1998, 1332)]
        [InlineData("1", "False", "1024", 1024, 683)]
        [InlineData("1", "False", "8000", 6000, 4000)]
        [InlineData("1", "True", "0", 2048, 1365)]
        [InlineData("0.25", "True", "300", 300, 200)]
        public void OutputSizeMatchesImageMagick(string scale, string clampPowerOf2, string maximumSize, int expectedWidth, int expectedHeight)
        {
            ConversionJob_Vips job = CreateImageJob("a.jpg", OutputType.Png);
            job.ConversionPreset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageScale, scale);
            job.ConversionPreset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageClampSizePowerOf2, clampPowerOf2);
            job.ConversionPreset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageMaximumSize, maximumSize);

            job.ComputeOutputSize(6000, 4000, out int width, out int height);

            Assert.Equal((expectedWidth, expectedHeight), (width, height));
        }

        private static ConversionJob_Vips CreateImageJob(string inputFile, OutputType outputType)
        {
            TestServices.Initialize();
            return new ConversionJob_Vips(new ConversionPreset("test", outputType), inputFile);
        }
    }
}
