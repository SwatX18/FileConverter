// <copyright file="HardwareEncoder.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Collections.Generic;

    public sealed class HardwareEncoder
    {
        /// <summary>
        /// All the encoders that are tested, in priority order for each codec.
        /// </summary>
        public static readonly IReadOnlyList<HardwareEncoder> Candidates = new[]
        {
            new HardwareEncoder(HardwareEncoderVendor.Nvenc, VideoCodec.H264, "h264_nvenc"),
            new HardwareEncoder(HardwareEncoderVendor.Nvenc, VideoCodec.Hevc, "hevc_nvenc"),
            new HardwareEncoder(HardwareEncoderVendor.Nvenc, VideoCodec.Av1, "av1_nvenc"),
            new HardwareEncoder(HardwareEncoderVendor.Amf, VideoCodec.H264, "h264_amf"),
            new HardwareEncoder(HardwareEncoderVendor.Amf, VideoCodec.Hevc, "hevc_amf"),
            new HardwareEncoder(HardwareEncoderVendor.Amf, VideoCodec.Av1, "av1_amf"),
            new HardwareEncoder(HardwareEncoderVendor.Qsv, VideoCodec.H264, "h264_qsv"),
            new HardwareEncoder(HardwareEncoderVendor.Qsv, VideoCodec.Hevc, "hevc_qsv"),
            new HardwareEncoder(HardwareEncoderVendor.Qsv, VideoCodec.Av1, "av1_qsv"),
        };

        private HardwareEncoder(HardwareEncoderVendor vendor, VideoCodec codec, string ffmpegName)
        {
            this.Vendor = vendor;
            this.Codec = codec;
            this.FfmpegName = ffmpegName;
        }

        public HardwareEncoderVendor Vendor { get; }

        public VideoCodec Codec { get; }

        /// <summary>
        /// The ffmpeg encoder name (<c>-c:v</c> value).
        /// </summary>
        public string FfmpegName { get; }

        public static HardwareEncoder Find(HardwareEncoderVendor vendor, VideoCodec codec)
        {
            foreach (HardwareEncoder candidate in HardwareEncoder.Candidates)
            {
                if (candidate.Vendor == vendor && candidate.Codec == codec)
                {
                    return candidate;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(codec), $"No {vendor} encoder for codec {codec}.");
        }

        public static HardwareEncoder FromFfmpegName(string ffmpegName)
        {
            foreach (HardwareEncoder candidate in HardwareEncoder.Candidates)
            {
                if (candidate.FfmpegName == ffmpegName)
                {
                    return candidate;
                }
            }

            return null;
        }

        public override string ToString() => this.FfmpegName;
    }
}
