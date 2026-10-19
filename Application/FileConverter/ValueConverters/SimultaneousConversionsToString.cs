// <copyright file="SimultaneousConversionsToString.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ValueConverters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Displays the <c>MaximumNumberOfSimultaneousConversions</c> setting: 0 means automatic (one job per physical core).
    /// </summary>
    public class SimultaneousConversionsToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = value is double number ? (int)Math.Round(number) : value is int integer ? integer : 0;
            if (count <= 0)
            {
                return string.Format(Properties.Resources.SimultaneousConversionsAuto, Hardware.SystemInfo.PhysicalCoreCount);
            }

            return count.ToString(culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
