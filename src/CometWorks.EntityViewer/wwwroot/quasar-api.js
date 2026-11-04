import { unpackAssetResponse } from "./asset-streaming.js";
import { viewerRequest } from "./viewer-sdk.js";

export function getViewerParams() {
    const params = new URLSearchParams(window.location.search);
    const agentId = params.get("agentId") || "";
    const entityId = params.get("entityId") || "";
    if (!agentId || !entityId) throw new Error("Viewer URL must include agentId and entityId.");
    return { agentId, entityId, voxels: parseVoxelFlag(params), context: parseContextFlag(params) };
}

export function parseVoxelFlag(params = new URLSearchParams(window.location.search)) {
    if (!params.has("voxels")) return { present: false, enabled: false };
    const value = (params.get("voxels") || "").trim().toLowerCase();
    return { present: true, enabled: value === "" || value === "1" || value === "true" || value === "yes" };
}

export function parseContextFlag(params = new URLSearchParams(window.location.search)) {
    if (!params.has("context")) return { present: false, enabled: false };
    const value = (params.get("context") || "").trim().toLowerCase();
    return { present: true, enabled: value === "" || value === "1" || value === "true" || value === "yes" };
}

export async function fetchEntityScene() {
    const { agentId, entityId, voxels, context } = getViewerParams();
    return unpackAssetResponse(await viewerRequest("scene.read", { serverId: agentId, entityId, voxels: voxels.enabled, context: context.enabled }));
}
