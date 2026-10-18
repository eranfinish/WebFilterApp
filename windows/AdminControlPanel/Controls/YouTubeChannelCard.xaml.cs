using System.Windows;
using System.Windows.Controls;

namespace AdminControlPanel.Controls
{
    public partial class YouTubeChannelCard : UserControl
    {
        public YouTubeChannelCard()
        {
            InitializeComponent();
        }

        private void AllowButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Allow channel functionality - TODO: Implement");
        }

        private void BlockButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Block channel functionality - TODO: Implement");
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Preview channel functionality - TODO: Implement");
        }
    }
}