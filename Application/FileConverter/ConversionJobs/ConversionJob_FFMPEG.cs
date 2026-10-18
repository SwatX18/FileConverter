// <copyright file="ConversionJob_FFMPEG.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using CommunityToolkit.Mvvm.DependencyInjection;
    using FileConverter.Controls;
    using FileConverter.Hardware;
    using FileConverter.Media;
    using FileConverter.Services;

    public partial class ConversionJob_FFMPEG : ConversionJob
    {
        private readonly Regex durationRegex = new Regex(@"Duration:\s*([0-9][0-9]):([0-9][0-9]):([0-9][0-9])\.([0-9][0-9]),.*bitrate:\s*([0-9]+) kb\/s");
        private readonly Regex progressRegex = new Regex(@"size=\s*([0-9]+).*time=([0-9][0-9]):([0-9][0-9]):([0-9][0-9]).([0-9][0-9])\s+bitrate=\s*([0-9]+.[0-9])");

        private TimeSpan fileDuration;
        private TimeSpan actualConvertedDuration;

        // Duration written so far by the running attempt (ffmpeg -progress "out_time_us"), null until reported.
        private TimeSpan? attemptOutputDuration;

        private ProcessStartInfo ffmpegProcessStartInfo;

        private readonly List<FFMpegPass> ffmpegArgumentStringByPass = new List<FFMpegPass>();

        private string attemptError;

        private MediaInfo sourceInfo;
        private bool sourceProbed;

        private static readonly TimeSpan MaximumRemuxDurationDifference = TimeSpan.FromSeconds(0.5);

        ISettingsService settingsService = Ioc.Default.GetRequiredService<ISettingsService>();

        public ConversionJob_FFMPEG() : base()
        {
        }

        public ConversionJob_FFMPEG(ConversionPreset conversionPreset, string inputFilePath) : base(conversionPreset, inputFilePath)
        {
        }

        public static VideoEncodingSpeed[] VideoEncodingSpeeds => new[]
           {
               VideoEncodingSpeed.UltraFast,
               VideoEncodingSpeed.SuperFast,
               VideoEncodingSpeed.VeryFast,
               VideoEncodingSpeed.Faster,
               VideoEncodingSpeed.Fast,
               VideoEncodingSpeed.Medium,
               VideoEncodingSpeed.Slow,
               VideoEncodingSpeed.Slower,
               VideoEncodingSpeed.VerySlow,
           };

        /// <summary>
        /// Gets the target resolutions offered by the video presets (preset setting <c>VideoResolution</c>):
        /// 0 uses the scale factor, otherwise the shorter side of the video is resized to that size.
        /// </summary>
        public static int[] VideoResolutions => new[] { 0, 2160, 1440, 1080, 720, 480, 360 };

        /// <summary>
        /// Gets the video codecs offered by the Mp4 / Mkv presets. AV1 is only offered when a hardware AV1 encoder is
        /// detected: the CPU fallback (libaom) is slower than real time.
        /// </summary>
        public static VideoCodec[] VideoCodecs
        {
            get
            {
                IHardwareEncoderDetector detector = Ioc.Default.GetService<IHardwareEncoderDetector>();
                bool hardwareAv1 = detector?.GetPreferredEncoder(VideoCodec.Av1) != null;
                return hardwareAv1 ? new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 } : new[] { VideoCodec.H264, VideoCodec.Hevc };
            }
        }

        /// <summary>
        /// Gets the encoding path of the last successful pass (see <see cref="EncodingPaths"/>), for diagnostics.
        /// </summary>
        public string EncodingPath
        {
            get;
            private set;
        }

        protected virtual string FfmpegPath
        {
            get
            {
                string applicationDirectory = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return System.IO.Path.Combine(applicationDirectory, "ffmpeg.exe");
            }
        }

        protected override void Initialize()
        {
            base.Initialize();

            if (this.ConversionPreset == null)
            {
                throw new Exception("The conversion preset must be valid.");
            }

            this.ffmpegProcessStartInfo = null;

            string ffmpegPath = this.FfmpegPath;
            if (!System.IO.File.Exists(ffmpegPath))
            {
                this.ConversionFailed(Properties.Resources.ErrorCantFindFFMPEG);
                Diagnostics.Debug.Log($"Can't find ffmpeg executable ({ffmpegPath}). Try to reinstall the application.");
                return;
            }

            this.ffmpegProcessStartInfo = new ProcessStartInfo(ffmpegPath)
            {
                CreateNoWindow = true, 
                UseShellExecute = false, 
                RedirectStandardOutput = true, 
                RedirectStandardError = true
            };

            this.FillFFMpegArgumentsList();
        }

        protected virtual void FillFFMpegArgumentsList()
        {
            const string baseArgs = "-n -progress pipe:1";

            bool customCommandEnabled = this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableFFMPEGCustomCommand);
            if (customCommandEnabled)
            {
                // Custom command override other settings.
                string customCommand = this.ConversionPreset.GetSettingsValue<string>(ConversionPreset.ConversionSettingKeys.FFMPEGCustomCommand) ?? string.Empty;

                string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {customCommand} \"{this.OutputFilePath}\"";
                this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));

                return;
            }

            // This option are necessary to be able to read metadata on Windows. src: http://jonhall.info/how_to/create_id3_tags_using_ffmpeg
            const string MP3MetadataArgs = "-id3v2_version 3 -write_id3v1 1";

            // AAC have no standard tag system, use ApeV2 (that are compatible). src: http://eolindel.free.fr/foobar/tags.shtml
            const string AACMetadataArgs = "-write_apetag 1";

            switch (this.ConversionPreset.OutputType)
            {
                case OutputType.Aac:
                    {
                        string channelArgs = ConversionJob_FFMPEG.ComputeAudioChannelArgs(this.ConversionPreset);

                        // https://trac.ffmpeg.org/wiki/Encode/AAC
                        int audioEncodingBitrate = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);
                        string encoderArgs = $"-c:a aac -q:a {this.AACBitrateToQualityIndex(audioEncodingBitrate)} {channelArgs} {AACMetadataArgs}";

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Avi:
                    {
                        // https://trac.ffmpeg.org/wiki/Encode/MPEG-4
                        int videoEncodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
                        int audioEncodingBitrate = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);

                        string transformArgs = ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset);

                        string audioArgs = "-an";
                        if (this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableAudio))
                        {
                            audioArgs = $"-c:a libmp3lame -qscale:a {this.MP3VBRBitrateToQualityIndex(audioEncodingBitrate)}";
                        }

                        // Compute final arguments.
                        string videoFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", transformArgs);
                        string encoderArgs = $"-c:v mpeg4 -vtag xvid -qscale:v {this.MPEG4QualityToQualityIndex(videoEncodingQuality)} {audioArgs} {videoFilteringArgs} {MP3MetadataArgs}";
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Flac:
                    {
                        string channelArgs = ConversionJob_FFMPEG.ComputeAudioChannelArgs(this.ConversionPreset);

                        // http://taer-naguur.blogspot.fr/2013/11/flac-audio-encoding-with-ffmpeg.html
                        string encoderArgs = $"-compression_level 12 {channelArgs}";
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Gif:
                    {
                        // http://blog.pkh.me/p/21-high-quality-gif-with-ffmpeg.html
                        string fileName = Path.GetFileName(this.InputFilePath);
                        string tempPath = Path.GetTempPath();
                        string paletteFilePath = PathHelpers.GenerateUniquePath(tempPath + fileName + " - palette.png");

                        string transformArgs = ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset);

                        // fps.
                        int framesPerSecond = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoFramesPerSecond);
                        if (!string.IsNullOrEmpty(transformArgs))
                        {
                            transformArgs += ",";
                        }

                        transformArgs += $"fps={framesPerSecond}";

                        // Generate palette.
                        string encoderArgs = $"-vf \"{transformArgs},palettegen\"";
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{paletteFilePath}\"";
                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass("Indexing colors", arguments, paletteFilePath));

                        // Create gif.
                        encoderArgs = $"-i \"{paletteFilePath}\" -lavfi \"{transformArgs},paletteuse\"";
                        arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";
                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Ico:
                    {
                        string encoderArgs = string.Empty;
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Jpg:
                    {
                        int encodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.ImageQuality);

                        float scaleFactor = this.ConversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.ImageScale);
                        string scaleArgs = string.Empty;
                        if (Math.Abs(scaleFactor - 1f) >= 0.005f)
                        {
                            scaleArgs = $"-vf scale=iw*{scaleFactor.ToString("#.##", CultureInfo.InvariantCulture)}:ih*{scaleFactor.ToString("#.##", CultureInfo.InvariantCulture)}";
                        }

                        string encoderArgs = $"-q:v {this.JPGQualityToQualityIndex(encodingQuality)} {scaleArgs}";

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Mp3:
                    {
                        string channelArgs = ConversionJob_FFMPEG.ComputeAudioChannelArgs(this.ConversionPreset);

                        string encoderArgs = string.Empty;
                        EncodingMode encodingMode = this.ConversionPreset.GetSettingsValue<EncodingMode>(ConversionPreset.ConversionSettingKeys.AudioEncodingMode);
                        int encodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);
                        switch (encodingMode)
                        {
                            case EncodingMode.Mp3VBR:
                                encoderArgs = $"-codec:a libmp3lame -q:a {this.MP3VBRBitrateToQualityIndex(encodingQuality)} {channelArgs} {MP3MetadataArgs}";
                                break;

                            case EncodingMode.Mp3CBR:
                                encoderArgs = $"-codec:a libmp3lame -b:a {encodingQuality}k {channelArgs} {MP3MetadataArgs}";
                                break;

                            default:
                                break;
                        }

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Mkv:
                case OutputType.Mp4:
                    {
                        // https://trac.ffmpeg.org/wiki/Encode/H.264
                        // https://trac.ffmpeg.org/wiki/Encode/AAC
                        int videoEncodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
                        VideoEncodingSpeed videoEncodingSpeed = this.ConversionPreset.GetSettingsValue<VideoEncodingSpeed>(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed);
                        int audioEncodingBitrate = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);

                        VideoCodec videoCodec = VideoCodec.H264;
                        string videoCodecSetting = this.ConversionPreset.GetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoCodec);
                        if (!string.IsNullOrEmpty(videoCodecSetting) && !Enum.TryParse(videoCodecSetting, out videoCodec))
                        {
                            Diagnostics.Debug.Log($"Unknown video codec '{videoCodecSetting}', using H.264.");
                            videoCodec = VideoCodec.H264;
                        }

                        string audioArgs = "-an";
                        if (this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableAudio))
                        {
                            audioArgs = $"-c:a aac -qscale:a {this.AACBitrateToQualityIndex(audioEncodingBitrate)}";
                        }

                        // HEVC / AV1 keep 10-bit sources (HDR phone videos, camera footage) in 10-bit; H.264 is always 8-bit.
                        bool tenBit = false;
                        if (videoCodec != VideoCodec.H264)
                        {
                            MediaStream sourceVideo = this.GetSourceInfo()?.PrimaryVideo;
                            tenBit = sourceVideo != null && sourceVideo.BitsPerComponent > 8;
                            Diagnostics.Debug.Log($"Source video: {sourceVideo?.ToString() ?? "unknown"}, output {(tenBit ? "10-bit" : "8-bit")} 4:2:0.");
                        }

                        // HEVC in MP4 needs the hvc1 tag to play on Windows and Apple devices.
                        string containerArgs = videoCodec == VideoCodec.Hevc && this.ConversionPreset.OutputType == OutputType.Mp4 ? "-tag:v hvc1" : string.Empty;

                        List<FFMpegAttempt> attempts = this.BuildVideoEncodeAttempts(
                            videoCodec,
                            videoEncodingQuality,
                            videoEncodingSpeed,
                            tenBit,
                            (inputArgs, encoderArgs, videoFilteringArgs) =>
                                $"{baseArgs} {inputArgs} -i \"{this.InputFilePath}\" {encoderArgs} {containerArgs} {audioArgs} {videoFilteringArgs} \"{this.OutputFilePath}\"");

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass("Conversion", attempts, string.Empty));
                    }

                    break;

                case OutputType.Ogg:
                    {
                        string channelArgs = ConversionJob_FFMPEG.ComputeAudioChannelArgs(this.ConversionPreset);

                        int encodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);
                        string encoderArgs = $"-vn -codec:a libvorbis -qscale:a {this.OGGVBRBitrateToQualityIndex(encodingQuality)} {channelArgs}";
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Ogv:
                    {
                        // https://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
                        int videoEncodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
                        int audioEncodingBitrate = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);

                        string transformArgs = ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset);
                        string videoFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", transformArgs);

                        string audioArgs = "-an";
                        if (this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableAudio))
                        {
                            audioArgs = $"-codec:a libvorbis -qscale:a {this.OGGVBRBitrateToQualityIndex(audioEncodingBitrate)}";
                        }

                        string encoderArgs = $"-codec:v libtheora -qscale:v {this.OGVTheoraQualityToQualityIndex(videoEncodingQuality)} {audioArgs} {videoFilteringArgs}";

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Png:
                    {
                        float scaleFactor = this.ConversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.ImageScale);
                        string scaleArgs = string.Empty;
                        if (Math.Abs(scaleFactor - 1f) >= 0.005f)
                        {
                            scaleArgs = $"-vf scale=iw*{scaleFactor.ToString("#.##", CultureInfo.InvariantCulture)}:ih*{scaleFactor.ToString("#.##", CultureInfo.InvariantCulture)}";
                        }

                        // http://www.howtogeek.com/203979/is-the-png-format-lossless-since-it-has-a-compression-parameter/
                        string encoderArgs = $"-compression_level 100 {scaleArgs}";

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Wav:
                    {
                        string channelArgs = ConversionJob_FFMPEG.ComputeAudioChannelArgs(this.ConversionPreset);

                        EncodingMode encodingMode = this.ConversionPreset.GetSettingsValue<EncodingMode>(ConversionPreset.ConversionSettingKeys.AudioEncodingMode);
                        string encoderArgs = $"-acodec {this.WAVEncodingToCodecArgument(encodingMode)} {channelArgs}";
                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                case OutputType.Webm:
                    {
                        // https://trac.ffmpeg.org/wiki/Encode/VP9
                        int videoEncodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
                        int audioEncodingQuality = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);

                        string encodingArgs = string.Empty;
                        if (videoEncodingQuality == 63)
                        {
                            // Replace maximum quality settings by lossless compression.
                            encodingArgs = $"-lossless 1";
                        }
                        else
                        {
                            encodingArgs = $"-crf {this.WebmQualityToCRF(videoEncodingQuality)} -b:v 0";
                        }

                        string transformArgs = ConversionJob_FFMPEG.ComputeTransformArgs(this.ConversionPreset);
                        string videoFilteringArgs = ConversionJob_FFMPEG.Encapsulate("-vf", transformArgs);

                        string audioArgs = "-an";
                        if (this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableAudio))
                        {
                            audioArgs = $"-c:a libvorbis -qscale:a {this.OGGVBRBitrateToQualityIndex(audioEncodingQuality)}";
                        }

                        string encoderArgs = $"-c:v libvpx-vp9 {encodingArgs} {audioArgs} {videoFilteringArgs}";

                        string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {encoderArgs} \"{this.OutputFilePath}\"";

                        this.ffmpegArgumentStringByPass.Add(new FFMpegPass(arguments));
                    }

                    break;

                default:
                    throw new NotImplementedException("Converter not implemented for output file type " +
                                                      this.ConversionPreset.OutputType);
            }

            if (this.ffmpegArgumentStringByPass.Count == 0)
            {
                throw new Exception("No ffmpeg arguments generated.");
            }

            this.AddRemuxAttempt(baseArgs, MP3MetadataArgs, AACMetadataArgs);

            for (int index = 0; index < this.ffmpegArgumentStringByPass.Count; index++)
            {
                FFMpegPass pass = this.ffmpegArgumentStringByPass[index];
                if (pass.Attempts.Count == 0 || pass.Attempts.Any(attempt => string.IsNullOrEmpty(attempt.Arguments)))
                {
                    throw new Exception("Invalid ffmpeg process arguments.");
                }
            }
        }

        /// <summary>
        /// When the source streams can be copied instead of re-encoded (see <see cref="RemuxPlanner"/>), tries that first:
        /// the conversion's attempts become the fallback if the copy fails or produces a file of the wrong duration.
        /// </summary>
        private void AddRemuxAttempt(string baseArgs, string mp3MetadataArgs, string aacMetadataArgs)
        {
            OutputType outputType = this.ConversionPreset.OutputType;
            if (!this.settingsService.Settings.CopyStreamsWhenPossible || this.ffmpegArgumentStringByPass.Count != 1 || !RemuxCompatibility.Default.SupportsRemux(outputType))
            {
                return;
            }

            // Don't pay an ffprobe launch (~0.3 s) for files that can't be remuxed anyway.
            string extension = Path.GetExtension(this.InputFilePath);
            if (!RemuxCompatibility.Default.IsRemuxSource(extension))
            {
                Diagnostics.Debug.Log($"Remux: no, {extension.ToLowerInvariant()} files are not remuxed.");
                return;
            }

            MediaInfo source = this.GetSourceInfo();
            RemuxPlan plan = RemuxPlanner.Default.Plan(this.ConversionPreset, this.InputFilePath, source, out string reason);
            if (plan == null)
            {
                Diagnostics.Debug.Log($"Remux: no, {reason}.");
                return;
            }

            Diagnostics.Debug.Log($"Remux plan: {plan}.");

            int audioBitrate = this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);
            List<string> streamArgs = new List<string>();
            for (int outputIndex = 0; outputIndex < plan.Streams.Count; outputIndex++)
            {
                RemuxStreamDecision decision = plan.Streams[outputIndex];
                streamArgs.Add($"-map 0:{decision.Stream.Index}");
                switch (decision.Action)
                {
                    case RemuxStreamAction.Copy:
                        streamArgs.Add($"-c:{outputIndex} copy");
                        break;

                    case RemuxStreamAction.Convert:
                        streamArgs.Add($"-c:{outputIndex} {decision.TargetCodec}");
                        break;

                    case RemuxStreamAction.Encode:
                        // Same audio encoders as the full conversion of these output types.
                        streamArgs.Add(outputType == OutputType.Mp4 || outputType == OutputType.Mkv ?
                            $"-c:{outputIndex} aac -qscale:{outputIndex} {this.AACBitrateToQualityIndex(audioBitrate)}" :
                            $"-c:{outputIndex} libvorbis -qscale:{outputIndex} {this.OGGVBRBitrateToQualityIndex(audioBitrate)}");
                        break;
                }
            }

            string containerArgs = string.Empty;
            switch (outputType)
            {
                case OutputType.Mp4:
                    // HEVC in MP4 needs the hvc1 tag to play on Windows and Apple devices.
                    bool hevc = plan.Streams.Any(decision => decision.Stream.IsVideo && decision.Stream.CodecName == "hevc");
                    containerArgs = hevc ? "-tag:v hvc1 -movflags +faststart" : "-movflags +faststart";
                    break;

                case OutputType.Mp3:
                    containerArgs = mp3MetadataArgs;
                    break;

                case OutputType.Aac:
                    containerArgs = aacMetadataArgs;
                    break;
            }

            string arguments = $"{baseArgs} -i \"{this.InputFilePath}\" {string.Join(" ", streamArgs)} {containerArgs} \"{this.OutputFilePath}\"";
            FFMpegAttempt remux = new FFMpegAttempt(EncodingPaths.Remux, arguments, usesGpu: false)
            {
                Label = Properties.Resources.ConversionStateRemux,
                Validate = () => this.ValidateRemuxOutput(source, plan),
            };

            FFMpegPass pass = this.ffmpegArgumentStringByPass[0];
            this.ffmpegArgumentStringByPass[0] = new FFMpegPass(pass.Name, new[] { remux }.Concat(pass.Attempts).ToList(), pass.FileToDelete);
        }

        /// <summary>
        /// Checks that a remuxed file is as long as the source streams it contains (copied streams with broken
        /// timestamps can produce truncated or stretched files without an ffmpeg error).
        /// </summary>
        /// <remarks>
        /// A single-stream output (audio extraction) is checked with the duration ffmpeg reports for what it muxed
        /// (-progress out_time_us): an ffprobe of the output would cost another process launch (~0.3 s, as much as the
        /// remux of a short track), and ADTS (.aac) files store no duration (ffprobe only estimates it from the bitrate).
        /// With several streams ffmpeg's figure is not the file's duration (Sintel with 10 subtitle tracks: 629.8 s
        /// reported for 888 s written), so the output is probed.
        /// </remarks>
        /// <returns>Null if the output is valid, the problem otherwise.</returns>
        private string ValidateRemuxOutput(MediaInfo source, RemuxPlan plan)
        {
            if (!File.Exists(this.OutputFilePath))
            {
                return "the remuxed file was not written";
            }

            // Compare with the written streams, not the source container: an audio track extracted from a video can be
            // shorter than the video. Fall back to the container duration if a stream's duration is unknown.
            List<MediaStream> mediaStreams = plan.Streams.Select(decision => decision.Stream).Where(stream => (stream.IsVideo || stream.IsAudio) && !stream.IsAttachedPicture).ToList();
            TimeSpan? expected = mediaStreams.Count > 0 && mediaStreams.All(stream => stream.Duration.HasValue) ? mediaStreams.Max(stream => stream.Duration.Value) : source.Duration;

            if (!expected.HasValue)
            {
                return null;
            }

            TimeSpan? actual;
            if (plan.Streams.Count == 1)
            {
                actual = this.attemptOutputDuration;
            }
            else
            {
                MediaInfo output = MediaProbe.Default.TryProbe(this.OutputFilePath);
                if (output == null)
                {
                    return "the remuxed file can't be read";
                }

                actual = output.Duration;
            }

            if (!actual.HasValue)
            {
                return "the remuxed file has no duration";
            }

            if (Math.Abs((actual.Value - expected.Value).TotalSeconds) > MaximumRemuxDurationDifference.TotalSeconds)
            {
                return string.Format(CultureInfo.InvariantCulture, "the remuxed file lasts {0:0.00} s instead of {1:0.00} s", actual.Value.TotalSeconds, expected.Value.TotalSeconds);
            }

            return null;
        }

        /// <summary>
        /// Probes the input file once per job (null if ffprobe is missing or fails).
        /// </summary>
        private MediaInfo GetSourceInfo()
        {
            if (!this.sourceProbed)
            {
                this.sourceInfo = MediaProbe.Default.TryProbe(this.InputFilePath);
                this.sourceProbed = true;
                if (this.sourceInfo != null)
                {
                    string duration = this.sourceInfo.Duration?.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) ?? "?";
                    Diagnostics.Debug.Log($"Source: {this.sourceInfo.FormatName}, {duration} s, streams: {string.Join(", ", this.sourceInfo.Streams)}.");
                }
            }

            return this.sourceInfo;
        }

        protected override void Convert()
        {
            if (this.ConversionPreset == null)
            {
                throw new Exception("The conversion preset must be valid.");
            }

            for (int index = 0; index < this.ffmpegArgumentStringByPass.Count; index++)
            {
                FFMpegPass currentPass = this.ffmpegArgumentStringByPass[index];

                if (!this.ExecutePass(currentPass))
                {
                    break;
                }
            }

            Diagnostics.Debug.Log(string.Empty);

            // Clean intermediate files.
            for (int index = 0; index < this.ffmpegArgumentStringByPass.Count; index++)
            {
                FFMpegPass currentPass = this.ffmpegArgumentStringByPass[index];

                if (string.IsNullOrEmpty(currentPass.FileToDelete))
                {
                    continue;
                }

                Diagnostics.Debug.Log($"Delete intermediate file {currentPass.FileToDelete}.");

                File.Delete(currentPass.FileToDelete);
            }
        }

        /// <summary>
        /// Runs the attempts of a pass in order until one succeeds (e.g. GPU pipeline, then GPU encode with CPU filters,
        /// then CPU). The job only fails if the last attempt fails.
        /// </summary>
        /// <returns>True if the pass succeeded.</returns>
        private bool ExecutePass(FFMpegPass pass)
        {
            for (int attemptIndex = 0; attemptIndex < pass.Attempts.Count; attemptIndex++)
            {
                FFMpegAttempt attempt = pass.Attempts[attemptIndex];
                bool isLastAttempt = attemptIndex == pass.Attempts.Count - 1;

                if (pass.Attempts.Count > 1)
                {
                    Diagnostics.Debug.Log($"Encoding path: {attempt.Path}.");
                }

                this.UserState = attempt.Label ?? pass.Name;

                string error = this.ExecuteAttempt(attempt);
                if (error == null && attempt.Validate != null && !this.CancelIsRequested)
                {
                    error = attempt.Validate();
                    if (error != null)
                    {
                        Diagnostics.Debug.Log($"Output validation failed: {error}.");
                    }
                }

                if (error == null)
                {
                    this.EncodingPath = attempt.Path;
                    if (attemptIndex > 0)
                    {
                        Diagnostics.Debug.Log($"Conversion succeeded with fallback encoding path '{attempt.Path}'.");
                    }

                    return true;
                }

                if (this.CancelIsRequested)
                {
                    return false;
                }

                if (isLastAttempt)
                {
                    this.ConversionFailed(error);
                    return false;
                }

                FFMpegAttempt nextAttempt = pass.Attempts[attemptIndex + 1];
                Diagnostics.Debug.Log($"Encoding path '{attempt.Path}' failed ({error}). Falling back to '{nextAttempt.Path}'.");

                // The next attempt writes the same output, and ffmpeg is started with -n (no overwrite).
                try
                {
                    if (File.Exists(this.OutputFilePath))
                    {
                        File.Delete(this.OutputFilePath);
                    }
                }
                catch (Exception exception)
                {
                    this.ConversionFailed(error);
                    Diagnostics.Debug.Log($"Can't delete the partial output before falling back: {exception.Message}");
                    return false;
                }

                this.Progress = 0f;
            }

            return false;
        }

        /// <summary>
        /// Runs ffmpeg once.
        /// </summary>
        /// <returns>Null on success, the error message otherwise.</returns>
        private string ExecuteAttempt(FFMpegAttempt attempt)
        {
            IDisposable gpuSession = null;
            if (attempt.UsesGpu)
            {
                GpuSessionLimiter.Configure(this.settingsService.Settings.MaximumNumberOfSimultaneousGpuConversions);
                gpuSession = GpuSessionLimiter.Acquire(() => this.CancelIsRequested);
                if (gpuSession == null)
                {
                    return Properties.Resources.ErrorCanceled;
                }
            }

            using (gpuSession)
            {
                this.ffmpegProcessStartInfo.Arguments = attempt.Arguments;
                this.attemptError = null;
                this.attemptOutputDuration = null;

                Diagnostics.Debug.Log($"Execute command: {this.ffmpegProcessStartInfo.FileName} {this.ffmpegProcessStartInfo.Arguments}.");
                Diagnostics.Debug.Log(string.Empty);

                try
                {
                    using (Process exeProcess = Process.Start(this.ffmpegProcessStartInfo))
                    {
                        // ffmpeg writes -progress key/value blocks on stdout. Drain it, otherwise the pipe buffer fills
                        // up after a few seconds of encoding and ffmpeg blocks forever (progress is parsed from stderr).
                        exeProcess.OutputDataReceived += (sender, args) => this.ParseFFMPEGProgress(args.Data);
                        exeProcess.BeginOutputReadLine();

                        using (StreamReader reader = exeProcess.StandardError)
                        {
                            while (!reader.EndOfStream)
                            {
                                if (this.CancelIsRequested && !exeProcess.HasExited)
                                {
                                    exeProcess.Kill();
                                }

                                string result = reader.ReadLine();

                                this.ParseFFMPEGOutput(result);

                                Diagnostics.Debug.Log($"ffmpeg output: {result}");
                            }
                        }

                        exeProcess.WaitForExit();

                        if (exeProcess.ExitCode != 0 && !this.CancelIsRequested)
                        {
                            Diagnostics.Debug.Log($"ffmpeg exited with code {exeProcess.ExitCode}.");
                            if (this.attemptError == null)
                            {
                                this.attemptError = string.Format(Properties.Resources.ErrorFFMPEGExitCode, exeProcess.ExitCode);
                            }
                        }
                    }
                }
                catch
                {
                    this.ConversionFailed(Properties.Resources.ErrorFailedToLaunchFFMPEG);
                    throw;
                }

                return this.attemptError;
            }
        }

        /// <summary>
        /// Parses ffmpeg's -progress key=value lines (stdout).
        /// </summary>
        private void ParseFFMPEGProgress(string line)
        {
            const string OutTimeKey = "out_time_us=";
            if (line != null && line.StartsWith(OutTimeKey, StringComparison.Ordinal) &&
                long.TryParse(line.Substring(OutTimeKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds) && microseconds >= 0)
            {
                this.attemptOutputDuration = TimeSpan.FromTicks(microseconds * 10);
            }
        }

        private void ParseFFMPEGOutput(string input)
        {
            Match match = this.durationRegex.Match(input);
            if (match.Success && match.Groups.Count >= 6)
            {
                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                int seconds = int.Parse(match.Groups[3].Value);
                int milliseconds = int.Parse(match.Groups[4].Value) * 10;
                float bitrate = float.Parse(match.Groups[5].Value);
                this.fileDuration = new TimeSpan(0, hours, minutes, seconds, milliseconds);
                return;
            }

            if (this.fileDuration.Ticks > 0)
            {
                match = this.progressRegex.Match(input);
                if (match.Success && match.Groups.Count >= 7)
                {
                    int size = int.Parse(match.Groups[1].Value);
                    int hours = int.Parse(match.Groups[2].Value);
                    int minutes = int.Parse(match.Groups[3].Value);
                    int seconds = int.Parse(match.Groups[4].Value);
                    int milliseconds = int.Parse(match.Groups[5].Value) * 10;
                    float bitrate = 0f;
                    float.TryParse(match.Groups[6].Value, out bitrate);

                    this.actualConvertedDuration = new TimeSpan(0, hours, minutes, seconds, milliseconds);

                    this.Progress = this.actualConvertedDuration.Ticks / (float)this.fileDuration.Ticks;
                    return;
                }
            }

            // Remove file names from log to avoid false negative when some words like 'Error' are in file name (github issue #247).
            string inputWithoutFileNames = input.Replace(this.InputFilePath, string.Empty).Replace(this.OutputFilePath, string.Empty);

            if (inputWithoutFileNames.Contains("Exiting.") || inputWithoutFileNames.Contains("Error") || inputWithoutFileNames.Contains("Unsupported dimensions") || inputWithoutFileNames.Contains("No such file or directory"))
            {
                if (inputWithoutFileNames.StartsWith("Error while decoding stream") && inputWithoutFileNames.EndsWith("Invalid data found when processing input"))
                {
                    // It is normal for a transport stream to start with a broken frame.
                    // https://trac.ffmpeg.org/ticket/1622
                }
                else if (inputWithoutFileNames.EndsWith("Error parsing Opus packet header."))
                {
                    // ffmpeg 8 logs this for Opus audio in Matroska / WebM files that decode correctly. A real
                    // failure still makes ffmpeg exit with an error code.
                }
                else if (this.attemptError == null)
                {
                    // Keep the first error: the attempt fails (or falls back) once ffmpeg has exited.
                    this.attemptError = input;
                }
            }
        }

        private class FFMpegPass
        {
            public FFMpegPass(string name, IReadOnlyList<FFMpegAttempt> attempts, string fileToDelete)
            {
                this.Name = name;
                this.Attempts = attempts;
                this.FileToDelete = fileToDelete;
            }

            public FFMpegPass(string name, string arguments, string fileToDelete)
                : this(name, new[] { new FFMpegAttempt(EncodingPaths.Default, arguments, usesGpu: false) }, fileToDelete)
            {
            }

            public FFMpegPass(string arguments)
                : this("Conversion", arguments, string.Empty)
            {
            }

            public string Name { get; }

            public IReadOnlyList<FFMpegAttempt> Attempts { get; }

            public string FileToDelete { get; }
        }

        private class FFMpegAttempt
        {
            public FFMpegAttempt(string path, string arguments, bool usesGpu)
            {
                this.Path = path;
                this.Arguments = arguments;
                this.UsesGpu = usesGpu;
            }

            /// <summary>
            /// Diagnostics label of the encoding path (see <see cref="EncodingPaths"/>).
            /// </summary>
            public string Path { get; }

            public string Arguments { get; }

            public bool UsesGpu { get; }

            /// <summary>
            /// Gets the job state shown while the attempt runs (null: the pass name).
            /// </summary>
            public string Label { get; set; }

            /// <summary>
            /// Gets the check run on the output once ffmpeg succeeded: null if the output is valid, the problem otherwise
            /// (the attempt then fails and the next one runs).
            /// </summary>
            public Func<string> Validate { get; set; }
        }
    }
}
