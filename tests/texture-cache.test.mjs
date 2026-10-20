import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';
import { state } from '../src/CometWorks.EntityViewer/wwwroot/state.js';

const base = new URL('../src/CometWorks.EntityViewer/wwwroot/', import.meta.url);
let session = 'first', resolves = 0, revision = 'original';
const hooks = registerHooks({
    load(url, context, next) {
        if (url === new URL('content-folder.js', base).href) return {format:'module',shortCircuit:true,source:
            'export const getAssetFolderCacheGeneration = () => 0; export const resolveAssetFile = (...args) => globalThis.resolveTextureFixture(...args); export const disposeCachedTexture = texture => texture.dispose();'};
        if (url === new URL('asset-streaming.js', base).href) return {format:'module',shortCircuit:true,source:
            'export const getRemoteAssetSessionKey = () => globalThis.textureSessionFixture();'};
        return next(url, context);
    },
});
const { loadTexture } = await import(new URL('texture-loader.js', base));
hooks.deregister();

function dds(red) {
    const bytes = new Uint8Array(132), header = new Uint32Array(bytes.buffer, 0, 32);
    header[0] = 0x20534444; header[1] = 124; header[3] = 1; header[4] = 1;
    header[20] = 0x41; header[22] = 32;
    header[23] = 0xff; header[24] = 0xff00; header[25] = 0xff0000; header[26] = 0xff000000;
    bytes.set([red, 0, 0, 255], 128);
    return new File([bytes], 'shared.dds', {lastModified:1000});
}

test('renewing a session resolves textures again but reuses unchanged decoded content', async () => {
    state.renderer = {capabilities:{getMaxAnisotropy:() => 1}};
    globalThis.textureSessionFixture = () => session;
    globalThis.resolveTextureFixture = async logicalPath => {
        resolves++;
        const version = revision;
        return {logicalPath,rootId:'content',cacheKey:version,getFile:async () => dds(version === 'original' ? 10 : 20)};
    };
    try {
        const first = await loadTexture('shared.dds');
        session = 'renewed';
        assert.equal(await loadTexture('shared.dds'), first);
        assert.equal(resolves, 2, 'Expired file tokens must not be reused in the next session');
        revision = 'replacement'; session = 'third';
        const replacement = await loadTexture('shared.dds');
        assert.notEqual(replacement, first, 'Same-size/same-timestamp replacements have distinct content keys');
        assert.equal(first.image.data[0], 10); assert.equal(replacement.image.data[0], 20);
        assert.equal(state.textureCache.size, 2, 'Both prepared revisions stay resident until cleanup');
    } finally {
        for (const texture of state.textureCache.values()) texture.dispose();
        state.textureCache.clear(); state.renderer = null; delete globalThis.textureSessionFixture; delete globalThis.resolveTextureFixture;
    }
});
