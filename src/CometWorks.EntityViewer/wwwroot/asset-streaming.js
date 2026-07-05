import { getViewerParams } from "./quasar-api.js";

const STATUS_PATH = "api/assets/status";
const CONSENT_PATH = "api/assets/settings/consent";
const INSTALLER_STATUS_PATH = "api/assets/installer/status";
const INSTALLER_START_PATH = "api/assets/installer/start";
const INSTALLER_INPUT_PATH = "api/assets/installer/input";
const INSTALLER_CANCEL_PATH = "api/assets/installer/cancel";
const SESSIONS_PATH = "api/assets/sessions";

export const assetStreamingConsentText = "Server asset streaming sends Space Engineers game and mod asset files from this server to viewer users with Quasar access. CometWorks does not verify whether those users own Space Engineers. This is a legal grey area. As the server owner, you are responsible for ensuring every enabled user is allowed to receive and use these assets under the relevant licenses, server rules, and access policies. CometWorks is not responsible for how this feature is used. Enable only if you understand and accept this risk.";

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

export async function acceptAssetStreamingConsent() {
    const response = await fetch(pluginApiUrl(CONSENT_PATH), {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
        credentials: "same-origin",
        body: JSON.stringify({ consentAccepted: true, streamingEnabled: true }),
    });
    if (!response.ok) throw await createStatusError(response, "Asset streaming consent request failed");
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

export async function fetchSteamCmdInstallerStatus() {
    const response = await fetch(pluginApiUrl(INSTALLER_STATUS_PATH), {
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    });
    if (!response.ok) throw await createStatusError(response, "SteamCMD installer status request failed");
    return await response.json();
}

export async function startSteamCmdInstaller(loginName, validate = true) {
    const response = await fetch(pluginApiUrl(INSTALLER_START_PATH), {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
        credentials: "same-origin",
        body: JSON.stringify({ loginName, validate }),
    });
    if (!response.ok) throw await createStatusError(response, "SteamCMD installer start request failed");
    return await response.json();
}

export async function sendSteamCmdInstallerInput(input) {
    const response = await fetch(pluginApiUrl(INSTALLER_INPUT_PATH), {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
        credentials: "same-origin",
        body: JSON.stringify({ input }),
    });
    if (!response.ok) throw await createStatusError(response, "SteamCMD installer input request failed");
    return await response.json();
}

export async function cancelSteamCmdInstaller() {
    const response = await fetch(pluginApiUrl(INSTALLER_CANCEL_PATH), {
        method: "POST",
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    });
    if (!response.ok) throw await createStatusError(response, "SteamCMD installer cancel request failed");
    return await response.json();
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
    return new Error(`${fallback} (${response.status}): ${detail}`);
}
