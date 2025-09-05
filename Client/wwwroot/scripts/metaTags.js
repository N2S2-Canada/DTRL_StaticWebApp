window.setSeoTags = (title, description, imageUrl) => {
    // Title
    document.title = title;

    // Description
    setOrUpdate('name', 'description', description);

    // Open Graph
    setOrUpdate('property', 'og:title', title);
    setOrUpdate('property', 'og:description', description);
    setOrUpdate('property', 'og:image', imageUrl);
    setOrUpdate('property', 'og:type', 'website');
    setOrUpdate('property', 'og:url', window.location.href);

    // Twitter
    setOrUpdate('name', 'twitter:card', 'summary_large_image');
    setOrUpdate('name', 'twitter:title', title);
    setOrUpdate('name', 'twitter:description', description);
    setOrUpdate('name', 'twitter:image', imageUrl);
};

function setOrUpdate(attrName, attrValue, content) {
    let tag = document.querySelector(`meta[${attrName}="${attrValue}"]`);
    if (!tag) {
        tag = document.createElement('meta');
        tag.setAttribute(attrName, attrValue);
        document.head.appendChild(tag);
    }
    tag.setAttribute('content', content);
}
