// <copyright file="HardwareEncoderDetectorTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using FileConverter.Hardware;

    using Xunit;

    /// <summary>
    /// HardwareEncoderDetector with a fake process runner: each candidate encoder's test encode succeeds, fails, times
    /// out or throws, and the result is cached by fingerprint.
    /// </summary>
    public class HardwareEncoderDetectorTests : IDisposable
    {
        private readonly string directory;
        private readonly string ffmpegPath;
        private readonly string cachePath;

        public HardwareEncoderDetectorTests()
        {
            this.directory = Path.Combine(Path.GetTempPath(), "FileConverter.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.directory);

            // The detector only checks that ffmpeg exists: the fake runner never starts it.
            this.ffmpegPath = Path.Combine(this.directory, "ffmpeg.exe");
            File.WriteAllText(this.ffmpegPath, string.Empty);
            this.cachePath = Path.Combine(this.directory, "hw-cache.json");
        }

        public void Dispose()
        {
            Directory.Delete(this.directory, recursive: true);
        }

        [Fact]
        public void KeepsTheEncodersWhoseTestEncodeSucceeds()
        {
            FakeProcessRunner runner = new FakeProcessRunner(encoder => encoder == "h264_nvenc" || encoder == "hevc_nvenc" || encoder == "h264_amf" ? Success : Failure);

            HardwareEncoderDetector detector = this.CreateDetector(runner);

            Assert.Equal(new[] { "h264_nvenc", "hevc_nvenc", "h264_amf" }, detector.AvailableEncoders.Select(encoder => encoder.FfmpegName));
            Assert.Equal(HardwareEncoder.Candidates.Count, runner.Calls.Count);
            Assert.All(runner.Calls, call => Assert.Contains("-f lavfi -i color=black", call));
        }

        [Fact]
        public void TimeoutsAndExceptionsCountAsUnavailable()
        {
            FakeProcessRunner runner = new FakeProcessRunner(encoder =>
            {
                switch (encoder)
                {
                    case "h264_nvenc":
                        return Success;
                    case "hevc_nvenc":
                        return new ProcessResult(0, timedOut: true, standardError: string.Empty);
                    case "h264_amf":
                        throw new InvalidOperationException("driver crashed");
                    default:
                        return Failure;
                }
            });

            HardwareEncoderDetector detector = this.CreateDetector(runner);

            Assert.Equal(new[] { "h264_nvenc" }, detector.AvailableEncoders.Select(encoder => encoder.FfmpegName));
        }

        [Fact]
        public void TestEncodesUseTheTimeout()
        {
            FakeProcessRunner runner = new FakeProcessRunner(_ => Failure);

            Assert.Empty(this.CreateDetector(runner).AvailableEncoders);
            Assert.All(runner.Timeouts, timeout => Assert.Equal(HardwareEncoderDetector.TestEncodeTimeout, timeout));
        }

        [Fact]
        public void NoEncoderWithoutFfmpeg()
        {
            FakeProcessRunner runner = new FakeProcessRunner(_ => Success);
            HardwareEncoderDetector detector = new HardwareEncoderDetector(runner, Path.Combine(this.directory, "missing.exe"), this.cachePath, () => "fingerprint");

            Assert.Empty(detector.AvailableEncoders);
            Assert.Empty(runner.Calls);
        }

        [Fact]
        public void PrefersNvencThenAmfThenQsv()
        {
            HardwareEncoderDetector detector = this.CreateDetector(new FakeProcessRunner(encoder => encoder.EndsWith("_nvenc") && encoder != "av1_nvenc" || encoder.EndsWith("_amf") || encoder.EndsWith("_qsv") ? Success : Failure));

            Assert.Equal("h264_nvenc", detector.GetPreferredEncoder(VideoCodec.H264).FfmpegName);
            Assert.Equal("av1_amf", detector.GetPreferredEncoder(VideoCodec.Av1).FfmpegName);
            Assert.True(detector.IsAvailable(HardwareEncoderVendor.Qsv, VideoCodec.Hevc));
        }

        [Fact]
        public void NoPreferredEncoderWhenNothingIsDetected()
        {
            HardwareEncoderDetector detector = this.CreateDetector(new FakeProcessRunner(_ => Failure));

            Assert.Null(detector.GetPreferredEncoder(VideoCodec.H264));
        }

        [Fact]
        public void SameFingerprintReadsTheCache()
        {
            this.CreateDetector(new FakeProcessRunner(encoder => encoder == "hevc_nvenc" ? Success : Failure)).AvailableEncoders.ToArray();

            FakeProcessRunner secondRunner = new FakeProcessRunner(_ => Success);
            HardwareEncoderDetector second = this.CreateDetector(secondRunner);

            Assert.Equal(new[] { "hevc_nvenc" }, second.AvailableEncoders.Select(encoder => encoder.FfmpegName));
            Assert.Empty(secondRunner.Calls);
        }

        [Fact]
        public void NewFingerprintDetectsAgain()
        {
            this.CreateDetector(new FakeProcessRunner(_ => Failure), "driver 1").AvailableEncoders.ToArray();

            FakeProcessRunner secondRunner = new FakeProcessRunner(encoder => encoder == "h264_qsv" ? Success : Failure);
            HardwareEncoderDetector second = this.CreateDetector(secondRunner, "driver 2");

            Assert.Equal(new[] { "h264_qsv" }, second.AvailableEncoders.Select(encoder => encoder.FfmpegName));
            Assert.NotEmpty(secondRunner.Calls);
        }

        [Fact]
        public void CorruptCacheDetectsAgain()
        {
            File.WriteAllText(this.cachePath, "{ not json");
            FakeProcessRunner runner = new FakeProcessRunner(encoder => encoder == "h264_amf" ? Success : Failure);

            Assert.Equal(new[] { "h264_amf" }, this.CreateDetector(runner).AvailableEncoders.Select(encoder => encoder.FfmpegName));
        }

        [Fact]
        public void RedetectIgnoresTheCache()
        {
            bool nvencWorks = false;
            FakeProcessRunner runner = new FakeProcessRunner(encoder => encoder == "h264_nvenc" && nvencWorks ? Success : Failure);
            HardwareEncoderDetector detector = this.CreateDetector(runner);
            Assert.Empty(detector.AvailableEncoders);

            nvencWorks = true;
            detector.Redetect();

            Assert.Equal(new[] { "h264_nvenc" }, detector.AvailableEncoders.Select(encoder => encoder.FfmpegName));
            Assert.Equal(new[] { "h264_nvenc" }, this.CreateDetector(new FakeProcessRunner(_ => Failure)).AvailableEncoders.Select(encoder => encoder.FfmpegName));
        }

        [Fact]
        public void SummaryNamesTheGpuOfEachVendor()
        {
            HardwareEncoder[] encoders =
            {
                HardwareEncoder.Find(HardwareEncoderVendor.Nvenc, VideoCodec.H264),
                HardwareEncoder.Find(HardwareEncoderVendor.Nvenc, VideoCodec.Hevc),
                HardwareEncoder.Find(HardwareEncoderVendor.Nvenc, VideoCodec.Av1),
                HardwareEncoder.Find(HardwareEncoderVendor.Amf, VideoCodec.Hevc),
                HardwareEncoder.Find(HardwareEncoderVendor.Amf, VideoCodec.H264),
                HardwareEncoder.Find(HardwareEncoderVendor.Qsv, VideoCodec.H264),
            };
            KeyValuePair<string, string>[] controllers =
            {
                new KeyValuePair<string, string>("AMD Radeon(TM) Graphics", @"PCI\VEN_1002&DEV_13C0&SUBSYS_00000000&REV_C1\4&1"),
                new KeyValuePair<string, string>("NVIDIA GeForce RTX 5070 Ti", @"PCI\VEN_10DE&DEV_2C05&SUBSYS_00000000&REV_A1\4&2"),
                new KeyValuePair<string, string>("Microsoft Basic Display Adapter", @"ROOT\BASICDISPLAY\0000"),
            };

            Assert.Equal(
                new[]
                {
                    "NVIDIA NVENC (NVIDIA GeForce RTX 5070 Ti): H.264, HEVC, AV1",
                    "AMD AMF (AMD Radeon(TM) Graphics): H.264, HEVC",
                    "Intel Quick Sync: H.264",
                },
                HardwareEncoderSummary.Describe(encoders, controllers));
        }

        private static ProcessResult Success => new ProcessResult(0, timedOut: false, standardError: string.Empty);

        private static ProcessResult Failure => new ProcessResult(1, timedOut: false, standardError: "Cannot load nvcuda.dll\nError while opening encoder");

        private HardwareEncoderDetector CreateDetector(FakeProcessRunner runner, string fingerprint = "fingerprint")
        {
            return new HardwareEncoderDetector(runner, this.ffmpegPath, this.cachePath, () => fingerprint);
        }

        private class FakeProcessRunner : IProcessRunner
        {
            private readonly Func<string, ProcessResult> resultByEncoder;

            public FakeProcessRunner(Func<string, ProcessResult> resultByEncoder)
            {
                this.resultByEncoder = resultByEncoder;
            }

            public ConcurrentBag<string> Calls { get; } = new ConcurrentBag<string>();

            public ConcurrentBag<TimeSpan> Timeouts { get; } = new ConcurrentBag<TimeSpan>();

            public ProcessResult Run(string fileName, string arguments, TimeSpan timeout)
            {
                this.Calls.Add(arguments);
                this.Timeouts.Add(timeout);

                // "... -c:v <encoder> -f null -"
                string[] words = arguments.Split(' ');
                string encoder = words[Array.IndexOf(words, "-c:v") + 1];
                return this.resultByEncoder(encoder);
            }
        }
    }
}
