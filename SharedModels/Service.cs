namespace SharedModels
{
    public class Service
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;

        public List<ServiceSection> Sections { get; set; } = new();
    }

    public class ServiceSection
    {
        public string Title { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Rich HTML body for the section (safe, trusted content you author).
        /// </summary>
        public string BodyHtml { get; set; } = string.Empty;
    }
}
