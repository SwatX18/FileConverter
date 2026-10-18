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

        private ProcessStartInfo ffmpegProcessStartInfo;

        private readonly List<FFMpegPass> ffmpegArgumentStringByPass = new List<FFMpegPass>();

        private string attemptError;

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
                            MediaStream sourceVideo = MediaProbe.Default.TryProbe(this.InputFilePath)?.PrimaryVideo;
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

            for (int index = 0; index < this.ffmpegArgumentStringByPass.Count; index++)
            {
                FFMpegPass pass = this.ffmpegArgumentStringByPass[index];
                if (pass.Attempts.Count == 0 || pass.Attempts.Any(attempt => string.IsNullOrEmpty(attempt.Arguments)))
                {
                    throw new Exception("Invalid ffmpeg process arguments.");
                }
            }
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

                this.UserState = currentPass.Name;

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

                string error = this.ExecuteAttempt(attempt);
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

                Diagnostics.Debug.Log($"Execute command: {this.ffmpegProcessStartInfo.FileName} {this.ffmpegProcessStartInfo.Arguments}.");
                Diagnostics.Debug.Log(string.Empty);

                try
                {
                    using (Process exeProcess = Process.Start(this.ffmpegProcessStartInfo))
                    {
                        // ffmpeg writes -progress key/value blocks on stdout. Drain it, otherwise the pipe buffer fills
                        // up after a few seconds of encoding and ffmpeg blocks forever (progress is parsed from stderr).
                        exeProcess.OutputDataReceived += (sender, args) => { };
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
        }
    }
}
