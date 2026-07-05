const STATUS_PATH = "api/assets/status";
const CONSENT_PATH = "api/assets/settings/consent";

export const assetStreamingConsentText = "Server asset streaming sends Space Engineers game and mod asset files from this server to viewer users with Quasar access. CometWorks does not verify whether those users own Space Engineers. This is a legal grey area. As the server owner, you are responsible for ensuring every enabled user is allowed to receive and use these assets under the relevant licenses, server rules, and access policies. CometWorks is not responsible for how this feature is used. Enable only if you understand and accept this risk.";

export async function fetchAssetStreamingStatus() {
    const response = await fetch(pluginApiUrl(STATUS_PATH), {
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    });
    if (!response.ok) throw await createStatusError(response, "Asset streaming status request failed");
    return await response.json();
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
