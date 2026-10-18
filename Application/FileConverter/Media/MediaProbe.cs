// <copyright file="MediaProbe.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Media
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    using FileConverter.Diagnostics;
    using FileConverter.Hardware;

    /// <summary>
    /// Reads the container and streams of a media file with ffprobe:
    /// <c>ffprobe -v error -show_streams -show_format -of json &lt;file&gt;</c>.
    /// </summary>
    public class MediaProbe
    {
        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

        private readonly IProcessRunner processRunner;
        private readonly string ffprobePath;

        public MediaProbe(IProcessRunner processRunner, string ffprobePath)
        {
            this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            this.ffprobePath = ffprobePath ?? throw new ArgumentNullException(nameof(ffprobePath));
        }

        /// <summary>
        /// Gets the default probe, using the ffprobe.exe next to the application.
        /// </summary>
        public static MediaProbe Default
        {
            get
            {
                string applicationDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return new MediaProbe(new ProcessRunner(), Path.Combine(applicationDirectory, "ffprobe.exe"));
            }
        }

        /// <summary>
        /// Probes <paramref name="filePath"/>. Returns null (and logs why) if ffprobe is missing or fails:
        /// callers must keep working without the information.
        /// </summary>
        public MediaInfo TryProbe(string filePath)
        {
            if (!File.Exists(this.ffprobePath))
            {
                Debug.Log($"ffprobe not found ({this.ffprobePath}): media probing skipped.");
                return null;
            }

            try
            {
                ProcessResult result = this.processRunner.Run(this.ffprobePath, $"-v error -show_streams -show_format -of json \"{filePath}\"", ProbeTimeout);
                if (!result.Succeeded)
                {
                    string reason = result.TimedOut ? "timeout" : $"exit code {result.ExitCode}";
                    Debug.Log($"ffprobe failed on '{filePath}' ({reason}): {result.StandardError.Trim()}");
                    return null;
                }

                return MediaProbe.Parse(result.StandardOutput);
            }
            catch (Exception exception)
            {
                Debug.Log($"ffprobe failed on '{filePath}': {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses ffprobe's JSON output (<c>-show_streams -show_format -of json</c>).
        /// </summary>
        public static MediaInfo Parse(string json)
        {
            ProbeOutput output;
            DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                output = (ProbeOutput)new DataContractJsonSerializer(typeof(ProbeOutput), settings).ReadObject(stream);
            }

            List<MediaStream> streams = (output.Streams ?? new List<ProbeStream>())
                .Select(stream => new MediaStream(
                    stream.Index,
                    stream.CodecType,
                    stream.CodecName,
                    stream.Profile,
                    stream.PixelFormat,
                    stream.Width,
                    stream.Height,
                    ParseLong(stream.BitRate),
                    (int?)ParseLong(stream.SampleRate),
                    stream.Channels,
                    stream.Tags != null && stream.Tags.TryGetValue("language", out string language) ? language : null,
                    stream.Disposition != null && stream.Disposition.TryGetValue("attached_pic", out int attachedPicture) && attachedPicture == 1,
                    ParseStreamDuration(stream)))
                .ToList();

            ProbeFormat format = output.Format ?? new ProbeFormat();
            double? durationSeconds = ParseDouble(format.Duration);
            TimeSpan? duration = durationSeconds.HasValue ? TimeSpan.FromSeconds(durationSeconds.Value) : (TimeSpan?)null;

            return new MediaInfo(format.FormatName, duration, ParseLong(format.BitRate), streams);
        }

        /// <summary>
        /// Stream duration in seconds, or (Matroska) the <c>DURATION</c> tag written by ffmpeg and mkvmerge (<c>00:00:59.424000000</c>).
        /// </summary>
        private static TimeSpan? ParseStreamDuration(ProbeStream stream)
        {
            double? seconds = ParseDouble(stream.Duration);
            if (seconds.HasValue)
            {
                return TimeSpan.FromSeconds(seconds.Value);
            }

            if (stream.Tags != null && stream.Tags.TryGetValue("DURATION", out string tag))
            {
                string[] parts = tag.Split(':');
                if (parts.Length == 3 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
                {
                    double? tagSeconds = ParseDouble(parts[2]);
                    if (tagSeconds.HasValue)
                    {
                        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(tagSeconds.Value);
                    }
                }
            }

            return null;
        }

        private static long? ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : (long?)null;
        }

        private static double? ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : (double?)null;
        }

        // ffprobe JSON schema (only the fields used). Numbers that ffprobe writes as strings are kept as strings.
        [DataContract]
        private class ProbeOutput
        {
            [DataMember(Name = "streams")]
            public List<ProbeStream> Streams { get; set; }

            [DataMember(Name = "format")]
            public ProbeFormat Format { get; set; }
        }

        [DataContract]
        private class ProbeStream
        {
            [DataMember(Name = "index")]
            public int Index { get; set; }

            [DataMember(Name = "codec_name")]
            public string CodecName { get; set; }

            [DataMember(Name = "codec_type")]
            public string CodecType { get; set; }

            [DataMember(Name = "profile")]
            public string Profile { get; set; }

            [DataMember(Name = "pix_fmt")]
            public string PixelFormat { get; set; }

            [DataMember(Name = "width")]
            public int? Width { get; set; }

            [DataMember(Name = "height")]
            public int? Height { get; set; }

            [DataMember(Name = "bit_rate")]
            public string BitRate { get; set; }

            [DataMember(Name = "sample_rate")]
            public string SampleRate { get; set; }

            [DataMember(Name = "channels")]
            public int? Channels { get; set; }

            [DataMember(Name = "duration")]
            public string Duration { get; set; }

            [DataMember(Name = "tags")]
            public Dictionary<string, string> Tags { get; set; }

            [DataMember(Name = "disposition")]
            public Dictionary<string, int> Disposition { get; set; }
        }

        [DataContract]
        private class ProbeFormat
        {
            [DataMember(Name = "format_name")]
            public string FormatName { get; set; }

            [DataMember(Name = "duration")]
            public string Duration { get; set; }

            [DataMember(Name = "bit_rate")]
            public string BitRate { get; set; }
        }
    }
}
