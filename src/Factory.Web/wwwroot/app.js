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

    // Reload buttons (<button data-reload>) are delegated, because the content security policy allows no inline handlers.
    document.addEventListener("click", (event) => {
        if (event.target.closest?.("[data-reload]")) location.reload();
    });

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

    // A screenshot pasted or dropped onto a report form becomes a choice in the form's file input, so Blazor reads it like any chosen file.
    function pictures(transfer) {
        return [...(transfer?.files ?? [])].filter((file) => file.type.startsWith("image/"));
    }

    function attach(form, files) {
        const input = form.querySelector("input[type=file]");
        if (!input || files.length === 0) return false;
        const transfer = new DataTransfer();
        for (const file of files) transfer.items.add(file);
        input.files = transfer.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
        return true;
    }

    document.addEventListener("paste", (event) => {
        const form = event.target.closest?.("[data-paste-images]");
        if (form && attach(form, pictures(event.clipboardData))) event.preventDefault();
    });

    document.addEventListener("dragover", (event) => {
        const form = event.target.closest?.("[data-paste-images]");
        if (!form || ![...(event.dataTransfer?.types ?? [])].includes("Files")) return;
        event.preventDefault();
        form.classList.add("drop-target");
    });

    document.addEventListener("dragleave", (event) => {
        event.target.closest?.("[data-paste-images]")?.classList.remove("drop-target");
    });

    document.addEventListener("drop", (event) => {
        const form = event.target.closest?.("[data-paste-images]");
        if (!form) return;
        form.classList.remove("drop-target");
        if (attach(form, pictures(event.dataTransfer))) event.preventDefault();
    });

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
