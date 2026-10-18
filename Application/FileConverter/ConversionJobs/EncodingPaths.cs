// <copyright file="EncodingPaths.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    /// <summary>
    /// Diagnostics labels of the path used to encode a job (logged, and exposed by <see cref="ConversionJob_FFMPEG.EncodingPath"/>).
    /// </summary>
    public static class EncodingPaths
    {
        /// <summary>
        /// Single-attempt passes (audio, images, custom commands...).
        /// </summary>
        public const string Default = "ffmpeg";

        /// <summary>
        /// CPU encode, no hardware encoder selected.
        /// </summary>
        public const string Cpu = "cpu";

        /// <summary>
        /// CPU encode after a hardware attempt failed.
        /// </summary>
        public const string CpuFallback = "cpu-fallback";

        /// <summary>
        /// NVDEC + scale_cuda + NVENC, frames stay in GPU memory.
        /// </summary>
        public const string Nvenc = "nvenc";

        /// <summary>
        /// NVENC with CPU filters (rotation, unsupported pixel formats or codecs for the full GPU pipeline).
        /// </summary>
        public const string NvencCpuFilters = "nvenc-cpu-filters";

        /// <summary>
        /// Hardware decode (default adapter) + AMF encode.
        /// </summary>
        public const string Amf = "amf";

        /// <summary>
        /// CPU decode + AMF encode (the decoder's adapter is not the AMD GPU).
        /// </summary>
        public const string AmfCpuDecode = "amf-cpu-decode";

        public const string Qsv = "qsv";

        public const string QsvCpuDecode = "qsv-cpu-decode";
    }
}
