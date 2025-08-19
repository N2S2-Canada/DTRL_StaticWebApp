namespace SharedModels;

public class Video
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public List<string> Categories { get; set; } = [];
}
