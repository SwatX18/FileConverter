// <copyright file="MediaInfo.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Media
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Container and streams of a media file, as reported by ffprobe (see <see cref="MediaProbe"/>).
    /// </summary>
    public sealed class MediaInfo
    {
        public MediaInfo(string formatName, TimeSpan? duration, long? bitRate, IReadOnlyList<MediaStream> streams)
        {
            this.FormatName = formatName ?? string.Empty;
            this.Duration = duration;
            this.BitRate = bitRate;
            this.Streams = streams ?? new MediaStream[0];
        }

        /// <summary>
        /// ffprobe container name(s), e.g. <c>matroska,webm</c> or <c>mov,mp4,m4a,3gp,3g2,mj2</c>.
        /// </summary>
        public string FormatName { get; }

        public TimeSpan? Duration { get; }

        public long? BitRate { get; }

        public IReadOnlyList<MediaStream> Streams { get; }

        /// <summary>
        /// Gets the first real video stream (cover art / attached pictures are ignored), or null.
        /// </summary>
        public MediaStream PrimaryVideo => this.Streams.FirstOrDefault(stream => stream.IsVideo && !stream.IsAttachedPicture);

        public IEnumerable<MediaStream> AudioStreams => this.Streams.Where(stream => stream.IsAudio);

        public IEnumerable<MediaStream> SubtitleStreams => this.Streams.Where(stream => stream.IsSubtitle);
    }

    public sealed class MediaStream
    {
        private static readonly Regex BitDepthRegex = new Regex(@"(?:p|gray)0?(\d{1,2})(?:le|be)?$", RegexOptions.Compiled);

        public MediaStream(int index, string codecType, string codecName, string profile, string pixelFormat, int? width, int? height, long? bitRate, int? sampleRate, int? channels, string language, bool isAttachedPicture)
        {
            this.Index = index;
            this.CodecType = codecType ?? string.Empty;
            this.CodecName = codecName ?? string.Empty;
            this.Profile = profile ?? string.Empty;
            this.PixelFormat = pixelFormat ?? string.Empty;
            this.Width = width;
            this.Height = height;
            this.BitRate = bitRate;
            this.SampleRate = sampleRate;
            this.Channels = channels;
            this.Language = language ?? string.Empty;
            this.IsAttachedPicture = isAttachedPicture;
        }

        public int Index { get; }

        /// <summary>
        /// <c>video</c>, <c>audio</c>, <c>subtitle</c>, <c>data</c> or <c>attachment</c>.
        /// </summary>
        public string CodecType { get; }

        public string CodecName { get; }

        public string Profile { get; }

        public string PixelFormat { get; }

        public int? Width { get; }

        public int? Height { get; }

        public long? BitRate { get; }

        public int? SampleRate { get; }

        public int? Channels { get; }

        public string Language { get; }

        public bool IsAttachedPicture { get; }

        public bool IsVideo => this.CodecType == "video";

        public bool IsAudio => this.CodecType == "audio";

        public bool IsSubtitle => this.CodecType == "subtitle";

        /// <summary>
        /// Gets the bits per color component of a video stream, from its pixel format
        /// (<c>yuv420p</c>: 8, <c>yuv420p10le</c> / <c>p010le</c>: 10, <c>yuv422p12le</c>: 12). 8 when unknown.
        /// </summary>
        public int BitsPerComponent
        {
            get
            {
                if (this.PixelFormat.StartsWith("rgb48", StringComparison.Ordinal) || this.PixelFormat.StartsWith("rgba64", StringComparison.Ordinal))
                {
                    return 16;
                }

                Match match = BitDepthRegex.Match(this.PixelFormat);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int bits) && bits >= 8 && bits <= 16)
                {
                    return bits;
                }

                return 8;
            }
        }

        public override string ToString()
        {
            string details = this.IsVideo ? $" {this.PixelFormat} {this.Width}x{this.Height}" : this.IsAudio ? $" {this.SampleRate}Hz {this.Channels}ch" : string.Empty;
            string language = string.IsNullOrEmpty(this.Language) ? string.Empty : $" [{this.Language}]";
            return $"#{this.Index} {this.CodecType}:{this.CodecName}{details}{language}";
        }
    }
}
