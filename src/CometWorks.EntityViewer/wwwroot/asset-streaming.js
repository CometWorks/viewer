import { viewerRequest, viewerSdk, VIEWER_PLUGIN } from "./viewer-sdk.js";
import { loadStreamedAsset } from "./asset-cache.js";
import { createAsyncQueue } from "./async-queue.js";

// Models, their LODs, textures and fonts share the server's two-resolve limit.
// Keep a read's slot until its body and SDK handle have both been released.
const remoteAssetQueue = createAsyncQueue(2);

let currentStatus = null;
let remoteAssetSession = null;

export async function fetchAssetStreamingStatus() {
    currentStatus = await viewerRequest("assets.status");
    return currentStatus;
}

// A grant arrives only with an authorized companion scene or retained history query.
export function unpackAssetResponse(response) {
    const data = response.data;
    if (data && typeof data === "object" && response.assetSession)
        Object.defineProperty(data, "assetSession", { value: response.assetSession, enumerable: false });
    return data;
}

export async function prepareRemoteAssetSession(scene) {
    const previousId = remoteAssetSession?.sessionId || "";
    remoteAssetSession = null;
    if (!currentStatus?.streamingEnabled || !currentStatus?.fileStreamingReady) return { active: false, changed: !!previousId };
    const sessionId = typeof scene?.assetSession?.sessionId === "string" ? scene.assetSession.sessionId.trim() : "";
    if (!sessionId) return { active: false, changed: !!previousId };
    remoteAssetSession = { sessionId, expiresAtUtc: scene.assetSession.expiresAtUtc || "" };
    return { active: true, changed: previousId !== sessionId };
}

export function getRemoteAssetSessionKey() {
    return remoteAssetSession?.sessionId || "";
}

export async function resolveRemoteAssetFile(logicalPath, options = {}) {
    if (!remoteAssetSession?.sessionId) return null;
    const sessionId = remoteAssetSession.sessionId;

    let resolved;
    try {
        resolved = await remoteAssetQueue(() => retryThrottled(() => viewerRequest("assets.resolve", {
            sessionId,
            asset: { logicalPath, rootId: options.rootId || options.RootId || "", sourceKind: options.sourceKind || options.SourceKind || "" },
        })));
    } catch (error) {
        if (error.status >= 400 && error.status < 500 && error.status !== 429) return null;
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
    const blob = await loadStreamedAsset(resolved, () => remoteAssetQueue(async () => {
        const sdk = await viewerSdk();
        const stream = await retryThrottled(() => sdk.openRead(VIEWER_PLUGIN, "assets.file.read", { assetToken: resolved.assetToken }));
        try { return await stream.blob(); }
        finally { await stream.dispose(); }
    }));
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

async function retryThrottled(operation) {
    for (let attempt = 0; ; attempt++) {
        try { return await operation(); }
        catch (error) {
            // Other frames share user quotas. Allow the minute rate window to reset,
            // but never replay a failed body transfer or any non-capacity failure.
            if (error.status !== 429 || attempt >= 8) throw error;
            await new Promise(resolve => setTimeout(resolve, Math.min(250 * 2 ** attempt, 30000)));
        }
    }
}
