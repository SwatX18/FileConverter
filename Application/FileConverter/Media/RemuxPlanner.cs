// <copyright file="RemuxPlanner.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Media
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using FileConverter.Hardware;

    public enum RemuxStreamAction
    {
        /// <summary>Stream copy (<c>-c copy</c>).</summary>
        Copy,

        /// <summary>Re-encoded with the preset's encoder (audio streams next to a copied video).</summary>
        Encode,

        /// <summary>Text subtitles converted to the codec the container accepts.</summary>
        Convert,
    }

    /// <summary>
    /// Decides, per stream, whether a conversion can copy the source streams instead of re-encoding them.
    /// </summary>
    /// <remarks>
    /// A video output is remuxed only if its main video stream can be copied: the container accepts the codec and the
    /// preset asks for no change (no resize, rotation, lower quality or other codec). Its audio streams are then copied
    /// or re-encoded one by one. An audio output is remuxed only if its audio stream can be copied.
    /// Converting a file to its own format (mp4 to Mp4, mp3 to Mp3...) is always re-encoded: that is the only reason to do it.
    /// </remarks>
    public class RemuxPlanner
    {
        /// <summary>
        /// A lossy audio stream is copied if its bitrate is at most this factor above the preset's target bitrate:
        /// re-encoding is only worth its quality loss (generation loss) if it saves at least a third of the size.
        /// </summary>
        public const double AudioBitrateTolerance = 1.5;

        private static readonly Dictionary<string, OutputType> OutputTypeByExtension = new Dictionary<string, OutputType>(StringComparer.OrdinalIgnoreCase)
        {
            { "mp4", OutputType.Mp4 },
            { "m4v", OutputType.Mp4 },
            { "mkv", OutputType.Mkv },
            { "webm", OutputType.Webm },
            { "ogv", OutputType.Ogv },
            { "aac", OutputType.Aac },
            { "mp3", OutputType.Mp3 },
            { "ogg", OutputType.Ogg },
            { "oga", OutputType.Ogg },
            { "flac", OutputType.Flac },
        };

        private readonly RemuxCompatibility compatibility;

        public RemuxPlanner(RemuxCompatibility compatibility)
        {
            this.compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        }

        public static RemuxPlanner Default => new RemuxPlanner(RemuxCompatibility.Default);

        public static bool IsVideoOutput(OutputType outputType)
        {
            return outputType == OutputType.Mp4 || outputType == OutputType.Mkv || outputType == OutputType.Webm || outputType == OutputType.Ogv;
        }

        /// <summary>
        /// Plans the conversion of <paramref name="inputFilePath"/> (probed as <paramref name="source"/>) with <paramref name="preset"/>.
        /// </summary>
        /// <param name="reason">Why the file is not remuxed (when null is returned), for diagnostics.</param>
        /// <returns>The streams to write and how, or null if the conversion must be a full re-encode.</returns>
        public RemuxPlan Plan(ConversionPreset preset, string inputFilePath, MediaInfo source, out string reason)
        {
            reason = this.GetBlockerBeforeProbe(preset, inputFilePath);
            if (reason != null)
            {
                return null;
            }

            if (source == null)
            {
                reason = "the source could not be probed";
                return null;
            }

            OutputType outputType = preset.OutputType;
            return RemuxPlanner.IsVideoOutput(outputType) ?
                this.PlanVideoOutput(preset, GetDefaults(outputType), source, out reason) :
                this.PlanAudioOutput(preset, source, out reason);
        }

        /// <summary>
        /// Checks what can be decided without probing the input (output type, input extension, preset settings), so
        /// that jobs which can't be remuxed don't pay an ffprobe launch (~0.3 s).
        /// </summary>
        /// <returns>Why the file can't be remuxed, or null if the input must be probed to decide.</returns>
        public string GetBlockerBeforeProbe(ConversionPreset preset, string inputFilePath)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            OutputType outputType = preset.OutputType;
            if (!this.compatibility.SupportsRemux(outputType))
            {
                return $"{outputType} outputs are not remuxed";
            }

            string extension = Path.GetExtension(inputFilePath ?? string.Empty).TrimStart('.');
            if (!this.compatibility.IsRemuxSource(extension))
            {
                return $".{extension.ToLowerInvariant()} files are not remuxed";
            }

            if (OutputTypeByExtension.TryGetValue(extension, out OutputType sameFormat) && sameFormat == outputType)
            {
                return $"a .{extension.ToLowerInvariant()} file converted to {outputType} is re-encoded";
            }

            return RemuxPlanner.IsVideoOutput(outputType) ? GetVideoPresetBlocker(preset, GetDefaults(outputType)) : null;
        }

        /// <summary>
        /// Gets a new preset of the output type: its settings tell what "no lower quality than default" means.
        /// </summary>
        private static ConversionPreset GetDefaults(OutputType outputType)
        {
            return new ConversionPreset(string.Empty, outputType);
        }

        /// <returns>Why the preset settings alone prevent copying the video (resize, rotation, lower quality), or null.</returns>
        private static string GetVideoPresetBlocker(ConversionPreset preset, ConversionPreset defaults)
        {
            float scale = preset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.VideoScale);
            if (Math.Abs(scale - 1f) >= 0.005f)
            {
                return "the preset resizes the video";
            }

            float rotation = preset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.VideoRotation);
            if (Math.Abs(rotation) >= 0.05f)
            {
                return "the preset rotates the video";
            }

            int quality = preset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
            int defaultQuality = defaults.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoQuality);
            if (quality < defaultQuality)
            {
                return $"the preset asks for a lower quality than the default ({quality} < {defaultQuality})";
            }

            return null;
        }

        private RemuxPlan PlanVideoOutput(ConversionPreset preset, ConversionPreset defaults, MediaInfo source, out string reason)
        {
            OutputType outputType = preset.OutputType;
            MediaStream video = source.PrimaryVideo;
            if (video == null)
            {
                reason = "the source has no video stream";
                return null;
            }

            reason = GetVideoPresetBlocker(preset, defaults) ?? this.GetVideoCopyBlocker(preset, video);
            if (reason != null)
            {
                return null;
            }

            bool enableAudio = preset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.EnableAudio);
            bool audioAtDefaultQuality = preset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate) >= defaults.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);

            List<RemuxStreamDecision> decisions = new List<RemuxStreamDecision>();
            foreach (MediaStream stream in source.Streams)
            {
                if (stream == video)
                {
                    decisions.Add(new RemuxStreamDecision(stream, RemuxStreamAction.Copy));
                }
                else if (stream.IsAudio)
                {
                    if (enableAudio)
                    {
                        bool copy = audioAtDefaultQuality && this.compatibility.CanCopyAudio(outputType, stream.CodecName);
                        decisions.Add(new RemuxStreamDecision(stream, copy ? RemuxStreamAction.Copy : RemuxStreamAction.Encode));
                    }
                }
                else if (stream.IsSubtitle)
                {
                    RemuxStreamDecision subtitle = this.PlanSubtitle(outputType, stream);
                    if (subtitle != null)
                    {
                        decisions.Add(subtitle);
                    }
                }
                else if (stream.CodecType == "attachment" && this.compatibility.CanCopyAttachments(outputType))
                {
                    decisions.Add(new RemuxStreamDecision(stream, RemuxStreamAction.Copy));
                }

                // Other video streams (cover art, secondary angles) and data streams (timecode, camera metadata) are dropped.
            }

            return new RemuxPlan(decisions);
        }

        private RemuxPlan PlanAudioOutput(ConversionPreset preset, MediaInfo source, out string reason)
        {
            OutputType outputType = preset.OutputType;
            MediaStream audio = source.AudioStreams.FirstOrDefault();
            if (audio == null)
            {
                reason = "the source has no audio stream";
                return null;
            }

            if (!this.compatibility.CanCopyAudio(outputType, audio.CodecName))
            {
                reason = $"{outputType} can't contain {audio.CodecName} audio";
                return null;
            }

            int channelCount = preset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioChannelCount);
            if (channelCount > 0 && channelCount != audio.Channels)
            {
                reason = $"the preset changes the channel count ({audio.Channels} to {channelCount})";
                return null;
            }

            // Flac is lossless: a flac stream is copied whatever its bitrate.
            if (outputType != OutputType.Flac)
            {
                // ffprobe reports no per-stream bitrate for some containers (e.g. Matroska): use the file's for an audio-only file.
                long? bitRate = audio.BitRate;
                if (!bitRate.HasValue && source.Streams.All(stream => stream.IsAudio || stream.IsAttachedPicture))
                {
                    bitRate = source.BitRate;
                }

                int targetKbps = preset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.AudioBitrate);
                if (!bitRate.HasValue)
                {
                    reason = "the source audio bitrate is unknown";
                    return null;
                }

                if (bitRate.Value > targetKbps * 1000 * AudioBitrateTolerance)
                {
                    reason = $"the source audio bitrate ({bitRate.Value / 1000} kb/s) is above the preset's ({targetKbps} kb/s)";
                    return null;
                }
            }

            List<RemuxStreamDecision> decisions = new List<RemuxStreamDecision> { new RemuxStreamDecision(audio, RemuxStreamAction.Copy) };
            MediaStream coverArt = source.Streams.FirstOrDefault(stream => stream.IsAttachedPicture);
            if (coverArt != null && this.compatibility.CanCopyCoverArt(outputType, coverArt.CodecName))
            {
                decisions.Add(new RemuxStreamDecision(coverArt, RemuxStreamAction.Copy));
            }

            reason = null;
            return new RemuxPlan(decisions);
        }

        /// <returns>Why the video stream can't be copied (codec, target resolution), or null if it can.</returns>
        private string GetVideoCopyBlocker(ConversionPreset preset, MediaStream video)
        {
            OutputType outputType = preset.OutputType;
            if (!this.compatibility.CanCopyVideo(outputType, video.CodecName))
            {
                return $"{outputType} can't contain {video.CodecName} video";
            }

            // Mp4 / Mkv presets choose a codec. H.264, the default, means "most compatible": any codec the container
            // accepts is kept. HEVC and AV1 are explicit requests: the source must already use that codec.
            if (outputType == OutputType.Mp4 || outputType == OutputType.Mkv)
            {
                string codecSetting = preset.GetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoCodec);
                if (!string.IsNullOrEmpty(codecSetting) && Enum.TryParse(codecSetting, out VideoCodec requestedCodec) && requestedCodec != VideoCodec.H264)
                {
                    string requestedName = requestedCodec == VideoCodec.Hevc ? "hevc" : "av1";
                    if (!string.Equals(video.CodecName, requestedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"the preset asks for {requestedCodec} and the source is {video.CodecName}";
                    }
                }
            }

            int resolution = preset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.VideoResolution);
            if (resolution > 0)
            {
                int? shorterSide = video.Width.HasValue && video.Height.HasValue ? Math.Min(video.Width.Value, video.Height.Value) : (int?)null;
                if (shorterSide != resolution)
                {
                    return $"the preset resizes the video to {resolution}p";
                }
            }

            return null;
        }

        private RemuxStreamDecision PlanSubtitle(OutputType outputType, MediaStream stream)
        {
            if (this.compatibility.CanCopySubtitle(outputType, stream.CodecName))
            {
                return new RemuxStreamDecision(stream, RemuxStreamAction.Copy);
            }

            string target = this.compatibility.GetTextSubtitleTarget(outputType);
            if (target != null && this.compatibility.IsTextSubtitle(stream.CodecName))
            {
                return new RemuxStreamDecision(stream, RemuxStreamAction.Convert, target);
            }

            // Bitmap subtitles (PGS, DVD) can't be converted to text: dropped.
            return null;
        }
    }

    /// <summary>
    /// The source streams written to the output, in order, and how.
    /// </summary>
    public sealed class RemuxPlan
    {
        public RemuxPlan(IReadOnlyList<RemuxStreamDecision> streams)
        {
            this.Streams = streams ?? throw new ArgumentNullException(nameof(streams));
        }

        public IReadOnlyList<RemuxStreamDecision> Streams { get; }

        /// <summary>
        /// Gets whether every written stream is copied (no stream re-encoded or converted).
        /// </summary>
        public bool IsFullCopy => this.Streams.All(stream => stream.Action == RemuxStreamAction.Copy);

        public override string ToString()
        {
            return string.Join(", ", this.Streams.Select(stream => stream.ToString()));
        }
    }

    public sealed class RemuxStreamDecision
    {
        public RemuxStreamDecision(MediaStream stream, RemuxStreamAction action, string targetCodec = null)
        {
            this.Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.Action = action;
            this.TargetCodec = targetCodec;
        }

        public MediaStream Stream { get; }

        public RemuxStreamAction Action { get; }

        /// <summary>
        /// Gets the codec a <see cref="RemuxStreamAction.Convert"/> stream is converted to.
        /// </summary>
        public string TargetCodec { get; }

        public override string ToString()
        {
            string action = this.Action == RemuxStreamAction.Convert ? $"convert to {this.TargetCodec}" : this.Action.ToString().ToLowerInvariant();
            return $"#{this.Stream.Index} {this.Stream.CodecType}:{this.Stream.CodecName} {action}";
        }
    }
}
