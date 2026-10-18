// <copyright file="SettingsService.Migration.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Services
{
    using System;
    using FileConverter.ConversionJobs;

    public partial class SettingsService
    {
        private void MigrateSettingsToCurrentVersion(Settings settings)
        {
            int settingsVersion = settings.SerializationVersion;

            if (settingsVersion <= 4)
            {
                // Hardware acceleration 'Auto' (version 5) is the new default. 'Off' was the previous default and was
                // written to every settings file, so it can't be told apart from a deliberate choice: switch it to
                // 'Auto', which falls back to the CPU encoder whenever a hardware encode fails.
                // Explicit CUDA / AMF choices are kept.
                if (settings.HardwareAccelerationMode == Helpers.HardwareAccelerationMode.Off)
                {
                    settings.HardwareAccelerationMode = Helpers.HardwareAccelerationMode.Auto;
                }
            }

            // Migrate conversion settings.
            if (settings.ConversionPresets != null)
            {
                foreach (ConversionPreset conversionPreset in settings.ConversionPresets)
                {
                    this.MigrateConversionPresetToCurrentVersion(conversionPreset, settingsVersion);
                }
            }
        }

        private void MigrateConversionPresetToCurrentVersion(ConversionPreset preset, int settingsVersion)
        {
            if (settingsVersion <= 2)
            {
                // Migrate video encoding speed.
                string videoEncodingSpeed = preset.GetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed);
                if (videoEncodingSpeed != null)
                {
                    VideoEncodingSpeed encodingSpeed;
                    if (!Enum.TryParse(videoEncodingSpeed, out encodingSpeed))
                    {
                        switch (videoEncodingSpeed)
                        {
                            case "Ultra Fast":
                                preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed, VideoEncodingSpeed.UltraFast.ToString());
                                break;

                            case "Super Fast":
                                preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed, VideoEncodingSpeed.SuperFast.ToString());
                                break;

                            case "Very Fast":
                                preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed, VideoEncodingSpeed.VeryFast.ToString());
                                break;

                            case "Very Slow":
                                preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoEncodingSpeed, VideoEncodingSpeed.VerySlow.ToString());
                                break;
                        }
                    }
                }
            }

            if (settingsVersion <= 3)
            {
                // Try to fix corrupted settings (GitHub issue #5).
                string scale = preset.GetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageScale);
                if (scale != null)
                {
                    preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.ImageScale, scale.Replace(',', '.'));
                }

                scale = preset.GetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoScale);
                if (scale != null)
                {
                    preset.SetSettingsValue(ConversionPreset.ConversionSettingKeys.VideoScale, scale.Replace(',', '.'));
                }
            }
        }
    }
}
