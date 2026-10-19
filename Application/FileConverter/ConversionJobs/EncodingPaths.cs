// <copyright file="EncodingPaths.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    /// <summary>
    /// Diagnostics labels of the path used to encode a job (logged, and exposed by <see cref="ConversionJob.EncodingPath"/>).
    /// </summary>
    public static class EncodingPaths
    {
        /// <summary>
        /// Single-attempt passes (audio, images, custom commands...).
        /// </summary>
        public const string Default = "ffmpeg";

        /// <summary>
        /// Stream copy without re-encoding (some audio streams may be re-encoded next to a copied video), see <see cref="Media.RemuxPlanner"/>.
        /// </summary>
        public const string Remux = "remux";

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

        /// <summary>
        /// Image converted by libvips (see <see cref="ConversionJob_Vips"/>).
        /// </summary>
        public const string Vips = "vips";

        /// <summary>
        /// Image converted by ImageMagick because libvips doesn't handle it (format, setting).
        /// </summary>
        public const string ImageMagick = "imagemagick";

        /// <summary>
        /// Image converted by ImageMagick after libvips failed or found the file unsupported once opened.
        /// </summary>
        public const string ImageMagickFallback = "imagemagick-fallback";
    }
}
