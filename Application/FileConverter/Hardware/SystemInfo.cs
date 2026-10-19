// <copyright file="SystemInfo.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Management;

    using FileConverter.Diagnostics;

    /// <summary>
    /// CPU and disk facts used to schedule conversions (queried once, cached).
    /// </summary>
    public static class SystemInfo
    {
        // WMI MSFT_PhysicalDisk.MediaType.
        private const ushort MediaTypeHdd = 3;

        private static readonly Lazy<int> PhysicalCores = new Lazy<int>(QueryPhysicalCores);
        private static readonly Lazy<Dictionary<char, uint>> HardDiskByDriveLetter = new Lazy<Dictionary<char, uint>>(QueryHardDisks);

        /// <summary>
        /// Gets the number of physical CPU cores (logical processors / 2 if WMI is unavailable).
        /// </summary>
        public static int PhysicalCoreCount => PhysicalCores.Value;

        public static int LogicalProcessorCount => Environment.ProcessorCount;

        /// <summary>
        /// Gets the number of the hard disk drive (rotational) that holds <paramref name="path"/>, or null for SSDs,
        /// network paths, removable or unknown media.
        /// </summary>
        public static uint? GetHardDiskNumber(string path)
        {
            string root;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                return null;
            }

            return HardDiskByDriveLetter.Value.TryGetValue(char.ToUpperInvariant(root[0]), out uint diskNumber) ? diskNumber : (uint?)null;
        }

        private static int QueryPhysicalCores()
        {
            try
            {
                int cores = 0;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                {
                    foreach (ManagementBaseObject processor in searcher.Get())
                    {
                        cores += Convert.ToInt32(processor["NumberOfCores"]);
                    }
                }

                if (cores > 0)
                {
                    return cores;
                }
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't read the number of CPU cores: {exception.Message}");
            }

            return Math.Max(1, Environment.ProcessorCount / 2);
        }

        /// <summary>
        /// Maps drive letters to the number of the physical disk behind them, for hard disk drives only
        /// (Storage Management API: MSFT_Partition and MSFT_PhysicalDisk, Windows 8+).
        /// </summary>
        private static Dictionary<char, uint> QueryHardDisks()
        {
            Dictionary<char, uint> hardDisks = new Dictionary<char, uint>();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                HashSet<uint> hardDiskNumbers = new HashSet<uint>();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk")))
                {
                    foreach (ManagementBaseObject disk in searcher.Get())
                    {
                        if (Convert.ToUInt16(disk["MediaType"]) == MediaTypeHdd && uint.TryParse(Convert.ToString(disk["DeviceId"]), out uint number))
                        {
                            hardDiskNumbers.Add(number);
                        }
                    }
                }

                if (hardDiskNumbers.Count > 0)
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
                    {
                        foreach (ManagementBaseObject partition in searcher.Get())
                        {
                            char letter = Convert.ToChar(partition["DriveLetter"]);
                            uint diskNumber = Convert.ToUInt32(partition["DiskNumber"]);
                            if (char.IsLetter(letter) && hardDiskNumbers.Contains(diskNumber))
                            {
                                hardDisks[char.ToUpperInvariant(letter)] = diskNumber;
                            }
                        }
                    }
                }

                Debug.Log(hardDisks.Count > 0 ?
                    $"Hard disk drives: {string.Join(", ", System.Linq.Enumerable.Select(hardDisks, pair => $"{pair.Key}: (disk {pair.Value})"))}." :
                    "No hard disk drive found.");
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't detect hard disk drives: {exception.Message}");
            }

            return hardDisks;
        }
    }
}
