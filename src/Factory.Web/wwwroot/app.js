// Small DOM behaviours that don't need a server round trip.
(function () {
    const root = document.documentElement;
    const storageKey = "hatchet.layout";

    function loadLayout() {
        try {
            return JSON.parse(localStorage.getItem(storageKey)) || {};
        } catch {
            return {};
        }
    }

    function saveLayout(layout) {
        try {
            localStorage.setItem(storageKey, JSON.stringify(layout));
        } catch {
            // The layout just won't be remembered.
        }
    }

    const layout = loadLayout();
    for (const [name, value] of Object.entries(layout)) {
        if (name.startsWith("--")) root.style.setProperty(name, value);
        else root.setAttribute(name, value);
    }

    // Pane resizing: <div data-resize="--left-w" data-axis="x" data-sign="1" data-min="180" data-max="600">
    document.addEventListener("pointerdown", (event) => {
        const handle = event.target.closest?.("[data-resize]");
        if (!handle) return;
        event.preventDefault();

        const name = handle.dataset.resize;
        const horizontal = handle.dataset.axis !== "y";
        const sign = Number(handle.dataset.sign || 1);
        const min = Number(handle.dataset.min || 120);
        const max = Number(handle.dataset.max || 900);
        const start = horizontal ? event.clientX : event.clientY;
        const initial = parseFloat(getComputedStyle(root).getPropertyValue(name)) || min;

        handle.classList.add("dragging");
        root.classList.add(horizontal ? "resizing-x" : "resizing-y");

        function move(moveEvent) {
            const position = horizontal ? moveEvent.clientX : moveEvent.clientY;
            const size = Math.min(max, Math.max(min, initial + (position - start) * sign));
            root.style.setProperty(name, `${size}px`);
        }

        function up() {
            document.removeEventListener("pointermove", move);
            document.removeEventListener("pointerup", up);
            handle.classList.remove("dragging");
            root.classList.remove("resizing-x", "resizing-y");
            layout[name] = root.style.getPropertyValue(name);
            saveLayout(layout);
        }

        document.addEventListener("pointermove", move);
        document.addEventListener("pointerup", up);
    });

    // VS Code's panel shortcuts: Ctrl+B for the side pane, Ctrl+J for the bottom panel, Ctrl+Alt+B for the agent pane.
    function toggle(attribute) {
        const hidden = root.getAttribute(attribute) === "hidden";
        root.setAttribute(attribute, hidden ? "shown" : "hidden");
        layout[attribute] = root.getAttribute(attribute);
        saveLayout(layout);
    }

    document.addEventListener("keydown", (event) => {
        if (!(event.ctrlKey || event.metaKey)) return;
        const key = event.key.toLowerCase();
        if (key === "b" && event.altKey) {
            event.preventDefault();
            toggle("data-agent-pane");
        } else if (key === "b") {
            event.preventDefault();
            toggle("data-side-pane");
        } else if (key === "j") {
            event.preventDefault();
            toggle("data-bottom-panel");
        }
    });

    document.addEventListener("click", (event) => {
        const toggleButton = event.target.closest?.("[data-toggle]");
        if (toggleButton) toggle(toggleButton.dataset.toggle);
    });

    // Live streams stay pinned to the bottom until the reader scrolls up.
    const unpinned = new WeakSet();
    let lastUserInput = 0;
    let pointerHeld = false;

    for (const type of ["wheel", "touchmove", "keydown"]) {
        document.addEventListener(type, () => { lastUserInput = performance.now(); }, { capture: true, passive: true });
    }
    document.addEventListener("pointerdown", () => { pointerHeld = true; }, true);
    document.addEventListener("pointerup", () => { pointerHeld = false; }, true);

    document.addEventListener("scroll", (event) => {
        const element = event.target;
        if (!(element instanceof Element) || !element.hasAttribute("data-stick-bottom")) return;
        const atBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 40;
        if (atBottom) {
            unpinned.delete(element);
            return;
        }
        // Content changing size also fires scroll events, and those mustn't count as the reader scrolling away.
        if (pointerHeld || performance.now() - lastUserInput < 400) unpinned.add(element);
    }, true);

    let scheduled = false;

    new MutationObserver(() => {
        if (scheduled) return;
        scheduled = true;
        setTimeout(() => {
            scheduled = false;
            for (const element of document.querySelectorAll("[data-stick-bottom]")) {
                if (!unpinned.has(element)) element.scrollTop = element.scrollHeight;
            }
        }, 50);
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true });

    window.hatchet = {
        setTitle: (title) => {
            document.title = title;
        },
        focus: (selector) => {
            const element = document.querySelector(selector);
            if (element && document.activeElement !== element && !element.contains(document.activeElement)) element.focus();
        },
        copy: async (text) => {
            await navigator.clipboard.writeText(text);
        }
    };
})();
