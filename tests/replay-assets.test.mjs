import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';
import * as THREE from 'three';
import { state } from '../src/CometWorks.EntityViewer/wwwroot/state.js';

const loaderUrl = new URL('../src/CometWorks.EntityViewer/wwwroot/mwm-loader.js', import.meta.url).href;
const textureUrl = new URL('../src/CometWorks.EntityViewer/wwwroot/texture-loader.js', import.meta.url).href;
const hooks = registerHooks({
    resolve(specifier, context, next) { return next(specifier === 'zip.js' ? '@zip.js/zip.js' : specifier, context); },
    load(url, context, next) {
        if (url === loaderUrl) return {format:'module',shortCircuit:true,
            source:'export const resolveModelAsset = asset => globalThis.resolveReplayFixture(asset); export function clearModelCache() {}'};
        if (url === textureUrl) return {format:'module',shortCircuit:true,
            source:'export const loadTexture = (...args) => globalThis.loadReplayTextureFixture(...args); export function textureToCanvas() { throw new Error("Unexpected canvas conversion"); }'};
        return next(url, context);
    },
});
const { ReplayGrids } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-grids.js');
hooks.deregister();
const flush = () => new Promise(resolve => setImmediate(resolve));
function parsed(path) {
    return {status:'parsed',model:{logicalPath:path,positions:new Float32Array([0,0,0,1,0,0,0,1,0]),indices:new Uint16Array([0,1,2]),groups:[]}};
}
function recording(...models) {
    return { records: [], geometry: new Map([['page', models.map(model => ({model,translation:[0,0,0]}))]]) };
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

test('range changes reuse model geometry, materials and textures until the history window closes', async () => {
    const requests = [], textureRequests = [];
    const texture = new THREE.Texture();
    let disposedTextures = 0, disposedGeometry = 0, disposedMaterial = 0;
    texture.addEventListener('dispose', () => disposedTextures++);
    globalThis.loadReplayTextureFixture = async path => {
        textureRequests.push(path); state.textureCache.set(path, texture); return texture;
    };
    globalThis.resolveReplayFixture = async asset => {
        requests.push(asset.logicalPath);
        const result = parsed(asset.logicalPath);
        result.model.groups = [{start:0,count:3,materialIndex:0,materialName:'paint',technique:'MESH',textures:{DiffuseTexture:'shared.dds'}}];
        return result;
    };
    const renderer = new ReplayGrids(() => {});
    try {
        await renderer.preload(recording('first.mwm'));
        const model = renderer.models.get('/first.mwm'), context = renderer.context;
        model.geometry.addEventListener('dispose', () => disposedGeometry++);
        model.materials[0].addEventListener('dispose', () => disposedMaterial++);
        assert.equal(model.materials[0].map, texture);
        const group = new THREE.Group();
        renderer.update(group, {layout:['page'],gridSize:2.5}, recording('first.mwm').geometry);
        renderer.release(group);
        await renderer.preload(recording('first.mwm', 'second.mwm'));
        await renderer.preload(recording('first.mwm'));
        assert.deepEqual(requests, ['first.mwm', 'second.mwm']);
        assert.deepEqual(textureRequests, ['shared.dds']);
        assert.equal(renderer.models.get('/first.mwm'), model);
        assert.equal(renderer.context, context);
        assert.equal(disposedTextures + disposedGeometry + disposedMaterial, 0);
        renderer.dispose(); renderer.dispose();
        assert.equal(disposedTextures, 1); assert.equal(disposedGeometry, 1); assert.equal(disposedMaterial, 1);
        assert.equal(context.disposed, true);
        for (const map of [renderer.models, context.geometries, context.materials, context.preloadedTextures, context.textureLoads, state.textureCache]) assert.equal(map.size, 0);
        const reopened = new ReplayGrids(() => {});
        try { await reopened.preload(recording('first.mwm')); assert.equal(requests.length, 3); }
        finally { reopened.dispose(); }
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; delete globalThis.loadReplayTextureFixture; }
});

test('overlapping range loads share pending model jobs and add only missing models', async () => {
    const requests = [];
    globalThis.resolveReplayFixture = asset => new Promise(resolve => requests.push({asset,resolve}));
    const renderer = new ReplayGrids(() => {});
    try {
        const first = renderer.preload(recording('shared.mwm'));
        const second = renderer.preload(recording('shared.mwm', 'new.mwm'));
        assert.equal(requests.length, 2);
        for (const {asset,resolve} of requests) resolve(parsed(asset.logicalPath));
        await Promise.all([first, second]);
        assert.equal(renderer.models.size, 2);
        assert.equal(renderer.active, 0);
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; }
});

test('closing during a model load cancels queued work and cannot repopulate disposed assets', async () => {
    const requests = [];
    let changed = 0;
    globalThis.resolveReplayFixture = asset => new Promise(resolve => requests.push({asset,resolve}));
    const renderer = new ReplayGrids(() => changed++), context = renderer.context;
    const pending = renderer.preload(recording(...Array.from({length:8}, (_,i) => `${i}.mwm`)));
    renderer.dispose();
    await pending;
    for (const {asset,resolve} of requests) resolve(parsed(asset.logicalPath));
    await flush(); await flush();
    await renderer.preload(recording('late.mwm'));
    assert.equal(requests.length, 4); assert.equal(changed, 0);
    assert.equal(context.disposed, true); assert.equal(renderer.models.size, 0);
    assert.equal(renderer.active, 0); assert.equal(renderer.queue.length, 0);
    delete globalThis.resolveReplayFixture;
});

test('a source reset reloads the same model and failed models can retry in another range', async () => {
    let attempts = 0;
    globalThis.resolveReplayFixture = async asset => ++attempts === 1 ? {status:'missing',message:'unavailable'} : parsed(asset.logicalPath);
    const renderer = new ReplayGrids(() => {});
    try {
        await renderer.preload(recording('model.mwm'));
        await renderer.preload(recording('model.mwm'));
        const old = renderer.models.get('/model.mwm').geometry;
        renderer.clear();
        await renderer.preload(recording('model.mwm'));
        assert.equal(attempts, 3);
        assert.notEqual(renderer.models.get('/model.mwm').geometry, old);
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; }
});

test('a texture finishing after close is disposed and cannot restore the render context', async () => {
    let finishTexture, disposed = 0;
    globalThis.loadReplayTextureFixture = path => {
        const pending = new Promise(resolve => { finishTexture = resolve; });
        state.textureCache.set(path, pending); return pending;
    };
    globalThis.resolveReplayFixture = async asset => {
        const result = parsed(asset.logicalPath);
        result.model.groups = [{start:0,count:3,materialIndex:0,materialName:'paint',technique:'MESH',textures:{DiffuseTexture:'pending.dds'}}];
        return result;
    };
    const renderer = new ReplayGrids(() => {}), context = renderer.context;
    try {
        const pending = renderer.preload(recording('model.mwm'));
        await flush();
        assert.equal(typeof finishTexture, 'function');
        renderer.dispose(); await pending;
        const texture = new THREE.Texture(); texture.addEventListener('dispose', () => disposed++);
        finishTexture(texture); await flush();
        assert.equal(disposed, 1); assert.equal(renderer.active, 0);
        for (const map of [renderer.models, context.geometries, context.materials, context.preloadedTextures, context.textureLoads, state.textureCache]) assert.equal(map.size, 0);
    } finally { renderer.dispose(); delete globalThis.resolveReplayFixture; delete globalThis.loadReplayTextureFixture; }
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
        let disposed = 0;
        before.geometry.addEventListener('dispose', () => disposed++);
        terrain.reset();
        assert.equal(before.parent, null); assert.equal(before.visible, false);
        assert.equal(terrain.meshes.size, 0); assert.equal(disposed, 0);
        await terrain.preload({...replay,voxels:new Map([['b',replay.voxels.get('b')]])},renderer.context,()=>{});
        assert.equal(terrain.meshes.size, 1); assert.equal(terrain.meshes.has('a'), false);
        assert.equal(terrain.cache.size, 2);
        terrain.reset();
        await terrain.preload(replay,renderer.context,()=>{});
        assert.equal(terrain.meshes.get('a'), before); assert.equal(before.parent, world);
        assert.equal(disposed, 0); assert.equal(terrain.ready, 2);
        terrain.clear();
        assert.equal(disposed, 1); assert.equal(terrain.cache.size, 0); assert.equal(world.children.length, 0);
    } finally { terrain.clear(); renderer.dispose(); }
});

test('closing during terrain preparation discards the pending revision', async () => {
    const { ReplayTerrain } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-terrain.js');
    const world = new THREE.Group(), terrain = new ReplayTerrain(world), renderer = new ReplayGrids(() => {});
    const pending = terrain.preload({voxels:new Map([['page',{palette:[]}]])}, renderer.context, () => assert.fail('closed history progressed'));
    terrain.clear(); renderer.dispose();
    await pending;
    assert.equal(terrain.cache.size, 0); assert.equal(terrain.meshes.size, 0); assert.equal(world.children.length, 0);
});

test('adding historical mod roots retains open archives until the asset source changes', async () => {
    const { setSceneModRoots, clearAssetFolderCaches } = await import('../src/CometWorks.EntityViewer/wwwroot/content-folder.js');
    const mod = {rootId:'mods-direct:test',name:'test'};
    let closed = 0;
    try {
        await setSceneModRoots([mod]);
        const root = state.modRoots.get(mod.rootId);
        root.archive = {reader:{async close() { closed++; }}};
        await setSceneModRoots([{...mod}, {rootId:'mods-direct:other',name:'other'}]);
        assert.equal(state.modRoots.get(mod.rootId), root); assert.equal(closed, 0);
        clearAssetFolderCaches();
        await setSceneModRoots([mod]);
        assert.notEqual(state.modRoots.get(mod.rootId), root); assert.equal(closed, 1);
    } finally { clearAssetFolderCaches(); await setSceneModRoots([]); }
});
