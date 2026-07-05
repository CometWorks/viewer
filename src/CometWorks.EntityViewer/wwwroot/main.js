import { cacheElements, els, state } from "./state.js";
import { initScene, animate, disposeViewer } from "./scene.js";
import { configureContextControl, configureVoxelControl, wireControls } from "./controls.js";
import { fetchEntityScene, parseContextFlag, parseVoxelFlag } from "./quasar-api.js";
import { clearAssetFolderCaches, getFileAccessSupport, getSavedContentFolderName, getSavedModsFolderName, pickContentFolder, pickModsFolder, restoreContentFolder, restoreModsFolder, warnIfUsingBackupFolderAccess } from "./content-folder.js";
import { fetchAssetStreamingStatus, prepareRemoteAssetSession } from "./asset-streaming.js";
import { renderEntityScene } from "./entity-renderer.js";
import { downloadLog, log } from "./logging.js";
import { startQuasarThemeSync } from "./theme.js";

let serverAssetStreamingReady = false;
let remoteAssetSessionChecked = false;
let remoteAssetSessionFailed = false;
let remoteAssetSessionActive = false;

document.addEventListener("DOMContentLoaded", start);
window.addEventListener("pagehide", () => {
    disposeViewer();
}, { once: true });
window.addEventListener("pageshow", event => {
    if (event.persisted && state.viewerDisposed) window.location.reload();
});

async function start() {
    startQuasarThemeSync();
    cacheElements();
    showLoading("Loading scene", "Initializing renderer...");
    state.voxelSupport = parseVoxelFlag();
    state.contextSupport = parseContextFlag();
    initScene();
    wireControls({ reloadScene, pickContent: selectContentFolder, pickMods: selectModsFolder });
    els.downloadLog.addEventListener("click", downloadLog);
    animate();

    await refreshAssetStreamingStatus();

    try {
        showLoading("Loading scene", "Restoring saved Content folder...");
        const restored = await restoreContentFolder();
        updateContentStatus(restored ? `Using saved Content folder: ${state.contentFolderName}` : savedContentFolderStatus());
    } catch (error) {
        updateContentStatus(savedContentFolderStatus());
        log(`Could not restore Content folder: ${error.message}`, true);
    }

    try {
        showLoading("Loading scene", "Restoring saved Mods folder...");
        const restored = await restoreModsFolder();
        updateModsStatus(restored ? `Using saved Mods folder: ${state.modsFolderName}` : savedModsFolderStatus());
    } catch (error) {
        updateModsStatus(savedModsFolderStatus());
        log(`Could not restore Mods folder: ${error.message}`, true);
    }

    await reloadScene();
}

async function refreshAssetStreamingStatus() {
    try {
        const status = await fetchAssetStreamingStatus();
        applyAssetStreamingStatus(status);
    } catch (error) {
        serverAssetStreamingReady = false;
        remoteAssetSessionChecked = false;
        remoteAssetSessionActive = false;
        remoteAssetSessionFailed = true;
        updateAssetStreamingStatus("Server asset streaming status unavailable. Local folders remain active.", true);
        updateLocalAssetFallbackVisibility();
        log(error.message, true);
    }
}

function applyAssetStreamingStatus(status) {
    serverAssetStreamingReady = !!status?.streamingEnabled && !!status?.fileStreamingReady;
    remoteAssetSessionChecked = false;
    remoteAssetSessionActive = false;
    remoteAssetSessionFailed = false;
    const message = String(status?.message || "").trim() || "Local folders remain active.";
    updateAssetStreamingStatus(message, false);
    updateLocalAssetFallbackVisibility();
}

async function reloadScene() {
    els.reloadScene.disabled = true;
    showLoading("Loading scene", "Requesting scene snapshot...");
    try {
        state.timings = {};
        state.voxelSupport = parseVoxelFlag();
        state.contextSupport = parseContextFlag();
        configureVoxelControl();
        configureContextControl();
        log(`Requesting scene snapshot ${state.contextSupport.enabled ? "with" : "without"} context and ${state.voxelSupport.enabled ? "with" : "without"} voxel data from Quasar.`);
        const fetchStart = performance.now();
        const scene = await fetchEntityScene();
        addTiming("sceneSnapshotFetch", performance.now() - fetchStart);
        await prepareRemoteAssets(scene);
        await renderEntityScene(scene, { onProgress: updateLoadingProgress });
        const firstVoxel = scene.voxels && scene.voxels[0];
        log(`Loaded scene ${scene.grid && scene.grid.id || firstVoxel && (firstVoxel.displayName || firstVoxel.id) || "unknown"}.`);
        hideLoading();
    } catch (error) {
        showLoading("Could not load scene", error.message, { error: true });
        log(error.message, true);
    } finally {
        els.reloadScene.disabled = false;
    }
}

async function prepareRemoteAssets(scene) {
    try {
        const result = await prepareRemoteAssetSession(scene);
        if (result.changed) clearAssetFolderCaches();
        remoteAssetSessionChecked = true;
        remoteAssetSessionActive = !!result.active;
        remoteAssetSessionFailed = serverAssetStreamingReady && !result.active;
        if (result.active) {
            updateAssetStreamingStatus("Server asset streaming session ready. Local asset folders are hidden.", false);
            log("Server asset streaming session ready.");
        }
        updateLocalAssetFallbackVisibility();
    } catch (error) {
        remoteAssetSessionChecked = true;
        remoteAssetSessionActive = false;
        remoteAssetSessionFailed = true;
        clearAssetFolderCaches();
        updateAssetStreamingStatus("Server asset streaming unavailable. Local folders remain active.", true);
        updateLocalAssetFallbackVisibility();
        log(`Server asset streaming unavailable: ${error.message}`, true);
    }
}

function updateLoadingProgress(progress) {
    showLoading(progress.title || "Loading scene", progress.text || "Preparing scene...", progress);
}

function showLoading(title, text, options = {}) {
    if (!els.loadingOverlay) return;
    els.loadingOverlay.classList.remove("is-hidden");
    els.loadingTitle.textContent = title;
    els.loadingText.textContent = text;
    els.loadingOverlay.classList.toggle("is-error", !!options.error);
    const progress = els.loadingProgress;
    if (!progress) return;

    const value = Number(options.value);
    const max = Number(options.max);
    if (Number.isFinite(value) && Number.isFinite(max) && max > 0) {
        progress.classList.remove("is-indeterminate");
        progress.style.width = `${Math.max(0, Math.min(100, value / max * 100))}%`;
    } else {
        progress.style.width = "";
        progress.classList.add("is-indeterminate");
    }
}

function hideLoading() {
    if (els.loadingOverlay) els.loadingOverlay.classList.add("is-hidden");
}

function updateFileAccessWarning(forceHidden = false) {
    if (!els.fileAccessWarning) return;
    if (forceHidden) {
        els.fileAccessWarning.hidden = true;
        return;
    }

    const support = getFileAccessSupport();
    els.fileAccessWarning.hidden = support.persistent;
    if (!support.persistent && els.fileAccessWarningDetail) {
        els.fileAccessWarningDetail.textContent = support.warning;
    }
}

function savedContentFolderStatus() {
    const name = getSavedContentFolderName();
    const support = getFileAccessSupport();
    if (!support.persistent) {
        return name
            ? `Backup folder picker active. Last Content folder: ${name}. Select it again after reload.`
            : "Backup folder picker active. Select Content folder for this session.";
    }
    return name ? `Last saved Content folder: ${name}. Select it again to grant browser access.` : "No Content folder selected.";
}

function savedModsFolderStatus() {
    const name = getSavedModsFolderName();
    const support = getFileAccessSupport();
    if (!support.persistent) {
        return name
            ? `Backup folder picker active. Last Mods folder: ${name}. Select it again after reload.`
            : "Backup folder picker active. Select Mods folder for this session.";
    }
    return name ? `Last saved Mods folder: ${name}. Select it again to grant browser access.` : "No Mods folder selected.";
}

async function selectModsFolder() {
    els.pickMods.disabled = true;
    try {
        const handle = await pickModsFolder();
        updateModsStatus(`Using Mods folder: ${handle.name || "Mods"}`);
        if (state.lastScene) {
            showLoading("Reloading assets", "Preparing scene with selected Mods folder...");
            await renderEntityScene(state.lastScene, { onProgress: updateLoadingProgress });
            hideLoading();
        }
    } catch (error) {
        if (error.name === "AbortError") return;
        showLoading("Could not reload assets", error.message, { error: true });
        updateModsStatus(error.message, true);
        log(error.message, true);
    } finally {
        els.pickMods.disabled = false;
    }
}

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
}

async function selectContentFolder() {
    els.pickContent.disabled = true;
    try {
        const handle = await pickContentFolder();
        updateContentStatus(`Using Content folder: ${handle.name || "Content"}`);
        if (state.lastScene) {
            showLoading("Reloading assets", "Preparing scene with selected Content folder...");
            await renderEntityScene(state.lastScene, { onProgress: updateLoadingProgress });
            hideLoading();
        }
    } catch (error) {
        if (error.name === "AbortError") return;
        showLoading("Could not reload assets", error.message, { error: true });
        updateContentStatus(error.message, true);
        log(error.message, true);
    } finally {
        els.pickContent.disabled = false;
    }
}

function updateContentStatus(message, isError = false) {
    els.contentStatus.textContent = message;
    els.contentStatus.classList.toggle("is-error", isError);
}

function updateModsStatus(message, isError = false) {
    els.modsStatus.textContent = message;
    els.modsStatus.classList.toggle("is-error", isError);
}

function updateAssetStreamingStatus(message, isError = false) {
    if (!els.assetStreamingStatus) return;
    els.assetStreamingStatus.textContent = message;
    els.assetStreamingStatus.classList.toggle("is-error", isError);
}

function updateLocalAssetFallbackVisibility() {
    const hideFallback = serverAssetStreamingReady &&
        !remoteAssetSessionFailed &&
        (!remoteAssetSessionChecked || remoteAssetSessionActive);
    if (els.localAssetFallback) els.localAssetFallback.hidden = hideFallback;
    updateFileAccessWarning(hideFallback);
    if (!hideFallback) warnIfUsingBackupFolderAccess();
}
