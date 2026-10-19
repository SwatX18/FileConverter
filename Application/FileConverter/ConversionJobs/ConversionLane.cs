// <copyright file="ConversionLane.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    /// <summary>
    /// The resource a conversion job mostly uses. Each lane has its own limit of simultaneous jobs (see
    /// <see cref="Services.ConversionLimits"/>), so that a mixed batch keeps the GPU and the CPU busy at the same time.
    /// </summary>
    public enum ConversionLane
    {
        /// <summary>
        /// Images, audio, stream copies (remux) and everything short: as many jobs as allowed in total.
        /// </summary>
        Light,

        /// <summary>
        /// Software video encodes (x264, x265, VP9...), already multithreaded: a few at a time.
        /// </summary>
        CpuVideo,

        /// <summary>
        /// Hardware video encodes (NVENC, AMF, QSV): limited by the GPU's encode sessions.
        /// </summary>
        Gpu,

        /// <summary>
        /// Word / Excel / PowerPoint automation: each job drives an Office application.
        /// </summary>
        Documents,
    }
}
