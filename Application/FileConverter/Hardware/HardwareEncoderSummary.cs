// <copyright file="HardwareEncoderSummary.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management;

    using FileConverter.Diagnostics;

    /// <summary>
    /// Describes the detected hardware encoders for the settings window, e.g.
    /// "NVIDIA NVENC (NVIDIA GeForce RTX 5070 Ti): H.264, HEVC, AV1".
    /// </summary>
    public static class HardwareEncoderSummary
    {
        // PCI vendor ids (Win32_VideoController.PNPDeviceID "PCI\VEN_10DE&DEV_...").
        private static readonly Dictionary<HardwareEncoderVendor, string> PciVendorIdByVendor = new Dictionary<HardwareEncoderVendor, string>
        {
            { HardwareEncoderVendor.Nvenc, "10DE" },
            { HardwareEncoderVendor.Amf, "1002" },
            { HardwareEncoderVendor.Qsv, "8086" },
        };

        private static readonly Dictionary<HardwareEncoderVendor, string> VendorNames = new Dictionary<HardwareEncoderVendor, string>
        {
            { HardwareEncoderVendor.Nvenc, "NVIDIA NVENC" },
            { HardwareEncoderVendor.Amf, "AMD AMF" },
            { HardwareEncoderVendor.Qsv, "Intel Quick Sync" },
        };

        private static readonly Dictionary<VideoCodec, string> CodecNames = new Dictionary<VideoCodec, string>
        {
            { VideoCodec.H264, "H.264" },
            { VideoCodec.Hevc, "HEVC" },
            { VideoCodec.Av1, "AV1" },
        };

        /// <summary>
        /// Gets one line per vendor, in priority order, with the GPU names found by WMI.
        /// </summary>
        public static IReadOnlyList<string> Describe(IReadOnlyList<HardwareEncoder> encoders)
        {
            return Describe(encoders, QueryVideoControllers());
        }

        /// <param name="videoControllers">Name and PNP device id of each video controller.</param>
        public static IReadOnlyList<string> Describe(IReadOnlyList<HardwareEncoder> encoders, IEnumerable<KeyValuePair<string, string>> videoControllers)
        {
            List<KeyValuePair<string, string>> controllers = videoControllers.ToList();
            List<string> lines = new List<string>();
            foreach (IGrouping<HardwareEncoderVendor, HardwareEncoder> vendorEncoders in encoders.GroupBy(encoder => encoder.Vendor).OrderBy(group => group.Key))
            {
                string vendorId = $"VEN_{PciVendorIdByVendor[vendorEncoders.Key]}";
                string[] gpuNames = controllers
                    .Where(controller => controller.Value != null && controller.Value.IndexOf(vendorId, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(controller => controller.Key)
                    .Distinct()
                    .ToArray();

                string gpus = gpuNames.Length > 0 ? $" ({string.Join(", ", gpuNames)})" : string.Empty;
                string codecs = string.Join(", ", vendorEncoders.OrderBy(encoder => encoder.Codec).Select(encoder => CodecNames[encoder.Codec]));
                lines.Add($"{VendorNames[vendorEncoders.Key]}{gpus}: {codecs}");
            }

            return lines;
        }

        private static IEnumerable<KeyValuePair<string, string>> QueryVideoControllers()
        {
            List<KeyValuePair<string, string>> controllers = new List<KeyValuePair<string, string>>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                {
                    foreach (ManagementBaseObject controller in searcher.Get())
                    {
                        controllers.Add(new KeyValuePair<string, string>(controller["Name"] as string, controller["PNPDeviceID"] as string));
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't query video controllers: {exception.Message}");
            }

            return controllers;
        }
    }
}
