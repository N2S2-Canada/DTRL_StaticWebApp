using Microsoft.JSInterop;
using System.Threading.Tasks;

namespace Client.Services
{
    public class MetaTagService
    {
        private readonly IJSRuntime _js;
        private const string DefaultOgImage = "/images/og-default.webp";

        public MetaTagService(IJSRuntime js)
        {
            _js = js;
        }

        /// <summary>
        /// Sets <title>, meta description, Open Graph, and Twitter Card tags.
        /// </summary>
        public Task SetSeoTagsAsync(string title, string description, string imageUrl)
            => _js.InvokeVoidAsync(
                "setSeoTags",
                title,
                description,
                string.IsNullOrEmpty(imageUrl) ? DefaultOgImage : imageUrl
            ).AsTask();
    }
}
