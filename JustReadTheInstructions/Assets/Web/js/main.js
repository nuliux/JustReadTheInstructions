import { CAMERA_SYNC_MS } from './config.js';
import { fetchCameras } from './api.js';
import { CameraCard } from './camera-card.js';
import { mountSettingsUI } from './settings-ui.js';

const cards = new Map();

function setStatus(id, message) {
    const el = document.getElementById(id);
    if (!el) return;
    if (message) {
        el.textContent = message;
        el.classList.add('visible');
    } else {
        el.classList.remove('visible');
    }
}

async function sync() {
    let cameras;
    try {
        cameras = await fetchCameras();
        setStatus('error', null);
    } catch {
        setStatus('error', 'Could not connect to KSP. Is the game running?');
        setStatus('empty', null);
        return;
    }

    setStatus('empty', cameras.length === 0
        ? 'No cameras open. Open or stream a hull camera in KSP first.'
        : null);

    const container = document.getElementById('cameras');
    const incomingIds = new Set(cameras.map((c) => c.id));

    for (const [id, card] of cards) {
        if (!incomingIds.has(id)) {
            card.dispose();
            cards.delete(id);
        }
    }

    for (const cam of cameras) {
        const existing = cards.get(cam.id);
        if (existing) {
            existing.update(cam);
        } else {
            const card = new CameraCard(cam);
            cards.set(cam.id, card);
            container.appendChild(card.el);
        }
    }
}

function wireLifecycle() {
    const finalize = () => {
        for (const card of cards.values()) card.emergencyFinalize();
    };
    window.addEventListener('pagehide', finalize);
    window.addEventListener('beforeunload', finalize);
}

function main() {
    mountSettingsUI();
    wireLifecycle();
    sync();
    setInterval(sync, CAMERA_SYNC_MS);
}

main();