const DEFAULT_BASE_PATH = "/_quasar/plugins/cometworks.entityviewer/api/assets";

export async function fetchAssetStatus(basePath = DEFAULT_BASE_PATH) {
    return await request(`${normalizeBasePath(basePath)}/status`);
}

export async function acceptConsent(basePath = DEFAULT_BASE_PATH) {
    return await request(`${normalizeBasePath(basePath)}/settings/consent`, {
        method: "POST",
        body: {
            consentAccepted: true,
            streamingEnabled: true,
        },
    });
}

export async function fetchRootSettings(basePath = DEFAULT_BASE_PATH) {
    return await request(`${normalizeBasePath(basePath)}/settings/roots`);
}

export async function saveRootSettings(basePath = DEFAULT_BASE_PATH, settings = {}) {
    return await request(`${normalizeBasePath(basePath)}/settings/roots`, {
        method: "POST",
        body: settings,
    });
}

export async function fetchInstallerStatus(basePath = DEFAULT_BASE_PATH) {
    return await request(`${normalizeBasePath(basePath)}/installer/status`);
}

export async function startInstaller(basePath = DEFAULT_BASE_PATH, requestBody = {}) {
    return await request(`${normalizeBasePath(basePath)}/installer/start`, {
        method: "POST",
        body: requestBody,
    });
}

export async function sendInstallerInput(basePath = DEFAULT_BASE_PATH, input = "") {
    return await request(`${normalizeBasePath(basePath)}/installer/input`, {
        method: "POST",
        body: { input },
    });
}

export async function cancelInstaller(basePath = DEFAULT_BASE_PATH) {
    return await request(`${normalizeBasePath(basePath)}/installer/cancel`, {
        method: "POST",
    });
}

async function request(url, options = {}) {
    const init = {
        method: options.method || "GET",
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    };
    if (options.body !== undefined) {
        init.headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(options.body);
    }

    const response = await fetch(url, init);
    if (!response.ok) throw await createRequestError(response, "Asset setup request failed");
    return await response.json();
}

async function createRequestError(response, fallback) {
    let detail = response.statusText || fallback;
    try {
        const body = await response.json();
        detail = body.detail || body.title || body.error || detail;
    } catch {
    }
    const url = response.url ? ` ${response.url}` : "";
    return new Error(`${fallback} (${response.status}): ${detail}${url}`);
}

function normalizeBasePath(basePath) {
    return String(basePath || DEFAULT_BASE_PATH).replace(/\/+$/, "");
}
