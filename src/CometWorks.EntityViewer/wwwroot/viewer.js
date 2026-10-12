// Both modes share the viewer shell. Navigation unloads the previous renderer,
// so scene and history never hold two WebGL contexts or asset sessions at once.
const params = new URLSearchParams(location.search);
const history = params.get("view") === "history";
for (const [id, mode] of [["sceneMode", "scene"], ["historyMode", "history"]]) {
    const url = new URL(location.href);
    url.searchParams.set("view", mode);
    const link = document.getElementById(id);
    link.href = url.href;
    if ((mode === "history") === history) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
}
if (!params.get("agentId") || !params.get("entityId")) {
    const sceneLink = document.getElementById("sceneMode");
    sceneLink.removeAttribute("href");
    sceneLink.setAttribute("aria-disabled", "true");
    sceneLink.title = "Open an entity from Quasar’s entity list to view its current scene.";
}
if (history) {
    document.getElementById("modeControls").replaceChildren(document.getElementById("historyControls").content.cloneNode(true));
    document.getElementById("sceneWorkspace").replaceWith(document.getElementById("historyWorkspace").content.cloneNode(true));
    document.querySelector(".lede").textContent = "Inspect recorded grids, destruction, nearby players, and incident evidence.";
}
try {
    await import(history ? "./replay.js" : "./main.js");
} catch (error) {
    document.documentElement.classList.remove("quasar-theme-pending");
    const notice = document.createElement("p");
    notice.setAttribute("role", "alert");
    notice.textContent = `Could not start the viewer: ${error.message}`;
    document.getElementById("modeControls").prepend(notice);
}
