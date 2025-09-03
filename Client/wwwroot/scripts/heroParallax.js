window.setupHeroParallax = () => {
    const heroBg = document.getElementById('hero-bg');
    if (!heroBg) return;

    window.addEventListener('scroll', () => {
        const scrollY = window.scrollY;
        heroBg.style.transform = `translateY(${scrollY * 0.4}px)`; // adjust speed
    });
};
