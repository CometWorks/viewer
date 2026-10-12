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

test('preparation loads every model beyond the previous admission cap and includes armor parts', async () => {
    let count = 0;
    globalThis.resolveReplayFixture = async asset => { count++; return parsed(asset.logicalPath); };
    const renderer = new ReplayGrids(() => {});
    try {
        const block = {partsId:'armor',patterns:[1,0,4,2],mod:'',translation:[5,0,0],rotation:[0,0,0,1],color:[0,-1,0]};
        await renderer.preload({records:[],modelParts:new Map([['armor',{parts:[{model:'plate.mwm',matrix:[2.5,0,0,0,0,2.5,0,0,0,0,2.5,0,0,0,0,1]}]}]]),
            geometry:new Map([['page',[...Array.from({length:160},(_,i)=>({model:`model-${i}.mwm`})),block]]])});
        assert.equal(count,161);
        assert.equal(renderer.models.size,161);
        assert.equal(renderer.active,0);
        const source = renderer.sources(block)[0];
        assert.equal(source.model,'plate.mwm');
        assert.deepEqual(source.patternOffset,{x:1,y:0,z:4,w:2});
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; }
});

test('historical terrain uses the shared mesher and switches revisions without rebuilding', async () => {
    const THREE = await import('three');
    const { ReplayTerrain } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-terrain.js');
    const { buildReplay } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-model.js');
    const world = new THREE.Group(), terrain = new ReplayTerrain(world), renderer = new ReplayGrids(() => {});
    const page = {body:'v',origin:[100,0,0],rotation:[0,0,0,1],min:[32,0,0],lod:1,size:[2,2,2],content:[0,0,255,255,0,0,255,255],materials:Array(8).fill(0),palette:[]};
    const replay = buildReplay([{session:'s',voxels:{a:page,b:{...page,content:[0,0,128,128,0,0,128,128]}},records:[
        {at:1000,kind:'voxel-sample',entity:'v',terrainId:'a',value:1000},
        {at:2000,kind:'voxel-invalidated',entity:'v'},
        {at:3000,kind:'voxel-sample',entity:'v',terrainId:'b',value:3000}]}]);
    try {
        await terrain.preload(replay,renderer.context,()=>{});
        assert.equal(terrain.meshes.size,2);
        const before = terrain.meshes.get('a');
        terrain.draw(replay,1500,'s',new THREE.Vector3(100,0,0));
        assert.equal(before.visible,true); assert.equal(before.position.x,64);
        terrain.draw(replay,2500,'s',new THREE.Vector3()); assert.equal(before.visible,false);
        terrain.draw(replay,3000,'s',new THREE.Vector3()); assert.equal(terrain.meshes.get('b').visible,true);
        terrain.draw(replay,1500,'s',new THREE.Vector3()); assert.equal(terrain.meshes.get('a'),before);
    } finally { terrain.clear(); renderer.dispose(); }
});
