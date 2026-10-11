import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = readFileSync(new URL("../src/CometWorks.EntityViewer/wwwroot/theme.js", import.meta.url), "utf8").replaceAll("export function", "function");
function fixture({ native = true, embedded = true } = {}) {
    const values = new Map(), classes = new Set(), listeners = new Map();
    const root = { style: { setProperty: (k, v) => values.set(k, v) }, classList: { add: c => classes.add(c), remove: c => classes.delete(c) } };
    const hostValues = new Map(native ? [["--quasar-palette-background", "#18191b"], ["--quasar-palette-primary", "#00c0fd"], ["--quasar-palette-primary-contrast-text", "#10212b"]]
        : [["--mud-palette-background", "#fafafa"], ["--mud-palette-primary", "#123456"], ["--mud-palette-primary-text", "#ffffff"]]);
    const parentRoot = { ownerDocument: { body: {}, head: {} } };
    const shell = { ownerDocument: parentRoot.ownerDocument };
    let observer;
    const win = { frameElement: { closest: () => native ? shell : null }, addEventListener: (name, fn) => listeners.set(name, fn),
        removeEventListener: name => listeners.delete(name), dispatchEvent() {} };
    win.parent = embedded ? { document: { documentElement: parentRoot } } : win;
    const context = { window: win, document: { documentElement: root }, CustomEvent: class {},
        MutationObserver: class { constructor(fn) { this.notify = fn; observer = this; this.targets = []; }
            observe(target, options) { this.targets.push({ target, options }); } disconnect() { this.disconnected = true; } },
        getComputedStyle: element => ({ fontFamily: "system-ui", getPropertyValue: key => (element === root ? values : hostValues).get(key) || "" }) };
    runInNewContext(source, context);
    return { context, values, classes, listeners, shell, hostValues, get observer() { return observer; } };
}

test("native shell palette takes priority and updates scene tokens on appearance changes", () => {
    const f = fixture();
    f.hostValues.set("--mud-palette-primary", "#ff0000");
    f.context.startQuasarThemeSync();
    assert.equal(f.values.get("--accent"), "#00c0fd");
    assert.equal(f.values.get("--button-text"), "#10212b");
    assert.equal(f.values.get("--viewer-color-scheme"), "dark");
    assert.equal(f.observer.targets[0].target, f.shell);
    assert.ok(f.observer.targets[0].options.attributeFilter.includes("data-theme"));
    f.hostValues.set("--quasar-palette-background", "#ffffff");
    f.hostValues.set("--quasar-palette-primary", "#0066aa");
    f.observer.notify();
    assert.equal(f.values.get("--accent"), "#0066aa");
    assert.equal(f.values.get("--viewer-color-scheme"), "light");
    f.listeners.get("pagehide")();
    assert.equal(f.observer.disconnected, true);
    assert.equal(f.listeners.size, 0);
});

test("legacy host keeps its palette while the Mud-free viewer is deployed during migration", () => {
    const f = fixture({ native: false });
    f.context.startQuasarThemeSync();
    assert.equal(f.values.get("--accent"), "#123456");
    assert.equal(f.values.get("--button-text"), "#ffffff");
    assert.equal(f.values.get("--viewer-color-scheme"), "light");
});

test("standalone viewer reveals its fallback theme without observing a parent", () => {
    const f = fixture({ embedded: false });
    f.context.startQuasarThemeSync();
    assert.equal(f.classes.has("quasar-theme-linked"), false);
    assert.equal(f.observer, undefined);
    assert.equal(f.values.size, 0);
});
