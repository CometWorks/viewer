import { log } from "./logging.js";
import { state } from "./state.js";

const CACHE_NAME = "quasar-entityviewer-assets-v1";
const MAX_CACHED_FILE_BYTES = 64 * 1024 * 1024;
const MAX_MEMORY_BYTES = 32 * 1024 * 1024;
const MAX_PENDING_WRITE_BYTES = 256 * 1024 * 1024;
const MAX_CONCURRENT_WRITES = 2;
let current = createCacheState();
let cacheWarningShown = false;

function createCacheState() {
    return { inFlight: new Map(), memory: new Map(), memoryBytes: 0, store: null, reset: null,
        writes: new Map(), queue: [], writeBytes: 0, activeWrites: 0, scheduled: false,
        invalidated: false, writesDisabled: false };
}

// Call only after a fresh, authorized resolve. The server key includes user, source and revision.
export async function loadStreamedAsset(resolved, fetchBlob) {
    const key = /^[a-f0-9]{64}$/i.test(resolved.cacheKey || "") ? resolved.cacheKey : "";
    if (!key) return await fetchBlob(); // Compatibility with servers predating persistent caching.
    const context = current;
    const available = context.memory.get(key) || context.writes.get(key)?.blob;
    if (available && available.size === resolved.size) {
        if (context.memory.has(key)) {
            context.memory.delete(key);
            context.memory.set(key, available);
        }
        count("Streamed asset cache hits");
        count("Streamed asset memory hits");
        return available;
    }
    if (context.inFlight.has(key)) return await context.inFlight.get(key);
    const promise = loadUncached(context, resolved, key, fetchBlob);
    context.inFlight.set(key, promise);
    try {
        return await promise;
    } finally {
        if (context.inFlight.get(key) === promise) context.inFlight.delete(key);
    }
}

async function persistentStore(context) {
    if (!globalThis.caches) return null;
    if (!context.store) context.store = (async () => {
        try {
            await context.reset;
            const cache = await caches.open(CACHE_NAME);
            // An index avoids a storage IPC round trip for every cold-cache miss.
            const keys = new Set((await cache.keys()).map(request => request.url));
            return { cache, keys };
        } catch (error) {
            warnCacheUnavailable(error);
            return null;
        }
    })();
    return await context.store;
}

async function loadUncached(context, resolved, key, fetchBlob) {
    const store = await persistentStore(context);
    const url = new URL(`_asset-cache/${key}`, import.meta.url);
    try {
        if (store?.keys.has(String(url))) {
            const cached = await store.cache.match(url);
            if (cached) {
                const blob = await cached.blob();
                if (blob.size === resolved.size) {
                    count("Streamed asset cache hits");
                    remember(context, key, blob);
                    return blob;
                }
                await store.cache.delete(url);
            }
            store.keys.delete(String(url));
        }
    } catch (error) {
        warnCacheUnavailable(error);
    }

    const blob = await fetchBlob();
    count("Streamed asset downloads");
    if (blob.size === resolved.size) {
        remember(context, key, blob);
        if (store && !context.invalidated && !context.writesDisabled && blob.size <= MAX_CACHED_FILE_BYTES) {
            if (context.writeBytes + blob.size <= MAX_PENDING_WRITE_BYTES) {
                const job = { key, url, blob, store };
                context.writes.set(key, job);
                context.queue.push(job);
                context.writeBytes += blob.size;
                scheduleWrites(context);
            } else count("Streamed asset skipped cache writes");
        }
    }
    return blob;
}

function remember(context, key, blob) {
    if (context.invalidated || blob.size > MAX_MEMORY_BYTES) return;
    const previous = context.memory.get(key);
    if (previous) context.memoryBytes -= previous.size;
    context.memory.delete(key);
    context.memory.set(key, blob);
    context.memoryBytes += blob.size;
    while (context.memoryBytes > MAX_MEMORY_BYTES) {
        const oldest = context.memory.keys().next().value;
        context.memoryBytes -= context.memory.get(oldest).size;
        context.memory.delete(oldest);
    }
}

function scheduleWrites(context) {
    if (context.scheduled || context.invalidated || context.writesDisabled || !context.queue.length || context.activeWrites >= MAX_CONCURRENT_WRITES) return;
    context.scheduled = true;
    const run = () => {
        context.scheduled = false;
        drainWrites(context);
    };
    if (typeof requestIdleCallback === "function") requestIdleCallback(run, { timeout: 1000 });
    else setTimeout(run, 0);
}

function drainWrites(context) {
    while (!context.invalidated && !context.writesDisabled && context.queue.length && context.activeWrites < MAX_CONCURRENT_WRITES) {
        const job = context.queue.shift();
        context.activeWrites++;
        write(context, job);
    }
}

async function write(context, job) {
    try {
        await job.store.cache.put(job.url, new Response(job.blob, { headers: { "Content-Type": job.blob.type } }));
        if (!context.invalidated) job.store.keys.add(String(job.url));
    } catch (error) {
        // Stop retrying a full/blocked store during this viewer session.
        context.writesDisabled = true;
        for (const queued of context.queue) {
            context.writes.delete(queued.key);
            context.writeBytes -= queued.blob.size;
        }
        context.queue.length = 0;
        warnCacheUnavailable(error);
    } finally {
        context.writes.delete(job.key);
        context.writeBytes -= job.blob.size;
        context.activeWrites--;
        // Continue the bounded pipeline after its initial idle start; waiting for another
        // idle period per file would starve persistence while the viewport is animating.
        drainWrites(context);
    }
}

export async function clearStreamedAssetCache() {
    const previous = current;
    previous.invalidated = true;
    previous.queue.length = 0;
    previous.memory.clear();
    previous.writes.clear();
    current = createCacheState();
    if (globalThis.caches) {
        current.reset = caches.delete(CACHE_NAME);
        await current.reset;
    }
}

function count(name) {
    state.stats[name] = (state.stats[name] || 0) + 1;
}

function warnCacheUnavailable(error) {
    if (cacheWarningShown) return;
    cacheWarningShown = true;
    log(`Browser asset cache unavailable; assets will still stream from the server (${error.name || "storage error"}).`, true);
}
