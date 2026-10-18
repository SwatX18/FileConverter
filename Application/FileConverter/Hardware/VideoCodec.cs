// <copyright file="VideoCodec.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    /// <summary>
    /// Video codec used by the Mp4 / Mkv presets.
    /// The value is stored by name in the preset settings (key <c>VideoCodec</c>), so don't rename the members.
    /// </summary>
    public enum VideoCodec
    {
        H264,
        Hevc,
        Av1,
    }
}
