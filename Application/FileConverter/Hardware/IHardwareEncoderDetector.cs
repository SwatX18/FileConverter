// <copyright file="IHardwareEncoderDetector.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System.Collections.Generic;

    public interface IHardwareEncoderDetector
    {
        /// <summary>
        /// Gets the hardware encoders that passed the test encode. Runs the detection (or loads the cache) on first access.
        /// </summary>
        IReadOnlyList<HardwareEncoder> AvailableEncoders { get; }

        /// <summary>
        /// Gets the best available hardware encoder for <paramref name="codec"/> (NVENC > AMF > QSV), or null if there is none.
        /// </summary>
        HardwareEncoder GetPreferredEncoder(VideoCodec codec);

        bool IsAvailable(HardwareEncoderVendor vendor, VideoCodec codec);

        /// <summary>
        /// Ignores the cache and runs the test encodes again.
        /// </summary>
        void Redetect();
    }
}
