// wwwroot/scripts/mediaModal.js

let modalInstance = null;

export function show() {
    const modalEl = document.getElementById('mediaModal');
    if (!modalEl) return;

    // Initialize Bootstrap modal if not already
    if (!modalInstance) {
        modalInstance = new bootstrap.Modal(modalEl, { backdrop: 'static', keyboard: true });
    }

    modalInstance.show();
}

export function hide() {
    const modalEl = document.getElementById('mediaModal');
    if (!modalEl) return;

    // Stop any playing video
    const video = modalEl.querySelector('video');
    if (video) {
        video.pause();
        video.currentTime = 0;
    }

    // Hide the modal
    if (modalInstance) {
        modalInstance.hide();
    }
}
