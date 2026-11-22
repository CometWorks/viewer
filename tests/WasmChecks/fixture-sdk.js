// Served at the plugin's generated _sdk/client.js URL by the Chromium test.
import { client as preview } from "../preview-sdk.js";

let accepted = false;
let installerState = "Idle";

function installerStatus() {
    return {
        state: installerState, isRunning: installerState === "Running",
        log: [{ timestampUtc: "2026-10-01T12:00:00Z", stream: "stdout", message: "fixture ready" }],
    };
}

export const client = {
    ...preview,
    async request(plugin, operation, args) {
        if (plugin !== "cometworks.entityviewer") throw new Error("Unexpected plugin ID");
        if (operation === "scene.read") return { data: { grid: { id: args.entityId, gridSize: 2.5 }, mods: [] } };
        if (globalThis.fixtureManager) {
            switch (operation) {
                case "assets.consent.update":
                    accepted = args.consentAccepted === true && args.streamingEnabled === true;
                    // The mutation returns the same status shape as a read.
                case "assets.status":
                    return { canManageStreaming: true, consentRequired: !accepted, consentAccepted: accepted };
                case "assets.roots.update":
                    if (args.baseGameSourceMode !== "ExternalInstall" || args.baseGameContentPath !== "/fixture/content")
                        throw new Error("Unexpected root settings");
                    return args;
                case "assets.installer.start":
                    if (args.loginName !== "fixture") throw new Error("Unexpected installer account");
                    installerState = "Running";
                    return installerStatus();
                case "assets.installer.status":
                    if (installerState === "Running") installerState = "Succeeded";
                    return installerStatus();
                case "assets.roots.read":
                    if (installerState === "Succeeded") return { baseGameSourceMode: "ExternalInstall", baseGameContentPath: "/fixture/refreshed" };
            }
        }
        return preview.request(plugin, operation, args);
    },
};
