// <copyright file="HardwareEncoderVendor.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    /// <summary>
    /// Hardware encoder families, declared in priority order (NVENC > AMF > QSV).
    /// </summary>
    public enum HardwareEncoderVendor
    {
        Nvenc,
        Amf,
        Qsv,
    }
}
