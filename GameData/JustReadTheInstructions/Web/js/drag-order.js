const DRAG_THRESHOLD = 5;
const EDGE_ZONE = 80;
const MAX_EDGE_SPEED = 14;

export function enableDragOrder(container, onReorder) {
    let drag = null;

    const onMove = (e) => {
        if (!drag) return;

        if (!drag.active) {
            if (Math.hypot(e.clientX - drag.startX, e.clientY - drag.startY) < DRAG_THRESHOLD) return;
            drag.active = true;
            _activate(drag, e);
        }

        drag.curX = e.clientX;
        drag.curY = e.clientY;
        drag.card.style.left = `${e.clientX - drag.offsetX}px`;
        drag.card.style.top = `${e.clientY - drag.offsetY}px`;
        _movePlaceholder(container, drag.placeholder, e.clientX, e.clientY);

        const vy = e.clientY;
        const vh = window.innerHeight;
        if (vy < EDGE_ZONE) {
            drag.edgeSpeed = -MAX_EDGE_SPEED * (1 - vy / EDGE_ZONE);
        } else if (vy > vh - EDGE_ZONE) {
            drag.edgeSpeed = MAX_EDGE_SPEED * (1 - (vh - vy) / EDGE_ZONE);
        } else {
            drag.edgeSpeed = 0;
        }
        _tickEdgeScroll(drag, container);
    };

    const onUp = () => {
        if (!drag) return;
        cancelAnimationFrame(drag.edgeRaf);
        if (drag.active) {
            drag.card.removeAttribute('style');
            drag.placeholder.replaceWith(drag.card);
            onReorder();
        }
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onUp);
        drag = null;
    };

    container.addEventListener('pointerdown', (e) => {
        if (e.button != null && e.button !== 0) return;
        const card = e.target.closest('.camera-card');
        if (!card || !container.contains(card)) return;
        if (e.target.closest('button, a, input, select')) return;

        const isTouch = e.pointerType === 'touch';
        if (isTouch && !e.target.closest('.drag-handle')) return;

        e.preventDefault();

        drag = {
            card,
            placeholder: null,
            startX: e.clientX,
            startY: e.clientY,
            curX: e.clientX,
            curY: e.clientY,
            offsetX: 0,
            offsetY: 0,
            active: false,
            edgeSpeed: 0,
            edgeRaf: null,
        };

        window.addEventListener('pointermove', onMove, { passive: false });
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onUp);
    });
}

function _tickEdgeScroll(drag, container) {
    if (drag.edgeRaf) return;
    if (!drag.edgeSpeed) return;

    const loop = () => {
        if (!drag?.active || !drag.edgeSpeed) {
            drag && (drag.edgeRaf = null);
            return;
        }
        window.scrollBy(0, drag.edgeSpeed);
        _movePlaceholder(container, drag.placeholder, drag.curX, drag.curY);
        drag.edgeRaf = requestAnimationFrame(loop);
    };
    drag.edgeRaf = requestAnimationFrame(loop);
}

function _activate(drag, e) {
    const rect = drag.card.getBoundingClientRect();
    drag.offsetX = e.clientX - rect.left;
    drag.offsetY = e.clientY - rect.top;

    const ph = document.createElement('div');
    ph.className = 'drag-placeholder';
    ph.style.cssText = `width:${rect.width}px;height:${rect.height}px`;
    drag.card.replaceWith(ph);
    drag.placeholder = ph;

    drag.card.style.cssText = [
        `position:fixed`,
        `left:${rect.left}px`,
        `top:${rect.top}px`,
        `width:${rect.width}px`,
        `height:${rect.height}px`,
        `z-index:1000`,
        `opacity:0.88`,
        `pointer-events:none`,
        `box-shadow:0 12px 40px rgba(0,0,0,0.6)`,
        `transform:rotate(1.5deg)`,
    ].join(';');
    document.body.appendChild(drag.card);
}

function _movePlaceholder(container, placeholder, cx, cy) {
    const phRect = placeholder.getBoundingClientRect();
    if (cx >= phRect.left && cx <= phRect.right && cy >= phRect.top && cy <= phRect.bottom) return;

    const cards = [...container.querySelectorAll('.camera-card')];
    if (!cards.length) return;

    let best = null;
    let bestScore = Infinity;
    for (const c of cards) {
        const r = c.getBoundingClientRect();
        const dy = cy < r.top ? r.top - cy : cy > r.bottom ? cy - r.bottom : 0;
        const dx = cx < r.left ? r.left - cx : cx > r.right ? cx - r.right : 0;
        const score = dy * 4 + dx;
        if (score < bestScore) { bestScore = score; best = c; }
    }

    if (!best) return;
    const r = best.getBoundingClientRect();
    const insertBefore = cy < r.top + r.height / 2;
    insertBefore ? best.before(placeholder) : best.after(placeholder);
}
