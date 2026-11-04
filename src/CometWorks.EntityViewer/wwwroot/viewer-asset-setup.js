import { viewerRequest } from "./viewer-sdk.js";

// basePath stays in the interop signatures for existing Razor callers; Quasar discovers its own panel base.
export const fetchAssetStatus = (basePath) => viewerRequest("assets.status");
export const acceptConsent = (basePath) => viewerRequest("assets.consent.update", { consentAccepted: true, streamingEnabled: true });
export const fetchRootSettings = (basePath) => viewerRequest("assets.roots.read");
export const saveRootSettings = (basePath, settings = {}) => viewerRequest("assets.roots.update", settings);
export const fetchInstallerStatus = (basePath) => viewerRequest("assets.installer.status");
export const startInstaller = (basePath, body = {}) => viewerRequest("assets.installer.start", body);
export const sendInstallerInput = (basePath, input = "") => viewerRequest("assets.installer.input", { input });
export const cancelInstaller = (basePath) => viewerRequest("assets.installer.cancel");

export function scrollToBottom(element) {
    if (!element) return;
    requestAnimationFrame(() => {
        element.scrollTop = element.scrollHeight;
    });
}
