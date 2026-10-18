// <copyright file="VideoResolutionToString.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ValueConverters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Displays a <c>VideoResolution</c> preset setting: 0 means "use the scale factor", otherwise e.g. "720p".
    /// </summary>
    public class VideoResolutionToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int resolution && resolution > 0)
            {
                return $"{resolution}p";
            }

            return Properties.Resources.VideoResolutionScaleFactorName;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
