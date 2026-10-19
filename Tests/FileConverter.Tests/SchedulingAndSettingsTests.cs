// <copyright file="SchedulingAndSettingsTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using FileConverter.ConversionJobs;
    using FileConverter.Services;

    using Xunit;

    public class SchedulingAndSettingsTests
    {
        [Theory]
        // setting total, GPU, CPU video, physical cores -> total, GPU, CPU video, documents
        [InlineData(0, 4, 0, 16, 16, 4, 4, 2)]
        [InlineData(0, 4, 0, 8, 8, 4, 2, 2)]
        [InlineData(0, 4, 0, 4, 4, 4, 2, 2)]
        [InlineData(0, 4, 0, 1, 1, 1, 1, 1)]
        [InlineData(0, 4, 0, 64, 64, 4, 4, 2)]
        [InlineData(6, 8, 0, 16, 6, 6, 4, 2)]
        [InlineData(0, 0, 0, 16, 16, 1, 4, 2)]
        [InlineData(0, 4, 1, 16, 16, 4, 1, 2)]
        [InlineData(0, 4, 12, 16, 16, 4, 12, 2)]
        [InlineData(3, 4, 12, 16, 3, 3, 3, 2)]
        public void LimitsPerLane(int total, int gpu, int cpuVideo, int cores, int expectedTotal, int expectedGpu, int expectedCpuVideo, int expectedDocuments)
        {
            ConversionLimits limits = ConversionLimits.Compute(total, gpu, cpuVideo, cores);

            Assert.Equal(expectedTotal, limits.Total);
            Assert.Equal(expectedGpu, limits.GetLaneLimit(ConversionLane.Gpu));
            Assert.Equal(expectedCpuVideo, limits.GetLaneLimit(ConversionLane.CpuVideo));
            Assert.Equal(expectedDocuments, limits.GetLaneLimit(ConversionLane.Documents));
            Assert.Equal(expectedTotal, limits.GetLaneLimit(ConversionLane.Light));
        }

        [Theory]
        [InlineData(32, 0, 0)]
        [InlineData(32, 1, 0)]
        [InlineData(32, 2, 16)]
        [InlineData(32, 4, 8)]
        [InlineData(32, 16, 2)]
        [InlineData(32, 64, 1)]
        [InlineData(6, 4, 1)]
        public void ThreadBudget(int logicalProcessors, int simultaneousJobs, int expected)
        {
            Assert.Equal(expected, ConversionLimits.ComputeThreadBudget(logicalProcessors, simultaneousJobs));
        }

        [Theory]
        // version, stored value -> migrated value
        [InlineData(4, Helpers.HardwareAccelerationMode.Off, Helpers.HardwareAccelerationMode.Auto)]
        [InlineData(4, Helpers.HardwareAccelerationMode.CUDA, Helpers.HardwareAccelerationMode.CUDA)]
        [InlineData(4, Helpers.HardwareAccelerationMode.AMF, Helpers.HardwareAccelerationMode.AMF)]
        [InlineData(5, Helpers.HardwareAccelerationMode.Off, Helpers.HardwareAccelerationMode.Off)]
        public void HardwareAccelerationMigration(int version, Helpers.HardwareAccelerationMode stored, Helpers.HardwareAccelerationMode expected)
        {
            Settings settings = new Settings { SerializationVersion = version, HardwareAccelerationMode = stored };

            SettingsService.MigrateSettingsToCurrentVersion(settings);

            Assert.Equal(expected, settings.HardwareAccelerationMode);
        }

        [Theory]
        [InlineData(5, 2, 0)]
        [InlineData(5, 6, 6)]
        [InlineData(5, 1, 1)]
        [InlineData(4, 2, 0)]
        [InlineData(6, 2, 2)]
        public void SimultaneousConversionsMigration(int version, int stored, int expected)
        {
            Settings settings = new Settings { SerializationVersion = version, MaximumNumberOfSimultaneousConversions = stored };

            SettingsService.MigrateSettingsToCurrentVersion(settings);

            Assert.Equal(expected, settings.MaximumNumberOfSimultaneousConversions);
        }

        [Fact]
        public void NewSettingsDefaultToAutomaticAndFastPaths()
        {
            Settings settings = new Settings();

            Assert.Equal(Helpers.HardwareAccelerationMode.Auto, settings.HardwareAccelerationMode);
            Assert.True(settings.CopyStreamsWhenPossible);
            Assert.Equal(Helpers.ImageEngine.Auto, settings.ImageEngine);
            Assert.Equal(0, settings.MaximumNumberOfSimultaneousCpuVideoConversions);
            Assert.Equal(Hardware.GpuSessionLimiter.DefaultMaximumSessions, settings.MaximumNumberOfSimultaneousGpuConversions);
        }
    }
}
