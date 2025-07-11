namespace XboxShellApp
{
    public class GameAppTileVM
    {
        public string Name { get; set; }
        public string Exe { get; set; }
        public string ImagePath { get; set; }
        public string Folder { get; set; }
        public bool IsGame { get; set; }
        public bool IsApp { get; set; }
        public bool IsMusic { get; set; }
        public bool IsPicture { get; set; }
        public bool IsVideo { get; set; }
        public bool IsRepack { get; set; }
        public string TypeLabel
        {
            get
            {
                if (IsGame) return "Game";
                if (IsRepack) return "Ready to Install";
                if (IsApp) return "App";
                if (IsMusic) return "Music";
                if (IsPicture) return "Picture";
                if (IsVideo) return "Video";
                return "Unknown";
            }
        }
    }
}
