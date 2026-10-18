// <copyright file="ConversionJob_FFMPEG.Converters.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using CommunityToolkit.Mvvm.DependencyInjection;

    using FileConverter.Controls;
    using FileConverter.Hardware;

    public partial class ConversionJob_FFMPEG
    {
        private static string Encapsulate(string optionName, string args)
        {
            if (string.IsNullOrEmpty(args))
            {
                return string.Empty;
            }

            return $"{optionName} \"{args}\"";
        }

        /// <summary>
        /// Compute the argument needed to change the number of channels in an audio file.
        /// </summary>
        /// <param name="conversionPreset">The conversion preset</param>
        /// <returns>The argument string.</returns>
        /// https://trac.ffmpeg.org/wiki/AudioChannelManipulation
        private static string ComputeAudioChannelArgs(ConversionPreset conversionPreset)
        {
            string channelArgs = string.Empty;
            int channelCount = conversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioChannelCount);
            if (channelCount > 0)
            {
                channelArgs = $"-ac {channelCount}";
            }

            return channelArgs;
        }

        /// <param name="cudaFrames">True when the frames stay in GPU memory (<c>-hwaccel_output_format cuda</c>):
        /// only CUDA filters can be used, so rotation is not supported (callers must check <see cref="HasRotation"/>).</param>
        /// <param name="pixelFormat">Output pixel format of the Mp4 / Mkv presets (8-bit <c>yuv420p</c>, or a 10-bit 4:2:0
        /// format for HEVC / AV1 from 10-bit sources, see <see cref="VideoPixelFormats"/>).</param>
        private static string ComputeTransformArgs(ConversionPreset conversionPreset, bool cudaFrames = false, string pixelFormat = "yuv420p")
        {
            float scaleFactor = conversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.VideoScale);
            int resolution = conversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoResolution);
            string scaleArgs = string.Empty;

            if (resolution > 0)
            {
                // Target resolution ("720p"): the shorter side becomes the given size (landscape: height, portrait: width),
                // the other side keeps the aspect ratio and is rounded to an even value (-2) for 4:2:0 encoders.
                // The commas are quoted because they would otherwise separate filters.
                string width = $"'if(gte(iw,ih),-2,{resolution})'";
                string height = $"'if(gte(iw,ih),{resolution},-2)'";
                scaleArgs = cudaFrames ? $"scale_cuda=w={width}:h={height}:format={pixelFormat}" : $"scale=w={width}:h={height}";
            }
            else if (conversionPreset.OutputType == OutputType.Mkv || conversionPreset.OutputType == OutputType.Mp4)
            {
                // This presets use h264 codec, the size of the video need to be divisible by 2.
                if (cudaFrames)
                {
                    scaleArgs = string.Format("scale_cuda=trunc(iw*{0}/2)*2:trunc(ih*{0}/2)*2:format={1}", scaleFactor.ToString("#.##", CultureInfo.InvariantCulture), pixelFormat);
                }
                else
                {
                    scaleArgs = string.Format("scale=trunc(iw*{0}/2)*2:trunc(ih*{0}/2)*2", scaleFactor.ToString("#.##", CultureInfo.InvariantCulture));
                }
            }
            else if (Math.Abs(scaleFactor - 1f) >= 0.005f)
            {
                scaleArgs = string.Format("scale=iw*{0}:ih*{0}", scaleFactor.ToString("#.##", CultureInfo.InvariantCulture));
            }

            float rotationAngleInDegrees = conversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.VideoRotation);
            string rotationArgs = string.Empty;
            if (Math.Abs(rotationAngleInDegrees - 0f) >= 0.05f)
            {
                // Transpose:
                // 0: 90 CounterClockwise and vertical flip
                // 1: 90 Clockwise
                // 2: 90 CounterClockwise 
                // 3: 90 Clockwise and vertical flip
                if (Math.Abs(rotationAngleInDegrees - 90f) <= 0.05f)
                {
                    rotationArgs = "transpose=2";
                }
                else if (Math.Abs(rotationAngleInDegrees - 180f) <= 0.05f)
                {
                    rotationArgs = "vflip,hflip";
                }
                else if (Math.Abs(rotationAngleInDegrees - 270f) <= 0.05f)
                {
                    rotationArgs = "transpose=1";
                }
                else
                {
                    Diagnostics.Debug.LogError($"Unsupported rotation: {rotationAngleInDegrees}°");
                }
            }

            // Build vf args content (scale=..., transpose=...)
            string transformArgs = string.Empty;
            if (!string.IsNullOrEmpty(scaleArgs))
            {
                transformArgs += scaleArgs;
            }

            if (!string.IsNullOrEmpty(rotationArgs))
            {
                if (!string.IsNullOrEmpty(transformArgs))
                {
                    transformArgs += ",";
                }

                transformArgs += rotationArgs;
            }

            if (!cudaFrames && (conversionPreset.OutputType == OutputType.Mkv || conversionPreset.OutputType == OutputType.Mp4))
            {
                // For MP4/MKV, force 4:2:0 (8-bit for H.264) for broad player compatibility:
                // http://trac.ffmpeg.org/wiki/Encode/H.264#Encodingfordumbplayers
                // - Software encoding and hardware encoders fed from system memory come through this path.
                // - CUDA frames are excluded here because the scale_cuda path above already sets the format.
                transformArgs += (transformArgs.Length > 0 ? "," : string.Empty) + $"format={pixelFormat}";
                //// TODO: maybe there should be an option for this on the settings?
            }

            return transformArgs;
        }

        private static bool HasRotation(ConversionPreset conversionPreset)
        {
            float rotationAngleInDegrees = conversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.VideoRotation);
            return Math.Abs(rotationAngleInDegrees - 0f) >= 0.05f;
        }

        /// <summary>
        /// Builds the ordered encode attempts of the Mp4 / Mkv presets: the hardware paths first (when a hardware
        /// encoder is selected), and always the CPU encoder last, so a failing GPU path never fails the conversion.
        /// </summary>
        /// <param name="inputOutputArgs">Formats the final command from (input args, encoder args, -vf args).</param>
        /// <param name="tenBit">Encode 10-bit 4:2:0 (HEVC / AV1 from a 10-bit source), 8-bit 4:2:0 otherwise.</param>
        private List<FFMpegAttempt> BuildVideoEncodeAttempts(VideoCodec codec, int quality, VideoEncodingSpeed speed, bool tenBit, Func<string, string, string, string> inputOutputArgs)
        {
            List<FFMpegAttempt> attempts = new List<FFMpegAttempt>();

            // Hardware encoders take 10-bit as P010, the CPU encoders (libx265, libaom) as planar yuv420p10le.
            string hardwarePixelFormat = tenBit ? VideoPixelFormats.Hardware10Bit : VideoPixelFormats.Yuv420p;
            string cpuPixelFormat = tenBit ? VideoPixelFormats.Cpu10Bit : VideoPixelFormats.Yuv420p;
            string hardwareFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset, pixelFormat: hardwarePixelFormat));
            string cpuFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset, pixelFormat: cpuPixelFormat));

            HardwareEncoder encoder = this.ResolveHardwareEncoder(codec);
            if (encoder != null)
            {
                string encoderArgs = this.HardwareEncoderArgs(encoder, quality, speed);
                switch (encoder.Vendor)
                {
                    case HardwareEncoderVendor.Nvenc:
                        if (!ConversionJob_FFMPEG.HasRotation(this.ConversionPreset))
                        {
                            // Full GPU pipeline: NVDEC decode, scale_cuda, NVENC encode. Fails at startup (and falls back) when
                            // NVDEC can't decode the source (e.g. ProRes) or scale_cuda can't convert its pixel format.
                            string cudaFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset, cudaFrames: true, pixelFormat: hardwarePixelFormat));
                            attempts.Add(new FFMpegAttempt(EncodingPaths.Nvenc, inputOutputArgs("-hwaccel cuda -hwaccel_output_format cuda", encoderArgs, cudaFilteringArgs), usesGpu: true));
                        }

                        // Frames downloaded to system memory: CPU filters (rotation, pixel format), NVENC encode.
                        // ffmpeg decodes on the CPU by itself when NVDEC doesn't support the source.
                        attempts.Add(new FFMpegAttempt(EncodingPaths.NvencCpuFilters, inputOutputArgs("-hwaccel cuda", encoderArgs, hardwareFilteringArgs), usesGpu: true));
                        break;

                    case HardwareEncoderVendor.Amf:
                    case HardwareEncoderVendor.Qsv:
                        // Hardware decode on the default adapter, then the same encoder with CPU decode: on multi-GPU machines
                        // (e.g. NVIDIA dGPU + AMD iGPU) the decoder's device is not the encoder's, and AMF fails to initialize
                        // ("Failed to create derived AMF device context"). That failure is immediate, so the retry is cheap.
                        bool isAmf = encoder.Vendor == HardwareEncoderVendor.Amf;
                        attempts.Add(new FFMpegAttempt(isAmf ? EncodingPaths.Amf : EncodingPaths.Qsv, inputOutputArgs("-hwaccel auto", encoderArgs, hardwareFilteringArgs), usesGpu: true));
                        attempts.Add(new FFMpegAttempt(isAmf ? EncodingPaths.AmfCpuDecode : EncodingPaths.QsvCpuDecode, inputOutputArgs(string.Empty, encoderArgs, hardwareFilteringArgs), usesGpu: true));
                        break;
                }
            }

            string cpuPath = attempts.Count > 0 ? EncodingPaths.CpuFallback : EncodingPaths.Cpu;
            attempts.Add(new FFMpegAttempt(cpuPath, inputOutputArgs(string.Empty, this.CpuVideoEncoderArgs(codec, quality, speed), cpuFilteringArgs), usesGpu: false));

            return attempts;
        }

        /// <summary>
        /// Hardware encoder selected by the hardware acceleration setting, or null to encode on the CPU.
        /// Explicit modes (CUDA / AMF) are used even if detection didn't confirm them: a failure falls back to the CPU.
        /// </summary>
        private HardwareEncoder ResolveHardwareEncoder(VideoCodec codec)
        {
            switch (this.settingsService.Settings.HardwareAccelerationMode)
            {
                case Helpers.HardwareAccelerationMode.Auto:
                    IHardwareEncoderDetector detector = Ioc.Default.GetService<IHardwareEncoderDetector>();
                    return detector?.GetPreferredEncoder(codec);

                case Helpers.HardwareAccelerationMode.CUDA:
                    return HardwareEncoder.Find(HardwareEncoderVendor.Nvenc, codec);

                case Helpers.HardwareAccelerationMode.AMF:
                    return HardwareEncoder.Find(HardwareEncoderVendor.Amf, codec);

                default:
                    return null;
            }
        }

        // Quality mapping. The preset quality slider is converted to a CPU CRF (H264QualityToCRF: crf = 51 - quality),
        // then each encoder gets the parameter that gives the same file size as the CPU encode at that CRF.
        // Calibrated on three 40 s excerpts of Sintel 1080p (real content), CRF 18 / 21 / 24 / 28, ffmpeg 8.0.1,
        // RTX 5070 Ti (driver 610.88) and Radeon 7950X3D iGPU, video only, -vf format=yuv420p:
        //
        //   encoder      parameter             vs CPU reference      size at equal setting   notes
        //   h264_nvenc   -rc vbr -cq crf+4     libx264 -crf          -5% .. +5%              VMAF 97.2 vs 96.2 (x264) at CRF 21
        //   hevc_nvenc   -rc vbr -cq crf+5     libx265 -crf          -5% .. +6%
        //   av1_nvenc    -rc vbr -cq crf+13    x264 / x265 VMAF      (equal VMAF)            CRF 21 -> cq 34: VMAF 96.4, 7.5 MB vs 17.7 MB x264
        //   h264_amf     -rc cqp -qp crf+4     libx264 -crf          -3% .. +5%              upstream mapping (qp = crf) was +72%
        //   hevc_amf     -rc cqp -qp crf+4     libx265 -crf          -5% .. -0%
        //   libx265      -crf crf              libx264 VMAF          (equal VMAF)            CRF 21: VMAF 96.3 vs 96.2 at 54% of the size
        //   libaom-av1   -crf crf+4            x264 VMAF slope       (estimated)             3x slower than real time at cpu-used 6
        //   av1_amf, *_qsv                     not calibrated (no AV1-capable AMD / no Intel GPU on the test machine):
        //                                      same offsets as the calibrated encoders of the same codec.
        private const int NvencH264CqOffset = 4;
        private const int NvencHevcCqOffset = 5;
        private const int Av1CqOffset = 13;
        private const int AmfQpOffset = 4;
        private const int QsvQualityOffset = 4;
        private const int LibaomCrfOffset = 4;

        private static class VideoPixelFormats
        {
            /// <summary>
            /// 8-bit 4:2:0: H.264 always (10-bit H.264 barely plays anywhere), HEVC / AV1 from 8-bit sources.
            /// </summary>
            public const string Yuv420p = "yuv420p";

            /// <summary>
            /// 10-bit 4:2:0 for NVENC / AMF / QSV and scale_cuda.
            /// </summary>
            public const string Hardware10Bit = "p010le";

            /// <summary>
            /// 10-bit 4:2:0 for libx265 / libaom.
            /// </summary>
            public const string Cpu10Bit = "yuv420p10le";
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private string HardwareEncoderArgs(HardwareEncoder encoder, int quality, VideoEncodingSpeed speed)
        {
            int crf = this.H264QualityToCRF(quality);
            switch (encoder.Vendor)
            {
                case HardwareEncoderVendor.Nvenc:
                    {
                        int cq;
                        switch (encoder.Codec)
                        {
                            case VideoCodec.Hevc:
                                cq = Clamp(crf + NvencHevcCqOffset, 0, 51);
                                break;
                            case VideoCodec.Av1:
                                cq = Clamp(crf + Av1CqOffset, 0, 63);
                                break;
                            default:
                                cq = Clamp(crf + NvencH264CqOffset, 0, 51);
                                break;
                        }

                        return $"-c:v {encoder.FfmpegName} -preset {this.H264EncodingSpeedToNVENCPreset(speed)} -tune hq -rc vbr -cq {cq} -b:v 0";
                    }

                case HardwareEncoderVendor.Amf:
                    {
                        string amfQuality = this.H264EncodingSpeedToAMFQuality(speed);
                        switch (encoder.Codec)
                        {
                            case VideoCodec.Hevc:
                                {
                                    int qp = Clamp(crf + AmfQpOffset, 0, 51);
                                    return $"-c:v {encoder.FfmpegName} -usage transcoding -quality {amfQuality} -rc cqp -qp_i {qp} -qp_p {qp}";
                                }

                            case VideoCodec.Av1:
                                {
                                    // av1_amf quantizers range from 0 to 255 (AV1 q-index): 4x the 0..63 scale.
                                    int qp = Clamp((crf + Av1CqOffset) * 4, 0, 255);
                                    return $"-c:v {encoder.FfmpegName} -usage transcoding -quality {amfQuality} -rc cqp -qp_i {qp} -qp_p {qp}";
                                }

                            default:
                                {
                                    int qp = Clamp(crf + AmfQpOffset, 0, 51);
                                    int bFrameQp = Math.Min(51, qp + 2);
                                    return $"-c:v {encoder.FfmpegName} -usage transcoding -quality {amfQuality} -rc cqp -qp_i {qp} -qp_p {qp} -qp_b {bFrameQp}";
                                }
                        }
                    }

                case HardwareEncoderVendor.Qsv:
                    {
                        // ICQ mode. Not calibrated (no Intel GPU on the test machine); av1_qsv is assumed to use the
                        // same global_quality scale as h264/hevc_qsv. A failing encode falls back to the CPU anyway.
                        int globalQuality = Clamp(crf + QsvQualityOffset, 1, 51);
                        return $"-c:v {encoder.FfmpegName} -preset {this.EncodingSpeedToQSVPreset(speed)} -global_quality {globalQuality}";
                    }
            }

            throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown hardware encoder.");
        }

        private string CpuVideoEncoderArgs(VideoCodec codec, int quality, VideoEncodingSpeed speed)
        {
            int crf = this.H264QualityToCRF(quality);
            switch (codec)
            {
                case VideoCodec.Hevc:
                    return $"-c:v libx265 -preset {this.H264EncodingSpeedToPreset(speed)} -crf {crf} -x265-params log-level=error";

                case VideoCodec.Av1:
                    return $"-c:v libaom-av1 -crf {Clamp(crf + LibaomCrfOffset, 0, 63)} -b:v 0 -cpu-used {this.EncodingSpeedToLibaomCpuUsed(speed)} -row-mt 1";

                default:
                    return $"-c:v libx264 -preset {this.H264EncodingSpeedToPreset(speed)} -crf {crf}";
            }
        }

        /// <summary>
        /// Convert video encoding speed to Intel Quick Sync preset.
        /// </summary>
        private string EncodingSpeedToQSVPreset(VideoEncodingSpeed encodingSpeed)
        {
            switch (encodingSpeed)
            {
                case VideoEncodingSpeed.UltraFast:
                case VideoEncodingSpeed.SuperFast:
                case VideoEncodingSpeed.VeryFast:
                    return "veryfast";

                case VideoEncodingSpeed.Faster:
                    return "faster";

                case VideoEncodingSpeed.Fast:
                    return "fast";

                case VideoEncodingSpeed.Medium:
                    return "medium";

                case VideoEncodingSpeed.Slow:
                    return "slow";

                case VideoEncodingSpeed.Slower:
                    return "slower";

                case VideoEncodingSpeed.VerySlow:
                    return "veryslow";
            }

            throw new ArgumentOutOfRangeException(nameof(encodingSpeed), encodingSpeed, "Unknown encoding speed.");
        }

        /// <summary>
        /// Convert video encoding speed to libaom <c>-cpu-used</c> (0 = slowest, 8 = fastest).
        /// libaom is very slow: even the "medium" mapping (6) is slower than real time at 1080p.
        /// </summary>
        private int EncodingSpeedToLibaomCpuUsed(VideoEncodingSpeed encodingSpeed)
        {
            switch (encodingSpeed)
            {
                case VideoEncodingSpeed.UltraFast:
                case VideoEncodingSpeed.SuperFast:
                    return 8;

                case VideoEncodingSpeed.VeryFast:
                case VideoEncodingSpeed.Faster:
                    return 7;

                case VideoEncodingSpeed.Fast:
                case VideoEncodingSpeed.Medium:
                    return 6;

                case VideoEncodingSpeed.Slow:
                    return 5;

                case VideoEncodingSpeed.Slower:
                    return 4;

                case VideoEncodingSpeed.VerySlow:
                    return 3;
            }

            throw new ArgumentOutOfRangeException(nameof(encodingSpeed), encodingSpeed, "Unknown encoding speed.");
        }

        /// <summary>
        /// Convert bitrate in <c>mp3</c> encoder quality index.
        /// </summary>
        /// <param name="bitrate">Wanted bitrate value.</param>
        /// <returns>The <c>mp3</c> encoder quality index associated to the given bitrate value.</returns>
        /// https://trac.ffmpeg.org/wiki/Encode/MP3
        private int MP3VBRBitrateToQualityIndex(int bitrate)
        {
            switch (bitrate)
            {
                case 245:
                    return 0;

                case 225:
                    return 1;

                case 190:
                    return 2;

                case 175:
                    return 3;

                case 165:
                    return 4;

                case 130:
                    return 5;

                case 115:
                    return 6;

                case 100:
                    return 7;

                case 85:
                    return 8;

                case 65:
                    return 9;
            }

            throw new Exception("Unknown VBR bitrate.");
        }

        /// <summary>
        /// Convert bitrate in <c>vorbis</c> encoder quality index.
        /// </summary>
        /// <param name="bitrate">Wanted bitrate value.</param>
        /// <returns>The <c>vorbis</c> encoder quality index associated to the given bitrate value.</returns>
        /// http://wiki.hydrogenaud.io/index.php?title=Recommended_Ogg_Vorbis
        private int OGGVBRBitrateToQualityIndex(int bitrate)
        {
            switch (bitrate)
            {
                case 500:
                    return 10;

                case 320:
                    return 9;

                case 256:
                    return 8;

                case 224:
                    return 7;

                case 192:
                    return 6;

                case 160:
                    return 5;

                case 128:
                    return 4;

                case 112:
                    return 3;

                case 96:
                    return 2;

                case 80:
                    return 1;

                case 64:
                    return 0;

                case 48:
                    return -1;

                case 32:
                    return -2;
            }

            throw new Exception("Unknown Ogg VBR bitrate.");
        }

        /// <summary>
        /// Convert video quality index to lib <c>theora</c> video quality level.
        /// </summary>
        /// <param name="quality">The quality index.</param>
        /// <returns>Returns the video quality index.</returns>
        /// The range of the video quality level is 0-10: where 10 is the highest quality/largest filesize, 0 being the lowest quality/smallest filesize.
        /// https://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
        private int OGVTheoraQualityToQualityIndex(int quality)
        {
            return quality;
        }

        /// <summary>
        /// Convert encoding mode setting to <c>ffmpeg</c> argument option.
        /// </summary>
        /// <param name="encoding">The encoding mode setting.</param>
        /// <returns>Returns the <c>ffmpeg</c> argument corresponding to the given encoding mode.</returns>
        /// https://trac.ffmpeg.org/wiki/audio%20types
        private string WAVEncodingToCodecArgument(EncodingMode encoding)
        {
            switch (encoding)
            {
                case EncodingMode.Wav8:
                    return "pcm_s8le";

                case EncodingMode.Wav16:
                    return "pcm_s16le";

                case EncodingMode.Wav24:
                    return "pcm_s24le";

                case EncodingMode.Wav32:
                    return "pcm_s32le";
            }

            throw new Exception("Unknown Wav encoding.");
        }

        /// <summary>
        /// Convert video quality index to MPEG4 video quality level.
        /// </summary>
        /// <param name="quality">The quality index.</param>
        /// <returns>Returns the H264 constant rate factor.</returns>
        /// The range of the video quality level is 1-31: where 1 is the highest quality/largest filesize, 31 being the lowest quality/smallest filesize.
        /// https://trac.ffmpeg.org/wiki/Encode/MPEG-4
        private int MPEG4QualityToQualityIndex(int quality)
        {
            return 31 - quality;
        }

        /// <summary>
        /// Convert video quality index to H264 constant rate factor.
        /// </summary>
        /// <param name="quality">The quality index.</param>
        /// <returns>Returns the H264 constant rate factor.</returns>
        /// The range of the quantizer scale is 0-51: where 0 is lossless, 23 is default, and 51 is worst possible. 
        /// A lower value is a higher quality and a subjectively sane range is 18-28. 
        /// https://trac.ffmpeg.org/wiki/Encode/H.264
        private int H264QualityToCRF(int quality)
        {
            return 51 - quality;
        }

        /// <summary>
        /// Convert image quality index to JPG quality index.
        /// </summary>
        /// <param name="quality">The quality index.</param>
        /// <returns>Returns the JPG quality index.</returns>
        /// The range of the quantizer scale is 1-31: where 1 is better quality, and 31 is worst possible. 
        /// http://superuser.com/questions/318845/improve-quality-of-ffmpeg-created-jpgs
        private int JPGQualityToQualityIndex(int quality)
        {
            return 31 - quality;
        }

        /// <summary>
        /// Convert video encoding speed to H264 preset.
        /// </summary>
        /// <param name="encodingSpeed">The encoding speed.</param>
        /// <returns>The H264 preset.</returns>
        private string H264EncodingSpeedToPreset(VideoEncodingSpeed encodingSpeed)
        {
            switch (encodingSpeed)
            {
                case VideoEncodingSpeed.UltraFast:
                    return "ultrafast";

                case VideoEncodingSpeed.SuperFast:
                    return "superfast";

                case VideoEncodingSpeed.VeryFast:
                    return "veryfast";

                case VideoEncodingSpeed.Faster:
                    return "faster";

                case VideoEncodingSpeed.Fast:
                    return "fast";

                case VideoEncodingSpeed.Medium:
                    return "medium";

                case VideoEncodingSpeed.Slow:
                    return "slow";

                case VideoEncodingSpeed.Slower:
                    return "slower";

                case VideoEncodingSpeed.VerySlow:
                    return "veryslow";
            }

            throw new ArgumentOutOfRangeException(nameof(encodingSpeed), encodingSpeed, "Unknown H264 encoding speed.");
        }

        /// <summary>
        /// Convert video encoding speed to NVENC preset.
        /// </summary>
        /// <param name="encodingSpeed">The encoding speed.</param>
        /// <returns>The NVENC preset.</returns>
        private string H264EncodingSpeedToNVENCPreset(VideoEncodingSpeed encodingSpeed)
        {
            switch (encodingSpeed)
            {
                case VideoEncodingSpeed.UltraFast:
                    return "p1";

                case VideoEncodingSpeed.SuperFast:
                    return "p2";

                case VideoEncodingSpeed.VeryFast:
                    return "p3";

                case VideoEncodingSpeed.Faster:
                case VideoEncodingSpeed.Fast:
                case VideoEncodingSpeed.Medium:
                    return "p4";

                case VideoEncodingSpeed.Slow:
                    return "p5";

                case VideoEncodingSpeed.Slower:
                    return "p6";

                case VideoEncodingSpeed.VerySlow:
                    return "p7";
            }

            throw new ArgumentOutOfRangeException(nameof(encodingSpeed), encodingSpeed, "Unknown H264 encoding speed.");
        }

        /// <summary>
        /// Convert video encoding speed to AMF quality mode.
        /// </summary>
        /// <param name="encodingSpeed">The encoding speed.</param>
        /// <returns>The AMF quality mode.</returns>
        private string H264EncodingSpeedToAMFQuality(VideoEncodingSpeed encodingSpeed)
        {
            switch (encodingSpeed)
            {
                case VideoEncodingSpeed.UltraFast:
                case VideoEncodingSpeed.SuperFast:
                case VideoEncodingSpeed.VeryFast:
                case VideoEncodingSpeed.Faster:
                case VideoEncodingSpeed.Fast:
                    return "speed";

                case VideoEncodingSpeed.Medium:
                case VideoEncodingSpeed.Slow:
                    return "balanced";

                case VideoEncodingSpeed.Slower:
                case VideoEncodingSpeed.VerySlow:
                    return "quality";
            }

            throw new ArgumentOutOfRangeException(nameof(encodingSpeed), encodingSpeed, "Unknown H264 encoding speed.");
        }

        /// <summary>
        /// Convert bitrate in <c>aac</c> encoder quality index.
        /// </summary>
        /// <param name="bitrate">Wanted bitrate value.</param>
        /// <returns>The <c>aac</c> encoder quality index associated to the given bitrate value.</returns>
        /// See the file Resources/FFMPEG retro engineering.ods for details.
        private string AACBitrateToQualityIndex(int bitrate)
        {
            switch (bitrate)
            {
                case 460:
                    return "3.9";

                case 340:
                    return "3";

                case 256:
                    return "2.2";

                case 224:
                    return "1.9";

                case 192:
                    return "1.6";

                case 155:
                    return "1.3";

                case 128:
                    return "1";

                case 112:
                    return "0.9";

                case 96:
                    return "0.75";

                case 80:
                    return "0.6";

                case 64:
                    return "0.45";

                case 48:
                    return "0.3";

                case 32:
                    return "0.2";

                case 16:
                    return "0.1";
            }

            throw new Exception("Unknown VBR bitrate.");
        }

        /// <summary>
        /// Convert video quality index to H264 constant rate factor.
        /// </summary>
        /// <param name="quality">The quality index.</param>
        /// <returns>Returns the H264 constant rate factor.</returns>
        /// The range of the quantizer scale is 0-63: lower values mean better quality.
        /// https://trac.ffmpeg.org/wiki/Encode/VP9
        private int WebmQualityToCRF(int quality)
        {
            return 63 - quality;
        }
    }
}
