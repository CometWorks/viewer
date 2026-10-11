const LINKED_CLASS = "quasar-theme-linked";
const PENDING_CLASS = "quasar-theme-pending";
const THEME_EVENT = "quasar-entity-viewer-theme-changed";
const TOKEN_MAP = [
    ["--quasar-palette-background", "--bg"],
    ["--quasar-palette-surface", "--panel"],
    ["--quasar-palette-drawer-background", "--panel-2"],
    ["--quasar-palette-text-primary", "--text"],
    ["--quasar-palette-text-secondary", "--muted"],
    ["--quasar-palette-primary", "--accent"],
    ["--quasar-palette-primary-contrast-text", "--button-text"],
    ["--quasar-palette-error", "--danger"],
    ["--quasar-palette-warning", "--warning"],
    ["--quasar-palette-info", "--hint"],
    ["--quasar-palette-lines-default", "--border"],
        ];

let colorCanvas;
let colorContext;
let themeReady = false;

export function startQuasarThemeSync() {
    const parentRoot = parentDocumentRoot();
    applyQuasarTheme(parentRoot);
    revealTheme();
    if (!parentRoot) return;

    const observer = new MutationObserver(() => applyQuasarTheme(parentRoot));
    observer.observe(parentRoot, { attributes: true, attributeFilter: ["class", "style", "data-theme"] });
    if (parentRoot.ownerDocument.body) {
        observer.observe(parentRoot.ownerDocument.body, { attributes: true, attributeFilter: ["class", "style", "data-theme"] });
    }
    if (parentRoot.ownerDocument.head) {
        observer.observe(parentRoot.ownerDocument.head, { childList: true, subtree: true, characterData: true });
    }
    window.addEventListener("message", handleThemeMessage);
    const stop = () => {
        observer.disconnect();
        window.removeEventListener("message", handleThemeMessage);
        window.removeEventListener("pagehide", stop);
    };
    window.addEventListener("pagehide", stop, { once: true });
    return stop;
}

export function themeColor(variableName, fallback) {
    const value = getComputedStyle(document.documentElement).getPropertyValue(variableName).trim();
    return cssColorToNumber(value, fallback);
}

function handleThemeMessage(event) {
    if (event.source !== window.parent) return;
    if (!event.data || event.data.type !== "quasar-theme-changed") return;
    applyQuasarTheme(parentDocumentRoot());
}

function applyQuasarTheme(parentRoot) {
    const root = document.documentElement;
    if (!parentRoot) {
        root.classList.remove(LINKED_CLASS);
        return false;
    }

    const source = getComputedStyle(parentRoot);
    const body = parentRoot.ownerDocument.body ? getComputedStyle(parentRoot.ownerDocument.body) : null;
    let linked = false;
    for (const [hostToken, viewerToken] of TOKEN_MAP) {
        const legacyToken = hostToken === "--quasar-palette-primary-contrast-text"
            ? "--mud-palette-primary-text" : hostToken.replace("--quasar-", "--mud-");
        const value = source.getPropertyValue(hostToken).trim() || body?.getPropertyValue(hostToken).trim()
            || source.getPropertyValue(legacyToken).trim() || body?.getPropertyValue(legacyToken).trim();
        if (!value) continue;

        root.style.setProperty(viewerToken, value);
        linked = true;
    }

    if (!linked) {
        root.classList.remove(LINKED_CLASS);
        return false;
    }

    root.style.setProperty("--viewer-font-family", source.fontFamily || "system-ui, sans-serif");
    root.style.setProperty("--radius-md", "6px");
    const background = getComputedStyle(root).getPropertyValue("--bg").trim();
    root.style.setProperty("--viewer-color-scheme", isDarkColor(background) ? "dark" : "light");
    root.classList.add(LINKED_CLASS);
    window.dispatchEvent(new CustomEvent(THEME_EVENT));
    return true;
}

function revealTheme() {
    if (themeReady) return;

    themeReady = true;
    document.documentElement.classList.remove(PENDING_CLASS);
}

function parentDocumentRoot() {
    try {
        if (!window.parent || window.parent === window) return null;
        return window.frameElement?.closest(".quasar-shell, .quasar-preview")
            || window.parent.document?.documentElement || null;
    } catch {
        return null;
    }
}

function isDarkColor(value) {
    const rgb = cssColorToRgb(value);
    if (!rgb) return true;

    return (rgb.r * 0.299 + rgb.g * 0.587 + rgb.b * 0.114) < 150;
}

function cssColorToNumber(value, fallback) {
    const rgb = cssColorToRgb(value);
    if (!rgb) return fallback;

    return rgb.r << 16 | rgb.g << 8 | rgb.b;
}

function cssColorToRgb(value) {
    if (!value) return null;

    const parsed = parseRgb(value) || parseHex(value) || parseCanvasColor(value);
    if (!parsed) return null;

    return {
        r: clampByte(parsed.r),
        g: clampByte(parsed.g),
        b: clampByte(parsed.b),
    };
}

function parseRgb(value) {
    const match = value.match(/^rgba?\(([^)]+)\)$/i);
    if (!match) return null;

    const channels = match[1].split(/[,\s/]+/).filter(Boolean);
    if (channels.length < 3) return null;

    return {
        r: parseChannel(channels[0]),
        g: parseChannel(channels[1]),
        b: parseChannel(channels[2]),
    };
}

function parseHex(value) {
    const match = value.match(/^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i);
    if (!match) return null;

    const hex = match[1];
    if (hex.length === 3) {
        return {
            r: parseInt(hex[0] + hex[0], 16),
            g: parseInt(hex[1] + hex[1], 16),
            b: parseInt(hex[2] + hex[2], 16),
        };
    }

    return {
        r: parseInt(hex.slice(0, 2), 16),
        g: parseInt(hex.slice(2, 4), 16),
        b: parseInt(hex.slice(4, 6), 16),
    };
}

function parseCanvasColor(value) {
    if (!document.createElement) return null;

    colorCanvas ||= document.createElement("canvas");
    colorCanvas.width = 1;
    colorCanvas.height = 1;
    colorContext ||= colorCanvas.getContext("2d");
    if (!colorContext) return null;

    const initial = "#010203";
    colorContext.fillStyle = initial;
    colorContext.fillStyle = value;
    const normalized = colorContext.fillStyle;
    if (!normalized || normalized === initial) {
        return null;
    }

    return parseHex(normalized) || parseRgb(normalized);
}

function parseChannel(value) {
    if (value.endsWith("%")) return Number(value.slice(0, -1)) * 2.55;
    return Number(value);
}

function clampByte(value) {
    return Math.max(0, Math.min(255, Math.round(Number(value) || 0)));
}
