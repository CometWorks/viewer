const LINKED_CLASS = "quasar-theme-linked";
const PENDING_CLASS = "quasar-theme-pending";
const THEME_EVENT = "quasar-entity-viewer-theme-changed";
const TOKEN_MAP = [
    ["--mud-palette-background", "--bg"],
    ["--mud-palette-surface", "--panel"],
    ["--mud-palette-drawer-background", "--panel-2"],
    ["--mud-palette-text-primary", "--text"],
    ["--mud-palette-text-secondary", "--muted"],
    ["--mud-palette-primary", "--accent"],
    ["--mud-palette-primary-text", "--button-text"],
    ["--mud-palette-error", "--danger"],
    ["--mud-palette-warning", "--warning"],
    ["--mud-palette-info", "--hint"],
    ["--mud-palette-lines-default", "--border"],
    ["--mud-default-borderradius", "--radius-md"],
    ["--mud-typography-default-family", "--viewer-font-family"],
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
    observer.observe(parentRoot, { attributes: true, attributeFilter: ["class", "style"] });
    if (parentRoot.ownerDocument.body) {
        observer.observe(parentRoot.ownerDocument.body, { attributes: true, attributeFilter: ["class", "style"] });
    }
    if (parentRoot.ownerDocument.head) {
        observer.observe(parentRoot.ownerDocument.head, { childList: true, subtree: true, characterData: true });
    }
    window.addEventListener("message", handleThemeMessage);
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
    for (const [mudToken, viewerToken] of TOKEN_MAP) {
        const value = source.getPropertyValue(mudToken).trim() || body?.getPropertyValue(mudToken).trim();
        if (!value) continue;

        root.style.setProperty(viewerToken, value);
        linked = true;
    }

    if (!linked) {
        root.classList.remove(LINKED_CLASS);
        return false;
    }

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
        return window.parent.document?.documentElement || null;
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
