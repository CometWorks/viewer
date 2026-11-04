import { viewerRequest, viewerSdk, VIEWER_PLUGIN } from "./viewer-sdk.js";
import { getViewerParams } from "./quasar-api.js";
import { log } from "./logging.js";
import { loadStreamedAsset } from "./asset-cache.js";

let currentStatus = null;
let remoteAssetSession = null;
let sessionGeneration = 0;

export async function fetchAssetStreamingStatus() {
    currentStatus = await viewerRequest("assets.status");
    return currentStatus;
}

export async function prepareRemoteAssetSession(scene, target = null) {
    const generation = ++sessionGeneration;
    const previousId = remoteAssetSession?.sessionId || "";
    remoteAssetSession = null;
    if (!currentStatus?.streamingEnabled || !currentStatus?.fileStreamingReady) {
        return { active: false, changed: !!previousId };
    }

    const { agentId, entityId } = target || getViewerParams();
    log("Creating server asset streaming session through Quasar SDK.");
    const body = await viewerRequest("assets.session.create", {
        agentId, entityId, mods: Array.isArray(scene?.mods) ? scene.mods : [],
    });
    if (generation !== sessionGeneration) return { active: false, changed: false };
    const sessionId = typeof body?.sessionId === "string" ? body.sessionId.trim() : "";
    if (!sessionId) {
        const fields = body && typeof body === "object" ? Object.keys(body).join(", ") : "none";
        throw new Error(`Asset streaming session response has no valid session ID (fields: ${fields}).`);
    }
    remoteAssetSession = {
        sessionId,
        expiresAtUtc: body.expiresAtUtc || "",
    };
    return { active: true, changed: previousId !== remoteAssetSession.sessionId };
}

export function getRemoteAssetSessionKey() {
    return remoteAssetSession?.sessionId || "";
}

export async function resolveRemoteAssetFile(logicalPath, options = {}) {
    if (!remoteAssetSession?.sessionId) return null;

    let resolved;
    try {
        resolved = await viewerRequest("assets.resolve", {
            sessionId: remoteAssetSession.sessionId,
            asset: { logicalPath, rootId: options.rootId || options.RootId || "", sourceKind: options.sourceKind || options.SourceKind || "" },
        });
    } catch (error) {
        if (error.status >= 400 && error.status < 500) return null;
        throw error;
    }
    if (!resolved?.found || !resolved.assetToken) return null;

    return {
        logicalPath: resolved.logicalPath || logicalPath,
        rootId: resolved.rootId || "",
        rootKind: resolved.rootKind || "remote",
        canonicalPath: resolved.logicalPath || logicalPath,
        cacheKey: resolved.cacheKey || "",
        getFile: async () => fetchResolvedRemoteFile(resolved),
    };
}

async function fetchResolvedRemoteFile(resolved) {
    const blob = await loadStreamedAsset(resolved, async () => {
        const stream = await (await viewerSdk()).openRead(VIEWER_PLUGIN, "assets.file.read", { assetToken: resolved.assetToken });
        try { return await stream.blob(); }
        finally { await stream.dispose(); }
    });
    const fileName = String(resolved.logicalPath || "asset").split("/").filter(Boolean).pop() || "asset";
    const lastModified = resolved.lastModifiedUtc ? Date.parse(resolved.lastModifiedUtc) : Date.now();
    if (typeof File === "function") {
        return new File([blob], fileName, {
            type: resolved.contentType || blob.type || "",
            lastModified: Number.isFinite(lastModified) ? lastModified : Date.now(),
        });
    }

    blob.name = fileName;
    blob.lastModified = Number.isFinite(lastModified) ? lastModified : Date.now();
    return blob;
}
