using System.Windows;
using System.Windows.Controls;

namespace AdminControlPanel.Controls
{
    public partial class TimeSliderControl : UserControl
    {
        public TimeSliderControl()
        {
            InitializeComponent();
        }

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CurrentTimeText != null)
            {
                var minutes = (int)e.NewValue;
                var hours = minutes / 60;
                var mins = minutes % 60;
                
                if (hours > 0)
                    CurrentTimeText.Text = mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
                else
                    CurrentTimeText.Text = $"{minutes}m";
            }
        }

        private void QuickTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int minutes))
            {
                TimeSlider.Value = minutes;
            }
        }
    }
}