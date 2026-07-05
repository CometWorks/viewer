import { getViewerParams } from "./quasar-api.js";

const STATUS_PATH = "api/assets/status";
const SESSIONS_PATH = "api/assets/sessions";

let currentStatus = null;
let remoteAssetSession = null;

export async function fetchAssetStreamingStatus() {
    const response = await fetch(pluginApiUrl(STATUS_PATH), {
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    });
    if (!response.ok) throw await createStatusError(response, "Asset streaming status request failed");
    currentStatus = await response.json();
    return currentStatus;
}

export async function prepareRemoteAssetSession(scene) {
    if (!currentStatus?.streamingEnabled || !currentStatus?.fileStreamingReady) {
        const changed = !!remoteAssetSession;
        remoteAssetSession = null;
        return { active: false, changed };
    }

    const { agentId, entityId } = getViewerParams();
    const response = await fetch(pluginApiUrl(SESSIONS_PATH), {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
        credentials: "same-origin",
        body: JSON.stringify({
            agentId,
            entityId,
            mods: Array.isArray(scene?.mods) ? scene.mods : [],
        }),
    });
    if (!response.ok) throw await createStatusError(response, "Asset streaming session request failed");

    const previousId = remoteAssetSession?.sessionId || "";
    const body = await response.json();
    remoteAssetSession = {
        sessionId: body.sessionId || "",
        expiresAtUtc: body.expiresAtUtc || "",
    };
    return { active: true, changed: previousId !== remoteAssetSession.sessionId };
}

export function getRemoteAssetSessionKey() {
    return remoteAssetSession?.sessionId || "";
}

export async function resolveRemoteAssetFile(logicalPath, options = {}) {
    if (!remoteAssetSession?.sessionId) return null;

    const response = await fetch(pluginApiUrl(`${SESSIONS_PATH}/${encodeURIComponent(remoteAssetSession.sessionId)}/resolve`), {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
        credentials: "same-origin",
        body: JSON.stringify({
            logicalPath,
            rootId: options.rootId || options.RootId || "",
            sourceKind: options.sourceKind || options.SourceKind || "",
        }),
    });
    if (!response.ok) return null;

    const resolved = await response.json();
    if (!resolved?.found || !resolved.assetToken) return null;

    return {
        logicalPath: resolved.logicalPath || logicalPath,
        rootId: resolved.rootId || "",
        rootKind: resolved.rootKind || "remote",
        canonicalPath: resolved.logicalPath || logicalPath,
        getFile: async () => fetchResolvedRemoteFile(resolved),
    };
}

async function fetchResolvedRemoteFile(resolved) {
    const response = await fetch(pluginApiUrl(`api/assets/files/${encodeURIComponent(resolved.assetToken)}`), {
        headers: { "Accept": resolved.contentType || "application/octet-stream" },
        credentials: "same-origin",
    });
    if (!response.ok) throw await createStatusError(response, "Asset stream request failed");

    const blob = await response.blob();
    const fileName = String(resolved.logicalPath || "asset").split("/").filter(Boolean).pop() || "asset";
    const lastModified = resolved.lastModifiedUtc ? Date.parse(resolved.lastModifiedUtc) : Date.now();
    if (typeof File === "function") {
        return new File([blob], fileName, {
            type: response.headers.get("content-type") || resolved.contentType || blob.type || "",
            lastModified: Number.isFinite(lastModified) ? lastModified : Date.now(),
        });
    }

    blob.name = fileName;
    blob.lastModified = Number.isFinite(lastModified) ? lastModified : Date.now();
    return blob;
}

function pluginApiUrl(path) {
    return new URL(path, import.meta.url);
}

async function createStatusError(response, fallback) {
    let detail = response.statusText;
    try {
        const body = await response.json();
        detail = body.detail || body.title || body.error || detail;
    } catch {
    }
    const url = response.url ? ` ${response.url}` : "";
    return new Error(`${fallback} (${response.status}): ${detail}${url}`);
}
