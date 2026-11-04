// Quasar owns discovery, credentials and transport. Preview/tests supply an explicit host adapter.
export const VIEWER_PLUGIN = "cometworks.entityviewer";
let loading;
export async function viewerSdk() {
    const provided = globalThis.quasarPluginHost?.sdk;
    let sdk;
    try { sdk = provided || (await (loading ||= import("./_sdk/client.js"))).client; }
    catch { throw new Error("Entity Viewer requires Quasar browser SDK v1. Update Quasar and the viewer plugin together."); }
    if (sdk?.version !== 1 || !["request", "openRead", "dispose"].every(method => typeof sdk[method] === "function"))
        throw new Error("Incompatible Quasar browser SDK. Update Quasar and the viewer plugin together.");
    return sdk;
}
export async function viewerRequest(operation, payload = {}, options = {}) {
    return (await viewerSdk()).request(VIEWER_PLUGIN, operation, payload, options);
}
