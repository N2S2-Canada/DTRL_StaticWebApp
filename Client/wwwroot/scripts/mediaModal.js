// mediaModal.js
export function showModal() {
    const modalEl = document.getElementById('mediaModal');
    if (modalEl) {
        const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
        modal.show();
    }
}

export function hideModal() {
    const modalEl = document.getElementById('mediaModal');
    if (modalEl) {
        const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
        modal.hide();

        // Pause any playing video
        const video = modalEl.querySelector('video');
        if (video) {
            video.pause();
            video.currentTime = 0;
        }
    }
}

// Optional: particle init function
export function init() {
    // Initialize your particles.js / canvas logic here
}
