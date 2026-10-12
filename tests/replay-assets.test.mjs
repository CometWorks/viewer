import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';

const loaderUrl = new URL('../src/CometWorks.EntityViewer/wwwroot/mwm-loader.js', import.meta.url).href;
const hooks = registerHooks({
    resolve(specifier, context, next) { return next(specifier === 'zip.js' ? '@zip.js/zip.js' : specifier, context); },
    load(url, context, next) {
        if (url === loaderUrl) return {format:'module',shortCircuit:true,
            source:'export const resolveModelAsset = asset => globalThis.resolveReplayFixture(asset); export function clearModelCache() {}'};
        return next(url, context);
    },
});
const { ReplayGrids } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-grids.js');
hooks.deregister();
const flush = () => new Promise(resolve => setImmediate(resolve));
function parsed(path) {
    return {status:'parsed',model:{logicalPath:path,positions:new Float32Array([0,0,0,1,0,0,0,1,0]),indices:new Uint16Array([0,1,2]),groups:[]}};
}

test('history preloads future assets with four jobs and rejects stale results after reload', async () => {
    const requests = [];
    globalThis.resolveReplayFixture = asset => new Promise(resolve => requests.push({asset,resolve}));
    const renderer = new ReplayGrids(() => {});
    try {
        renderer.preload({records:[{type:'player',model:'player.mwm'}],geometry:new Map([['future',Array.from({length:6},(_,i)=>({model:`future-${i}.mwm`}))]])});
        assert.equal(requests.length,4);
        assert.equal(renderer.models.size,7);
        assert.equal(requests[0].asset.logicalPath,'player.mwm');
        renderer.clear();
        renderer.preload({records:[],geometry:new Map([['new',[{model:'new.mwm'}]]])});
        for (const item of requests.slice()) item.resolve(parsed(item.asset.logicalPath));
        await flush(); await flush();
        assert.equal(requests.length,5); // Old queued history never starts.
        requests[4].resolve(parsed('new.mwm'));
        await flush(); await flush();
        assert.deepEqual([...renderer.models.keys()],['/new.mwm']);
        assert.ok(renderer.models.get('/new.mwm').geometry);
        assert.equal(renderer.active,0);
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; }
});
