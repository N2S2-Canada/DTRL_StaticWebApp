namespace SharedModels
{
    public class Video
    {
        public string Name { get; set; } = "";
        public string? Url { get; set; }
        public string? ThumbnailUrl { get; set; }   // kept for back-compat (we'll set to Medium)
        public string[] Categories { get; set; } = Array.Empty<string>();
        public bool IsVideo { get; set; }

        // NEW:
        public string? ThumbnailSmall { get; set; }
        public string? ThumbnailMedium { get; set; }
        public string? ThumbnailLarge { get; set; }
    }
}
