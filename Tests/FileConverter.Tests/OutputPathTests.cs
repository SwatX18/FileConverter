// <copyright file="OutputPathTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    using FileConverter.ConversionJobs;

    using Xunit;

    /// <summary>
    /// Output paths of the jobs of one batch: inputs that give the same output name get distinct outputs.
    /// </summary>
    public sealed class OutputPathTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "FileConverter.Tests-" + Guid.NewGuid().ToString("N"));

        public OutputPathTests()
        {
            TestServices.Initialize();
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, recursive: true);
        }

        [Fact]
        public void SameOutputNameInABatchGetsDistinctPaths()
        {
            HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ConversionJob first = this.Prepare("a.mp4", reserved);
            ConversionJob second = this.Prepare("a.mkv", reserved);
            ConversionJob third = this.Prepare("A.webm", reserved);

            Assert.Equal(Path.Combine(this.folder, "a.mp3"), first.OutputFilePath);
            Assert.Equal(Path.Combine(this.folder, "a (2).mp3"), second.OutputFilePath);
            Assert.Equal(Path.Combine(this.folder, "A (3).mp3"), third.OutputFilePath);
            Assert.Equal(3, reserved.Count);
        }

        [Fact]
        public void ExistingOutputIsNotOverwritten()
        {
            File.WriteAllText(Path.Combine(this.folder, "a.mp3"), string.Empty);

            ConversionJob job = this.Prepare("a.mp4", new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(Path.Combine(this.folder, "a (2).mp3"), job.OutputFilePath);
        }

        private ConversionJob Prepare(string inputName, ISet<string> reserved)
        {
            string input = Path.Combine(this.folder, inputName);
            File.WriteAllText(input, string.Empty);
            ConversionPreset preset = new ConversionPreset("test", OutputType.Mp3) { OutputFileNameTemplate = "(p)(f)" };
            ConversionJob job = new ConversionJob(preset, input);

            job.PrepareConversion(reserved);

            Assert.Equal(ConversionState.Ready, job.State);
            return job;
        }
    }
}
