import { log } from "./logging.js";
import { state } from "./state.js";

const CACHE_NAME = "quasar-entityviewer-assets-v1";
const MAX_CACHED_FILE_BYTES = 64 * 1024 * 1024;
const inFlightFiles = new Map();
let cacheGeneration = 0;
let cacheWarningShown = false;

// Call only after a fresh, authorized resolve. The server key includes user, source and revision.
export async function loadStreamedAsset(resolved, fetchBlob) {
    const key = /^[a-f0-9]{64}$/i.test(resolved.cacheKey || "") ? resolved.cacheKey : "";
    if (!key) return await fetchBlob(); // Compatibility with servers predating persistent caching.
    if (inFlightFiles.has(key)) return await inFlightFiles.get(key);
    const promise = loadUncached(resolved, key, fetchBlob, cacheGeneration);
    inFlightFiles.set(key, promise);
    try {
        return await promise;
    } finally {
        if (inFlightFiles.get(key) === promise) inFlightFiles.delete(key);
    }
}

async function loadUncached(resolved, key, fetchBlob, generation) {
    let cache = null;
    const url = new URL(`_asset-cache/${key}`, import.meta.url);
    try {
        if (globalThis.caches) {
            cache = await caches.open(CACHE_NAME);
            const cached = await cache.match(url);
            if (cached) {
                const blob = await cached.blob();
                if (blob.size === resolved.size) {
                    count("Streamed asset cache hits");
                    return blob;
                }
                await cache.delete(url);
            }
        }
    } catch (error) {
        warnCacheUnavailable(error);
    }

    const blob = await fetchBlob();
    count("Streamed asset downloads");
    if (cache && generation === cacheGeneration && blob.size === resolved.size && blob.size <= MAX_CACHED_FILE_BYTES) {
        try {
            await cache.put(url, new Response(blob, { headers: { "Content-Type": blob.type } }));
        } catch (error) {
            // Browser quota/privacy settings may reject storage; viewing still uses the downloaded bytes.
            warnCacheUnavailable(error);
        }
    }
    return blob;
}

export async function clearStreamedAssetCache() {
    cacheGeneration++;
    inFlightFiles.clear();
    if (globalThis.caches) await caches.delete(CACHE_NAME);
}

function count(name) {
    state.stats[name] = (state.stats[name] || 0) + 1;
}

function warnCacheUnavailable(error) {
    if (cacheWarningShown) return;
    cacheWarningShown = true;
    log(`Browser asset cache unavailable; assets will still stream from the server (${error.name || "storage error"}).`, true);
}
