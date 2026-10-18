using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AdminControlPanel.Services;

namespace AdminControlPanel.Controls
{
    public partial class UserProfileCard : UserControl
    {
        private DeviceUser? _user;
        
        public UserProfileCard()
        {
            InitializeComponent();
        }
        
        public UserProfileCard(DeviceUser user) : this()
        {
            _user = user;
            LoadUserData();
        }
        
        private void LoadUserData()
        {
            if (_user == null) return;
            
            // Update UI elements with user data
            if (FindName("UserName") is TextBlock nameBlock)
                nameBlock.Text = _user.DisplayName;
                
            if (FindName("UserAge") is TextBlock ageBlock)
                ageBlock.Text = _user.AgeText;
                
            if (FindName("StatusText") is TextBlock statusBlock)
                statusBlock.Text = _user.StatusText;
                
            if (FindName("TimeRemaining") is TextBlock timeBlock)
                timeBlock.Text = _user.TimeRemainingText;
                
            if (FindName("StatusIndicator") is Ellipse indicator)
            {
                indicator.Fill = _user.StatusColor switch
                {
                    "Green" => Brushes.Green,
                    "Red" => Brushes.Red,
                    "Gray" => Brushes.Gray,
                    _ => Brushes.Gray
                };
            }
            
            // Update progress bar if exists
            if (FindName("UsageProgressBar") is System.Windows.Controls.ProgressBar progressBar && _user.DailyLimitMinutes.HasValue)
            {
                var percentage = (_user.TodayMinutesUsed / (double)_user.DailyLimitMinutes.Value) * 100;
                progressBar.Value = Math.Min(100, Math.Max(0, percentage));
            }
        }

        private void ExtendTime_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Extend time functionality - TODO: Implement");
        }

        private void HomeworkMode_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Homework mode functionality - TODO: Implement");
        }

        private void BlockNow_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Block now functionality - TODO: Implement");
        }
    }
}