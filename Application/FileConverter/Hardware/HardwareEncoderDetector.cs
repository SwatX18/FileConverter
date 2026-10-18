// <copyright file="HardwareEncoderDetector.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Management;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;
    using System.Threading.Tasks;

    using FileConverter.Diagnostics;

    /// <summary>
    /// Detects the usable hardware video encoders by running a tiny test encode with each candidate encoder.
    /// GPU names are not trusted: a driver, a disabled device or a missing feature (e.g. AV1 on older GPUs)
    /// only shows up when actually encoding. The result is cached on disk, keyed by a hardware fingerprint
    /// (GPU names + driver versions + ffmpeg build), and re-detected when the fingerprint changes.
    /// </summary>
    public class HardwareEncoderDetector : IHardwareEncoderDetector
    {
        public static readonly TimeSpan TestEncodeTimeout = TimeSpan.FromSeconds(5);

        private const string TestEncodeArguments = "-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:d=0.1 -frames:v 1 -c:v {0} -f null -";

        private readonly IProcessRunner processRunner;
        private readonly string ffmpegPath;
        private readonly string cacheFilePath;
        private readonly Func<string> fingerprintProvider;
        private readonly object detectionLock = new object();

        private IReadOnlyList<HardwareEncoder> availableEncoders;

        public HardwareEncoderDetector(IProcessRunner processRunner, string ffmpegPath, string cacheFilePath, Func<string> fingerprintProvider = null)
        {
            this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            this.ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
            this.cacheFilePath = cacheFilePath;
            this.fingerprintProvider = fingerprintProvider ?? (() => HardwareEncoderDetector.ComputeFingerprint(ffmpegPath));
        }

        public IReadOnlyList<HardwareEncoder> AvailableEncoders
        {
            get
            {
                lock (this.detectionLock)
                {
                    if (this.availableEncoders == null)
                    {
                        this.availableEncoders = this.LoadOrDetect(useCache: true);
                    }

                    return this.availableEncoders;
                }
            }
        }

        public HardwareEncoder GetPreferredEncoder(VideoCodec codec)
        {
            // Candidates are declared in vendor priority order.
            return this.AvailableEncoders.FirstOrDefault(encoder => encoder.Codec == codec);
        }

        public bool IsAvailable(HardwareEncoderVendor vendor, VideoCodec codec)
        {
            return this.AvailableEncoders.Any(encoder => encoder.Vendor == vendor && encoder.Codec == codec);
        }

        public void Redetect()
        {
            lock (this.detectionLock)
            {
                this.availableEncoders = this.LoadOrDetect(useCache: false);
            }
        }

        /// <summary>
        /// Video controllers (name + driver version) and the ffmpeg build: any change invalidates the cache.
        /// </summary>
        public static string ComputeFingerprint(string ffmpegPath)
        {
            StringBuilder fingerprint = new StringBuilder();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController"))
                {
                    List<string> controllers = new List<string>();
                    foreach (ManagementBaseObject controller in searcher.Get())
                    {
                        controllers.Add($"{controller["Name"]}|{controller["DriverVersion"]}|{controller["PNPDeviceID"]}");
                    }

                    controllers.Sort(StringComparer.Ordinal);
                    fingerprint.Append(string.Join(";", controllers));
                }
            }
            catch (Exception exception)
            {
                // Without WMI the fingerprint only depends on ffmpeg: still correct, just re-detects less eagerly.
                Debug.Log($"Can't query video controllers: {exception.Message}");
            }

            try
            {
                FileInfo ffmpeg = new FileInfo(ffmpegPath);
                fingerprint.Append($"#ffmpeg|{ffmpeg.Length}|{ffmpeg.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception)
            {
                fingerprint.Append("#ffmpeg|missing");
            }

            return fingerprint.ToString();
        }

        private IReadOnlyList<HardwareEncoder> LoadOrDetect(bool useCache)
        {
            string fingerprint = this.fingerprintProvider();

            if (useCache)
            {
                IReadOnlyList<HardwareEncoder> cached = this.LoadCache(fingerprint);
                if (cached != null)
                {
                    Debug.Log($"Hardware encoders (cached): {HardwareEncoderDetector.Describe(cached)}");
                    return cached;
                }
            }

            IReadOnlyList<HardwareEncoder> detected = this.Detect();
            this.SaveCache(fingerprint, detected);
            return detected;
        }

        private IReadOnlyList<HardwareEncoder> Detect()
        {
            if (!File.Exists(this.ffmpegPath))
            {
                Debug.Log($"Hardware encoder detection skipped: ffmpeg not found ({this.ffmpegPath}).");
                return new HardwareEncoder[0];
            }

            DateTime start = DateTime.UtcNow;
            Task<bool>[] tests = HardwareEncoder.Candidates
                .Select(candidate => Task.Run(() => this.TestEncoder(candidate)))
                .ToArray();
            Task.WaitAll(tests);

            List<HardwareEncoder> available = new List<HardwareEncoder>();
            for (int index = 0; index < tests.Length; index++)
            {
                if (tests[index].Result)
                {
                    available.Add(HardwareEncoder.Candidates[index]);
                }
            }

            Debug.Log($"Hardware encoders detected in {(DateTime.UtcNow - start).TotalMilliseconds:F0}ms: {HardwareEncoderDetector.Describe(available)}");
            return available;
        }

        private bool TestEncoder(HardwareEncoder candidate)
        {
            try
            {
                ProcessResult result = this.processRunner.Run(this.ffmpegPath, string.Format(TestEncodeArguments, candidate.FfmpegName), TestEncodeTimeout);
                if (!result.Succeeded)
                {
                    string reason = result.TimedOut ? "timeout" : $"exit code {result.ExitCode}";
                    string firstErrorLine = result.StandardError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    Debug.Log($"  {candidate.FfmpegName}: unavailable ({reason}) {firstErrorLine}");
                }

                return result.Succeeded;
            }
            catch (Exception exception)
            {
                Debug.Log($"  {candidate.FfmpegName}: test failed ({exception.Message})");
                return false;
            }
        }

        private IReadOnlyList<HardwareEncoder> LoadCache(string fingerprint)
        {
            if (string.IsNullOrEmpty(this.cacheFilePath) || !File.Exists(this.cacheFilePath))
            {
                return null;
            }

            try
            {
                HardwareEncoderCache cache;
                using (FileStream stream = File.OpenRead(this.cacheFilePath))
                {
                    cache = (HardwareEncoderCache)new DataContractJsonSerializer(typeof(HardwareEncoderCache)).ReadObject(stream);
                }

                if (cache == null || cache.Fingerprint != fingerprint || cache.Encoders == null)
                {
                    Debug.Log("Hardware encoder cache is outdated (hardware, driver or ffmpeg changed).");
                    return null;
                }

                return cache.Encoders.Select(HardwareEncoder.FromFfmpegName).Where(encoder => encoder != null).ToArray();
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't read hardware encoder cache: {exception.Message}");
                return null;
            }
        }

        private void SaveCache(string fingerprint, IReadOnlyList<HardwareEncoder> encoders)
        {
            if (string.IsNullOrEmpty(this.cacheFilePath))
            {
                return;
            }

            try
            {
                HardwareEncoderCache cache = new HardwareEncoderCache
                {
                    Fingerprint = fingerprint,
                    DetectedAtUtc = DateTime.UtcNow.ToString("o"),
                    Encoders = encoders.Select(encoder => encoder.FfmpegName).ToList(),
                };

                string temporaryPath = this.cacheFilePath + ".tmp";
                using (FileStream stream = File.Create(temporaryPath))
                {
                    new DataContractJsonSerializer(typeof(HardwareEncoderCache)).WriteObject(stream, cache);
                }

                if (File.Exists(this.cacheFilePath))
                {
                    File.Delete(this.cacheFilePath);
                }

                File.Move(temporaryPath, this.cacheFilePath);
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't write hardware encoder cache: {exception.Message}");
            }
        }

        private static string Describe(IReadOnlyList<HardwareEncoder> encoders)
        {
            return encoders.Count == 0 ? "none" : string.Join(", ", encoders.Select(encoder => encoder.FfmpegName));
        }

        [DataContract]
        private class HardwareEncoderCache
        {
            [DataMember(Order = 0)]
            public string Fingerprint { get; set; }

            [DataMember(Order = 1)]
            public string DetectedAtUtc { get; set; }

            [DataMember(Order = 2)]
            public List<string> Encoders { get; set; }
        }
    }
}
