// Standalone PreviewHost fixture. Production discovery never selects this module.
export const client = Object.freeze({
    version: 1,
    async request(plugin, operation) {
        if (plugin !== "cometworks.entityviewer") throw new Error("Unknown preview plugin.");
        switch (operation) {
            case "assets.status": return { mode: "local", streamingEnabled: false, consentAccepted: false, consentRequired: false,
                canManageStreaming: false, fileStreamingReady: false, message: "Preview uses local folders. Server asset streaming is unavailable." };
            case "assets.roots.read": return { baseGameSourceMode: "ExternalInstall", baseGameContentPath: "", dedicatedServerModsPath: "", baseGameContentConfigured: false };
            case "assets.installer.status": return { state: "Idle", isRunning: false, log: [], message: "Preview does not run SteamCMD." };
            case "replay.servers": return [];
            default: throw new Error("Preview does not provide live scenes, server writes or history.");
        }
    },
    async openRead() { throw new Error("Preview does not stream server assets."); },
    dispose() {},
});
