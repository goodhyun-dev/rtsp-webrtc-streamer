using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace test_rtsp.Themes
{
    /// <summary>채널 색 인덱스 → 컬러 바 브러시 (Bar1 ~ Bar6).</summary>
    public sealed class ChannelBrushConverter : IValueConverter
    {
        public const int Count = 6;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int i = value is int n && n >= 0 ? n % Count : 0;
            return Application.Current.TryFindResource("Bar" + (i + 1)) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
