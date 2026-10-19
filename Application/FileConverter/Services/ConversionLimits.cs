// <copyright file="ConversionLimits.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Services
{
    using System;

    using FileConverter.ConversionJobs;

    /// <summary>
    /// How many conversion jobs may run at the same time, in total and per <see cref="ConversionLane"/>.
    /// </summary>
    public sealed class ConversionLimits
    {
        /// <summary>
        /// Jobs streaming large files (see <see cref="HardDiskLimitMinimumFileSize"/>) on the same hard disk drive: more
        /// makes the heads seek between files (4 x 1.1 GB remuxes on a 7200 rpm disk: 92.6 s with this limit, 107.8 s
        /// without).
        /// </summary>
        public const int JobsPerHardDisk = 2;

        /// <summary>
        /// The hard disk limit only applies to jobs whose input is at least this large. Smaller files are served by the
        /// Windows file cache: 16 x 24 MP JPEG -> PNG on a hard disk took 22 s limited to 2 jobs and 3 s without limit.
        /// </summary>
        public const long HardDiskLimitMinimumFileSize = 100L * 1024 * 1024;

        /// <summary>
        /// Office documents: each job drives a Word / Excel / PowerPoint instance (the previous global default was 2).
        /// </summary>
        public const int MaximumDocumentJobs = 2;

        private ConversionLimits(int total, int gpu, int cpuVideo, int documents)
        {
            this.Total = total;
            this.Gpu = gpu;
            this.CpuVideo = cpuVideo;
            this.Documents = documents;
        }

        public int Total { get; }

        public int Gpu { get; }

        public int CpuVideo { get; }

        public int Documents { get; }

        /// <param name="maximumSimultaneousConversions">The user setting; 0 (or less) means automatic: one job per physical core.</param>
        /// <param name="maximumSimultaneousGpuConversions">The GPU encode session limit (see <see cref="Hardware.GpuSessionLimiter"/>).</param>
        public static ConversionLimits Compute(int maximumSimultaneousConversions, int maximumSimultaneousGpuConversions, int physicalCores)
        {
            physicalCores = Math.Max(1, physicalCores);
            int total = maximumSimultaneousConversions > 0 ? maximumSimultaneousConversions : physicalCores;

            // Software video encoders already use many threads each: 2 to 4 jobs keep the cores busy without thrashing.
            int cpuVideo = Math.Max(2, Math.Min(4, physicalCores / 4));

            return new ConversionLimits(
                total,
                Math.Min(total, Math.Max(1, maximumSimultaneousGpuConversions)),
                Math.Min(total, cpuVideo),
                Math.Min(total, MaximumDocumentJobs));
        }

        /// <summary>
        /// Gets the number of CPU threads a job should use when <paramref name="simultaneousJobs"/> jobs share the CPU.
        /// </summary>
        /// <returns>0 (no limit) for a single job.</returns>
        public static int ComputeThreadBudget(int logicalProcessors, int simultaneousJobs)
        {
            if (simultaneousJobs <= 1)
            {
                return 0;
            }

            return Math.Max(1, logicalProcessors / simultaneousJobs);
        }

        public int GetLaneLimit(ConversionLane lane)
        {
            switch (lane)
            {
                case ConversionLane.Gpu:
                    return this.Gpu;

                case ConversionLane.CpuVideo:
                    return this.CpuVideo;

                case ConversionLane.Documents:
                    return this.Documents;

                default:
                    return this.Total;
            }
        }

        public override string ToString()
        {
            return $"total {this.Total}, GPU {this.Gpu}, CPU video {this.CpuVideo}, documents {this.Documents}, {JobsPerHardDisk} per hard disk";
        }
    }
}
