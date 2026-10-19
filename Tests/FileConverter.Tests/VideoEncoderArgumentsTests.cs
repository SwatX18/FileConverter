// <copyright file="VideoEncoderArgumentsTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using FileConverter.ConversionJobs;
    using FileConverter.Hardware;

    using Xunit;

    /// <summary>
    /// Preset quality (CRF = 51 - quality) -> NVENC / AMF / QSV / CPU encoder parameters. The offsets were calibrated
    /// against libx264 / libx265 sizes and VMAF (see the table in ConversionJob_FFMPEG.Converters.cs).
    /// </summary>
    public class VideoEncoderArgumentsTests
    {
        // Default Mp4 / Mkv quality 28 -> CRF 23.
        private const int DefaultQuality = 28;

        [Theory]
        [InlineData(HardwareEncoderVendor.Nvenc, VideoCodec.H264, "-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 27 -b:v 0")]
        [InlineData(HardwareEncoderVendor.Nvenc, VideoCodec.Hevc, "-c:v hevc_nvenc -preset p4 -tune hq -rc vbr -cq 28 -b:v 0")]
        [InlineData(HardwareEncoderVendor.Nvenc, VideoCodec.Av1, "-c:v av1_nvenc -preset p4 -tune hq -rc vbr -cq 36 -b:v 0")]
        [InlineData(HardwareEncoderVendor.Amf, VideoCodec.H264, "-c:v h264_amf -usage transcoding -quality balanced -rc cqp -qp_i 27 -qp_p 27 -qp_b 29")]
        [InlineData(HardwareEncoderVendor.Amf, VideoCodec.Hevc, "-c:v hevc_amf -usage transcoding -quality balanced -rc cqp -qp_i 27 -qp_p 27")]
        [InlineData(HardwareEncoderVendor.Amf, VideoCodec.Av1, "-c:v av1_amf -usage transcoding -quality balanced -rc cqp -qp_i 144 -qp_p 144")]
        [InlineData(HardwareEncoderVendor.Qsv, VideoCodec.H264, "-c:v h264_qsv -preset medium -global_quality 27")]
        [InlineData(HardwareEncoderVendor.Qsv, VideoCodec.Hevc, "-c:v hevc_qsv -preset medium -global_quality 27")]
        [InlineData(HardwareEncoderVendor.Qsv, VideoCodec.Av1, "-c:v av1_qsv -preset medium -global_quality 27")]
        public void DefaultQualityMapsToCalibratedParameters(HardwareEncoderVendor vendor, VideoCodec codec, string expected)
        {
            Assert.Equal(expected, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(vendor, codec), DefaultQuality, VideoEncodingSpeed.Medium));
        }

        [Theory]
        [InlineData(HardwareEncoderVendor.Nvenc, VideoCodec.H264, "-cq 51 ")]
        [InlineData(HardwareEncoderVendor.Nvenc, VideoCodec.Av1, "-cq 63 ")]
        [InlineData(HardwareEncoderVendor.Amf, VideoCodec.H264, "-qp_i 51 -qp_p 51 -qp_b 51")]
        [InlineData(HardwareEncoderVendor.Amf, VideoCodec.Av1, "-qp_i 255 -qp_p 255")]
        [InlineData(HardwareEncoderVendor.Qsv, VideoCodec.H264, "-global_quality 51")]
        public void LowestQualityIsClampedToTheEncoderRange(HardwareEncoderVendor vendor, VideoCodec codec, string expected)
        {
            Assert.Contains(expected, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(vendor, codec), 0, VideoEncodingSpeed.Medium));
        }

        [Theory]
        [InlineData(HardwareEncoderVendor.Nvenc, "-cq 4 ")]
        [InlineData(HardwareEncoderVendor.Amf, "-qp_i 4 ")]
        [InlineData(HardwareEncoderVendor.Qsv, "-global_quality 4")]
        public void HighestQualityKeepsTheOffset(HardwareEncoderVendor vendor, string expected)
        {
            Assert.Contains(expected, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(vendor, VideoCodec.H264), 51, VideoEncodingSpeed.Medium));
        }

        [Theory]
        [InlineData(VideoEncodingSpeed.UltraFast, "-preset p1 ", "-quality speed ", "-preset veryfast ")]
        [InlineData(VideoEncodingSpeed.Fast, "-preset p4 ", "-quality speed ", "-preset fast ")]
        [InlineData(VideoEncodingSpeed.Slow, "-preset p5 ", "-quality balanced ", "-preset slow ")]
        [InlineData(VideoEncodingSpeed.VerySlow, "-preset p7 ", "-quality quality ", "-preset veryslow ")]
        public void EncodingSpeedMapsToEachVendorPreset(VideoEncodingSpeed speed, string nvenc, string amf, string qsv)
        {
            Assert.Contains(nvenc, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(HardwareEncoderVendor.Nvenc, VideoCodec.H264), DefaultQuality, speed));
            Assert.Contains(amf, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(HardwareEncoderVendor.Amf, VideoCodec.H264), DefaultQuality, speed));
            Assert.Contains(qsv, ConversionJob_FFMPEG.HardwareEncoderArgs(HardwareEncoder.Find(HardwareEncoderVendor.Qsv, VideoCodec.H264), DefaultQuality, speed));
        }

        [Theory]
        [InlineData(VideoCodec.H264, "-c:v libx264 -preset medium -crf 23")]
        [InlineData(VideoCodec.Hevc, "-c:v libx265 -preset medium -crf 23 -x265-params log-level=error")]
        [InlineData(VideoCodec.Av1, "-c:v libaom-av1 -crf 27 -b:v 0 -cpu-used 6 -row-mt 1")]
        public void CpuFallbackUsesTheSameQuality(VideoCodec codec, string expected)
        {
            Assert.Equal(expected, ConversionJob_FFMPEG.CpuVideoEncoderArgs(codec, DefaultQuality, VideoEncodingSpeed.Medium));
        }
    }
}
