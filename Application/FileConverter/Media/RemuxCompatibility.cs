// <copyright file="RemuxCompatibility.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Media
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    /// <summary>
    /// Which codecs each output container accepts as a stream copy (<c>-c copy</c>). The table is the embedded
    /// <c>Media/remux-compat.json</c>: extend it there.
    /// </summary>
    public sealed class RemuxCompatibility
    {
        private const string AnyCodec = "*";

        private static readonly Lazy<RemuxCompatibility> DefaultTable = new Lazy<RemuxCompatibility>(LoadDefault);

        private readonly CompatibilityTable table;

        private RemuxCompatibility(CompatibilityTable table)
        {
            this.table = table;
            this.table.TextSubtitleCodecs = this.table.TextSubtitleCodecs ?? new List<string>();
            this.table.Containers = this.table.Containers ?? new Dictionary<string, ContainerRules>();
        }

        /// <summary>
        /// Gets the table embedded in the application.
        /// </summary>
        public static RemuxCompatibility Default => DefaultTable.Value;

        public static RemuxCompatibility Parse(string json)
        {
            DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return new RemuxCompatibility((CompatibilityTable)new DataContractJsonSerializer(typeof(CompatibilityTable), settings).ReadObject(stream));
            }
        }

        /// <summary>
        /// Gets whether the output type has an entry in the table (other output types are never remuxed).
        /// </summary>
        public bool SupportsRemux(OutputType outputType)
        {
            return this.GetRules(outputType) != null;
        }

        public bool CanCopyVideo(OutputType outputType, string codec) => Accepts(this.GetRules(outputType)?.Video, codec);

        public bool CanCopyAudio(OutputType outputType, string codec) => Accepts(this.GetRules(outputType)?.Audio, codec);

        public bool CanCopySubtitle(OutputType outputType, string codec) => Accepts(this.GetRules(outputType)?.Subtitles, codec);

        public bool CanCopyCoverArt(OutputType outputType, string codec) => Accepts(this.GetRules(outputType)?.CoverArt, codec);

        public bool CanCopyAttachments(OutputType outputType) => this.GetRules(outputType)?.Attachments ?? false;

        /// <summary>
        /// Gets the codec that text subtitles are converted to when they can't be copied, or null if they are dropped.
        /// </summary>
        public string GetTextSubtitleTarget(OutputType outputType)
        {
            string target = this.GetRules(outputType)?.TextSubtitlesTo;
            return string.IsNullOrEmpty(target) ? null : target;
        }

        public bool IsTextSubtitle(string codec) => Accepts(this.table.TextSubtitleCodecs, codec);

        private static bool Accepts(List<string> codecs, string codec)
        {
            return codecs != null && !string.IsNullOrEmpty(codec) && codecs.Any(accepted => accepted == AnyCodec || string.Equals(accepted, codec, StringComparison.OrdinalIgnoreCase));
        }

        private static RemuxCompatibility LoadDefault()
        {
            using (Stream stream = typeof(RemuxCompatibility).Assembly.GetManifestResourceStream("FileConverter.Media.remux-compat.json"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("The embedded remux compatibility table is missing.");
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return RemuxCompatibility.Parse(reader.ReadToEnd());
                }
            }
        }

        private ContainerRules GetRules(OutputType outputType)
        {
            return this.table.Containers.TryGetValue(outputType.ToString(), out ContainerRules rules) ? rules : null;
        }

        [DataContract]
        private class CompatibilityTable
        {
            [DataMember(Name = "textSubtitleCodecs")]
            public List<string> TextSubtitleCodecs { get; set; }

            [DataMember(Name = "containers")]
            public Dictionary<string, ContainerRules> Containers { get; set; }
        }

        [DataContract]
        private class ContainerRules
        {
            [DataMember(Name = "video")]
            public List<string> Video { get; set; }

            [DataMember(Name = "audio")]
            public List<string> Audio { get; set; }

            [DataMember(Name = "coverArt")]
            public List<string> CoverArt { get; set; }

            [DataMember(Name = "subtitles")]
            public List<string> Subtitles { get; set; }

            [DataMember(Name = "textSubtitlesTo")]
            public string TextSubtitlesTo { get; set; }

            [DataMember(Name = "attachments")]
            public bool Attachments { get; set; }
        }
    }
}
