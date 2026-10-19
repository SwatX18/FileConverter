// <copyright file="ConversionJob_Vips.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ConversionJobs
{
    using System;
    using System.IO;
    using System.Threading;

    using CommunityToolkit.Mvvm.DependencyInjection;
    using FileConverter.Diagnostics;
    using FileConverter.Imaging;
    using FileConverter.Services;
    using NetVips;

    /// <summary>
    /// Converts images with libvips (shrink-on-load resize, streaming), and falls back to the ImageMagick conversion
    /// (the base class) when libvips can't handle the file or fails. The result follows the same preset settings.
    /// </summary>
    public class ConversionJob_Vips : ConversionJob_ImageMagick
    {
        // AVIF calibration against ImageMagick at the default quality 50 (real photos, SSIM against the source,
        // in-process encode of 20-24 MP; see docs/IMAGES.md):
        //   ImageMagick q50              1.4-2.3 s, reference size, SSIM 0.976-0.986
        //   libvips q50 effort 4         1.3-1.5 s, +28-46%,        SSIM 0.990-0.995 (better, not smaller)
        //   libvips q45 effort 3         0.4-0.5 s, +11-22%,        SSIM 0.976-0.985 (same quality)
        //   libvips q50 effort 2         0.3-0.4 s, +40-52%,        SSIM 0.975-0.987
        // effort 3 with q - 5 gives ImageMagick's quality 3-5x faster, for a slightly larger file.

        /// <summary>
        /// AV1 encoder effort for AVIF (0 fastest to 9 slowest, libvips default 4).
        /// </summary>
        private const int AvifEffort = 3;

        /// <summary>
        /// Added to the preset quality for AVIF, so that libvips matches ImageMagick's quality at the same setting.
        /// </summary>
        private const int AvifQualityOffset = -5;

        private readonly ISettingsService settingsService = Ioc.Default.GetService<ISettingsService>();

        public ConversionJob_Vips() : base()
        {
        }

        public ConversionJob_Vips(ConversionPreset conversionPreset, string inputFilePath) : base(conversionPreset, inputFilePath)
        {
        }

        /// <summary>
        /// Gets the engine that wrote the output (see <see cref="EncodingPaths"/>), for diagnostics.
        /// </summary>
        public string EncodingPath
        {
            get;
            private set;
        }

        protected override void Convert()
        {
            string blocker = this.GetBlocker();
            if (blocker == null)
            {
                try
                {
                    VipsEngine.SetConcurrency(this.ThreadBudget);
                    this.ConvertWithVips();
                    this.EncodingPath = EncodingPaths.Vips;
                    Debug.Log("Image engine: vips.");
                    return;
                }
                catch (Exception exception) when (!this.CancelIsRequested)
                {
                    blocker = exception is VipsUnsupportedException ? exception.Message : $"libvips failed ({exception.Message.Trim()})";
                    this.DeletePartialOutput();
                    this.Progress = 0f;
                    this.EncodingPath = exception is VipsUnsupportedException ? EncodingPaths.ImageMagick : EncodingPaths.ImageMagickFallback;
                }
                catch (Exception)
                {
                    // Canceled: the job is already failed.
                    this.DeletePartialOutput();
                    return;
                }
            }
            else
            {
                this.EncodingPath = EncodingPaths.ImageMagick;
            }

            Debug.Log($"Image engine: {this.EncodingPath} ({blocker}).");
            base.Convert();
        }

        private string GetBlocker()
        {
            Settings settings = this.settingsService?.Settings;
            if (settings != null && settings.ImageEngine == Helpers.ImageEngine.ImageMagick)
            {
                return "image engine set to ImageMagick only";
            }

            string blocker = VipsEngine.GetBlocker(this.ConversionPreset.OutputType, this.InputFilePath);
            if (blocker != null)
            {
                return blocker;
            }

            if (this.IsRelevant(ConversionPreset.ConversionSettingKeys.ImageRotation))
            {
                float rotation = this.ConversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.ImageRotation);
                if (GetQuarterTurns(rotation) < 0)
                {
                    return $"a {rotation}° rotation is done by ImageMagick";
                }
            }

            return null;
        }

        private void ConvertWithVips()
        {
            // The header gives the size and orientation without decoding the pixels.
            int width, height, orientation;
            Enums.ForeignKeep keep;
            using (Image header = Image.NewFromFile(this.InputFilePath, access: Enums.Access.Sequential))
            {
                CheckSupported(header);
                width = header.Width;
                height = header.Height;
                orientation = header.Contains("orientation") ? System.Convert.ToInt32(header.Get("orientation")) : 1;

                // Metadata is kept (EXIF, XMP, IPTC, ICC) like ImageMagick does, but libvips would also create an EXIF
                // block for images that had none: only keep EXIF if the source has some.
                keep = header.Contains("exif-data") ? Enums.ForeignKeep.All : Enums.ForeignKeep.All & ~Enums.ForeignKeep.Exif;
            }

            // Output size, computed like the ImageMagick conversion (same steps, same rounding) so both engines give the
            // same dimensions. ImageMagick keeps the EXIF orientation; libvips applies it: for 90° orientations (5 to 8)
            // the displayed image is height x width.
            this.ComputeOutputSize(width, height, out int outputWidth, out int outputHeight);
            if (orientation >= 5 && orientation <= 8)
            {
                int swap = outputWidth;
                outputWidth = outputHeight;
                outputHeight = swap;
            }

            int quarterTurns = 0;
            if (this.IsRelevant(ConversionPreset.ConversionSettingKeys.ImageRotation))
            {
                quarterTurns = GetQuarterTurns(this.ConversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.ImageRotation));
            }

            // The preset rotation is applied after the resize: the thumbnail size is the size before that rotation.
            Debug.Log($"libvips: {width}x{height} (orientation {orientation}) -> {outputWidth}x{outputHeight}{(quarterTurns > 0 ? $", then rotated {quarterTurns * 90}°" : string.Empty)}.");

            // Thumbnail: shrink-on-load (JPEG / WebP decode at a fraction of the size), then resample, then autorotate.
            Image image = Image.Thumbnail(this.InputFilePath, outputWidth, outputHeight, size: Enums.Size.Force);
            try
            {
                if (quarterTurns > 0)
                {
                    // Rotating needs random access to the pixels.
                    Image rotated = image.CopyMemory().Rot(quarterTurns == 1 ? Enums.Angle.D90 : quarterTurns == 2 ? Enums.Angle.D180 : Enums.Angle.D270);
                    image.Dispose();
                    image = rotated;
                }

                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    image.SetProgress(new JobProgress(this, cancellation), cancellation.Token);
                    this.Save(image, keep);
                }
            }
            finally
            {
                image.Dispose();
            }
        }

        private void Save(Image image, Enums.ForeignKeep keep)
        {
            switch (this.ConversionPreset.OutputType)
            {
                case OutputType.Jpg:
                    // Same quality scale as ImageMagick (libjpeg tables); libvips uses 4:4:4 chroma at Q >= 90 like ImageMagick.
                    image.Jpegsave(this.OutputFilePath, q: this.GetQuality(), optimizeCoding: true, keep: keep);
                    break;

                case OutputType.Png:
                    // ImageMagick's PNG "quality 95" means zlib level 9 with adaptive filtering.
                    image.Pngsave(this.OutputFilePath, compression: 9, filter: Enums.ForeignPngFilter.All, keep: keep);
                    break;

                case OutputType.Webp:
                    image.Webpsave(this.OutputFilePath, q: this.GetQuality(), keep: keep);
                    break;

                case OutputType.Avif:
                    int avifQuality = Math.Max(1, this.GetQuality() + AvifQualityOffset);
                    image.Heifsave(this.OutputFilePath, q: avifQuality, compression: Enums.ForeignHeifCompression.Av1, effort: AvifEffort, keep: keep);
                    break;

                default:
                    throw new VipsUnsupportedException($"{this.ConversionPreset.OutputType} outputs are written by ImageMagick");
            }
        }

        private int GetQuality()
        {
            return this.ConversionPreset.GetSettingsValue<int>(ConversionPreset.ConversionSettingKeys.ImageQuality);
        }

        /// <summary>
        /// Same size computation as <see cref="ConversionJob_ImageMagick"/>: scale factor, then (after rotation, which
        /// doesn't change the result) the power-of-2 clamp and the maximum size, each keeping the aspect ratio.
        /// </summary>
        private void ComputeOutputSize(int width, int height, out int outputWidth, out int outputHeight)
        {
            double w = width;
            double h = height;

            if (this.IsRelevant(ConversionPreset.ConversionSettingKeys.ImageScale))
            {
                float scaleFactor = this.ConversionPreset.GetSettingsValue<float>(ConversionPreset.ConversionSettingKeys.ImageScale);
                if (Math.Abs(scaleFactor - 1f) >= 0.005f)
                {
                    w = Math.Max(1, Math.Round(w * scaleFactor, MidpointRounding.AwayFromZero));
                    h = Math.Max(1, Math.Round(h * scaleFactor, MidpointRounding.AwayFromZero));
                }
            }

            if (this.IsRelevant(ConversionPreset.ConversionSettingKeys.ImageClampSizePowerOf2) &&
                this.ConversionPreset.GetSettingsValue<bool>(ConversionPreset.ConversionSettingKeys.ImageClampSizePowerOf2))
            {
                double referenceSize = Math.Min(w, h);
                double size = 2;
                while (size * 2 <= referenceSize)
                {
                    size *= 2;
                }

                Fit(ref w, ref h, size, size);
            }

            if (this.IsRelevant(ConversionPreset.ConversionSettingKeys.ImageMaximumSize))
            {
                uint maximumSize = this.ConversionPreset.GetSettingsValue<uint>(ConversionPreset.ConversionSettingKeys.ImageMaximumSize);
                if (maximumSize > 0)
                {
                    Fit(ref w, ref h, Math.Min(w, maximumSize), Math.Min(h, maximumSize));
                }
            }

            outputWidth = (int)w;
            outputHeight = (int)h;
        }

        /// <summary>
        /// Fits w x h in a box keeping the aspect ratio, like ImageMagick's Scale(width, height).
        /// </summary>
        private static void Fit(ref double w, ref double h, double boxWidth, double boxHeight)
        {
            double factor = Math.Min(boxWidth / w, boxHeight / h);
            if (Math.Abs(factor - 1) < 1e-9)
            {
                return;
            }

            double fittedWidth = Math.Max(1, Math.Round(w * factor, MidpointRounding.AwayFromZero));
            double fittedHeight = Math.Max(1, Math.Round(h * factor, MidpointRounding.AwayFromZero));
            w = fittedWidth;
            h = fittedHeight;
        }

        /// <returns>The number of clockwise quarter turns (0 to 3), or -1 if the angle is not a multiple of 90°.</returns>
        private static int GetQuarterTurns(float rotationInDegrees)
        {
            double turns = rotationInDegrees / 90.0;
            double rounded = Math.Round(turns);
            if (Math.Abs(turns - rounded) * 90.0 >= 0.05)
            {
                return -1;
            }

            return (int)(((rounded % 4) + 4) % 4);
        }

        /// <summary>
        /// Throws <see cref="VipsUnsupportedException"/> for files that need ImageMagick, now that the header is known.
        /// </summary>
        private static void CheckSupported(Image header)
        {
            // This libvips build has no HEVC decoder: only AV1 HEIF (AVIF) is decoded.
            if (header.Contains("heif-compression") && (string)header.Get("heif-compression") != "av1")
            {
                throw new VipsUnsupportedException($"{header.Get("heif-compression")} HEIF images are read by ImageMagick");
            }

            if (header.Interpretation == Enums.Interpretation.Cmyk)
            {
                throw new VipsUnsupportedException("CMYK images are converted by ImageMagick");
            }
        }

        private bool IsRelevant(string settingsKey)
        {
            return this.ConversionPreset.IsRelevantSetting(settingsKey);
        }

        private void DeletePartialOutput()
        {
            try
            {
                if (File.Exists(this.OutputFilePath))
                {
                    File.Delete(this.OutputFilePath);
                }
            }
            catch (Exception exception)
            {
                Debug.Log($"Can't delete the partial output: {exception.Message}");
            }
        }

        /// <summary>
        /// A file libvips is able to read but that the ImageMagick conversion handles (not an error).
        /// </summary>
        private class VipsUnsupportedException : Exception
        {
            public VipsUnsupportedException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Reports libvips' progress (percent of the image computed) to the job, and cancels the computation when the
        /// user cancels the job.
        /// </summary>
        private class JobProgress : IProgress<int>
        {
            private readonly ConversionJob_Vips job;
            private readonly CancellationTokenSource cancellation;

            public JobProgress(ConversionJob_Vips job, CancellationTokenSource cancellation)
            {
                this.job = job;
                this.cancellation = cancellation;
            }

            public void Report(int percent)
            {
                if (this.job.CancelIsRequested)
                {
                    this.cancellation.Cancel();
                    return;
                }

                this.job.Progress = percent / 100f;
            }
        }
    }
}
