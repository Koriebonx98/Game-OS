using System.Windows;
using System.Windows.Controls;

namespace XboxShellApp
{
    public partial class GameAppTile : UserControl
    {
        public delegate void TileClickedHandler(object sender, RoutedEventArgs e);
        public event TileClickedHandler TileClicked;

        public GameAppTile()
        {
            InitializeComponent();
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            TileClicked?.Invoke(this, e);
        }
    }
}
