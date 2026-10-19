// <copyright file="RemuxPlannerTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Linq;

    using FileConverter.Media;

    using Xunit;

    /// <summary>
    /// Source codecs x output container x preset settings -> copy / encode / convert per stream, or a full re-encode.
    /// Uses the table embedded in the application (Media/remux-compat.json).
    /// </summary>
    public class RemuxPlannerTests
    {
        private static readonly RemuxPlanner Planner = RemuxPlanner.Default;

        public static TheoryData<string, OutputType, string, string> VideoCases => new TheoryData<string, OutputType, string, string>
        {
            // input file, output, source streams (type:codec[:kbps]), expected plan (or "re-encode")
            { "a.mkv", OutputType.Mp4, "v:h264 a:aac:128", "copy copy" },
            { "a.mkv", OutputType.Mp4, "v:hevc a:aac:128", "copy copy" },
            { "a.mkv", OutputType.Mp4, "v:av1 a:opus:96", "copy copy" },
            { "a.mov", OutputType.Mp4, "v:prores a:pcm_s16le", "re-encode" },
            { "a.mkv", OutputType.Mp4, "v:vp9 a:opus:96", "re-encode" },
            { "a.mkv", OutputType.Mp4, "v:h264 a:flac", "copy encode" },
            { "a.mkv", OutputType.Mp4, "v:h264 a:aac:128 a:dts", "copy copy encode" },
            { "a.mkv", OutputType.Mp4, "v:h264 a:aac:128 s:subrip", "copy copy convert:mov_text" },
            { "a.mkv", OutputType.Mp4, "v:h264 a:aac:128 s:hdmv_pgs_subtitle", "copy copy" },
            { "a.mkv", OutputType.Mp4, "v:h264 a:aac:128 t:ttf", "copy copy" },
            { "a.mp4", OutputType.Mkv, "v:h264 a:aac:128 s:mov_text", "copy copy convert:subrip" },
            { "a.mp4", OutputType.Mkv, "v:mpeg2video a:mp2", "copy copy" },
            { "a.mkv", OutputType.Webm, "v:vp9 a:opus:96 s:subrip", "copy copy convert:webvtt" },
            { "a.mkv", OutputType.Webm, "v:h264 a:aac:128", "re-encode" },
            { "a.mkv", OutputType.Webm, "v:av1 a:aac:128", "copy encode" },
            { "a.mkv", OutputType.Mp4, "a:aac:128", "re-encode" },
            { "a.mp4", OutputType.Mp4, "v:h264 a:aac:128", "re-encode" },
            { "a.mkv", OutputType.Mkv, "v:h264 a:aac:128", "re-encode" },
            { "a.wmv", OutputType.Mp4, "v:h264 a:aac:128", "re-encode" },
            { "a.mkv", OutputType.Avi, "v:h264 a:aac:128", "re-encode" },
        };

        public static TheoryData<string, OutputType, string, string> AudioCases => new TheoryData<string, OutputType, string, string>
        {
            { "a.m4a", OutputType.Aac, "a:aac:128", "copy" },
            { "a.m4a", OutputType.Aac, "a:aac:192", "copy" },
            { "a.m4a", OutputType.Aac, "a:aac:256", "re-encode" },
            { "a.mkv", OutputType.Mp3, "v:h264 a:mp3:192", "copy" },
            { "a.mp4", OutputType.Mp3, "a:mp3:192 v:mjpeg:cover", "copy copy" },
            { "a.mp4", OutputType.Aac, "a:aac:128 v:mjpeg:cover", "copy" },
            { "a.opus", OutputType.Ogg, "a:opus:96", "copy" },
            { "a.mka", OutputType.Mp3, "a:flac", "re-encode" },
            { "a.mp3", OutputType.Mp3, "a:mp3:128", "re-encode" },
            { "a.wav", OutputType.Mp3, "a:pcm_s16le", "re-encode" },
            { "a.mkv", OutputType.Flac, "a:flac", "re-encode" },
            { "a.mkv", OutputType.Aac, "v:h264", "re-encode" },
        };

        [Theory]
        [MemberData(nameof(VideoCases))]
        [MemberData(nameof(AudioCases))]
        public void PlansEachStream(string inputFile, OutputType outputType, string sourceStreams, string expectedPlan)
        {
            ConversionPreset preset = new ConversionPreset("test", outputType);

            RemuxPlan plan = Planner.Plan(preset, inputFile, Source(sourceStreams), out string reason);

            Assert.Equal(expectedPlan, Describe(plan));
            Assert.True(plan != null || !string.IsNullOrEmpty(reason), "a re-encode must give a reason");
        }

        [Theory]
        [InlineData(ConversionPreset.ConversionSettingKeys.VideoScale, "0.5", "resizes")]
        [InlineData(ConversionPreset.ConversionSettingKeys.VideoRotation, "90", "rotates")]
        [InlineData(ConversionPreset.ConversionSettingKeys.VideoQuality, "20", "lower quality")]
        public void VideoPresetChangesPreventTheCopyBeforeProbing(string settingKey, string value, string expectedReason)
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(settingKey, value);

            Assert.Contains(expectedReason, Planner.GetBlockerBeforeProbe(preset, "a.mkv"));
        }

        [Fact]
        public void HigherQualityThanTheDefaultStillCopies()
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoQuality, "40");

            Assert.Null(Planner.GetBlockerBeforeProbe(preset, "a.mkv"));
            Assert.Equal("copy copy", Describe(Planner.Plan(preset, "a.mkv", Source("v:h264 a:aac:128"), out _)));
        }

        [Theory]
        [InlineData("Hevc", "h264", "re-encode")]
        [InlineData("Hevc", "hevc", "copy copy")]
        [InlineData("Av1", "hevc", "re-encode")]
        [InlineData("Av1", "av1", "copy copy")]
        [InlineData("H264", "hevc", "copy copy")]
        public void ExplicitCodecRequestNeedsThatCodec(string requestedCodec, string sourceCodec, string expectedPlan)
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoCodec, requestedCodec);

            Assert.Equal(expectedPlan, Describe(Planner.Plan(preset, "a.mkv", Source($"v:{sourceCodec} a:aac:128"), out _)));
        }

        [Theory]
        [InlineData(1080, "copy copy")]
        [InlineData(720, "re-encode")]
        public void TargetResolutionMustMatchTheSource(int resolution, string expectedPlan)
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoResolution, resolution.ToString());

            Assert.Equal(expectedPlan, Describe(Planner.Plan(preset, "a.mkv", Source("v:h264 a:aac:128"), out _)));
        }

        [Fact]
        public void LowerAudioBitrateReencodesTheAudioNextToACopiedVideo()
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.AudioBitrate, "96");

            Assert.Equal("copy encode", Describe(Planner.Plan(preset, "a.mkv", Source("v:h264 a:aac:128"), out _)));
        }

        [Fact]
        public void DisabledAudioDropsTheAudioStreams()
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp4);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.EnableAudio, "False");

            Assert.Equal("copy", Describe(Planner.Plan(preset, "a.mkv", Source("v:h264 a:aac:128 a:ac3"), out _)));
        }

        [Fact]
        public void ChannelCountChangeReencodesAudioOutputs()
        {
            ConversionPreset preset = new ConversionPreset("test", OutputType.Aac);
            preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.AudioChannelCount, "1");

            Assert.Null(Planner.Plan(preset, "a.m4a", Source("a:aac:128"), out string reason));
            Assert.Contains("channel count", reason);
        }

        [Fact]
        public void UnprobedSourceIsReencoded()
        {
            Assert.Null(Planner.Plan(new ConversionPreset("test", OutputType.Mp4), "a.mkv", null, out string reason));
            Assert.Contains("probed", reason);
        }

        [Fact]
        public void CompatibilityTableCanBeReplaced()
        {
            RemuxCompatibility table = RemuxCompatibility.Parse(@"{ ""sourceExtensions"": [ ""mkv"" ], ""containers"": { ""Mp4"": { ""video"": [ ""h264"" ], ""audio"": [] } } }");
            RemuxPlanner planner = new RemuxPlanner(table);

            Assert.Equal("copy encode", Describe(planner.Plan(new ConversionPreset("test", OutputType.Mp4), "a.mkv", Source("v:h264 a:aac:128"), out _)));
            Assert.Null(planner.Plan(new ConversionPreset("test", OutputType.Mkv), "a.mkv", Source("v:h264"), out _));
        }

        /// <summary>
        /// Builds a probe result from "type:codec[:kbps|:cover]" items: v (video, 1920x1080), a (audio, stereo), s, t (attachment).
        /// </summary>
        private static MediaInfo Source(string streams)
        {
            MediaStream[] parsed = streams.Split(' ').Select((item, index) =>
            {
                string[] parts = item.Split(':');
                string type = parts[0] == "v" ? "video" : parts[0] == "a" ? "audio" : parts[0] == "s" ? "subtitle" : "attachment";
                bool cover = parts.Length > 2 && parts[2] == "cover";
                long? bitRate = parts.Length > 2 && !cover ? long.Parse(parts[2]) * 1000 : (long?)null;
                bool isVideo = type == "video";
                return new MediaStream(
                    index,
                    type,
                    parts[1],
                    profile: null,
                    pixelFormat: isVideo ? "yuv420p" : null,
                    width: isVideo ? 1920 : (int?)null,
                    height: isVideo ? 1080 : (int?)null,
                    bitRate: bitRate,
                    sampleRate: type == "audio" ? 48000 : (int?)null,
                    channels: type == "audio" ? 2 : (int?)null,
                    language: null,
                    isAttachedPicture: cover);
            }).ToArray();

            return new MediaInfo("matroska", TimeSpan.FromSeconds(60), null, parsed);
        }

        private static string Describe(RemuxPlan plan)
        {
            if (plan == null)
            {
                return "re-encode";
            }

            return string.Join(" ", plan.Streams.Select(decision =>
                decision.Action == RemuxStreamAction.Convert ? $"convert:{decision.TargetCodec}" : decision.Action.ToString().ToLowerInvariant()));
        }
    }
}
