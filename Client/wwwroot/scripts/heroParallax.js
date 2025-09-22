window.setupHeroParallax = () => {
    const heroBg = document.getElementById('hero-bg');
    if (!heroBg) return;

    window.addEventListener('scroll', () => {
        const scrollY = window.scrollY;
        heroBg.style.transform = `translateY(${scrollY * 0.4}px)`; // adjust speed
    });
};

window.copyFromElement = async function (elementId) {
    const el = document.getElementById(elementId);
    if (!el) return false;
    const text = el.textContent?.trim();
    if (!text) return false;
    await navigator.clipboard.writeText(text);
    return true;
};
