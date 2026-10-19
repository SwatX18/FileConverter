// <copyright file="SimultaneousConversionsToString.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ValueConverters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Displays a simultaneous conversions setting: 0 means automatic. The automatic value is one job per physical core,
    /// or the automatic software video limit when the parameter is "CpuVideo".
    /// </summary>
    public class SimultaneousConversionsToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = value is double number ? (int)Math.Round(number) : value is int integer ? integer : 0;
            if (count <= 0)
            {
                int cores = Hardware.SystemInfo.PhysicalCoreCount;
                int automatic = parameter as string == "CpuVideo" ? Services.ConversionLimits.GetAutomaticCpuVideoLimit(cores) : cores;
                return string.Format(Properties.Resources.SimultaneousConversionsAuto, automatic);
            }

            return count.ToString(culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
