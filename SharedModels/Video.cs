namespace SharedModels;

public class Video
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public List<string> Categories { get; set; } = [];
    public bool IsVideo { get; set; } // True if this is a video, false if photo
    public string ThumbnailUrl { get; set; } = string.Empty;
}
