import test from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from 'three';
import { registerHooks } from 'node:module';
const hooks = registerHooks({resolve(specifier, context, next) { return next(specifier === 'zip.js' ? '@zip.js/zip.js' : specifier, context); }});
const { replayViewRotation, diagnosticText, updateReplaySun } = await import('../src/CometWorks.EntityViewer/wwwroot/replay-scene.js');
hooks.deregister();
import { buildReplay, replayJournal, describeEvent, pruneReplayContext, frameAt, terrainAt } from '../src/CometWorks.EntityViewer/wwwroot/replay-model.js';

test('gravity view matches projected forward, including vertical and zero gravity', () => {
    for (const gravity of [[0,-9.81,0],[-9,0,0],[0,0,9]]) {
        const q = replayViewRotation({gravity});
        const up = new THREE.Vector3(...gravity).negate().normalize().applyQuaternion(q);
        assert.ok(up.distanceTo(new THREE.Vector3(0,1,0)) < 1e-8);
        assert.ok(q.toArray().every(Number.isFinite));
    }
    const rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,1,0), .5);
    assert.ok(rotation.clone().multiply(replayViewRotation({rotation:rotation.toArray()})).angleTo(new THREE.Quaternion()) < 1e-8);
});

test('replay sunlight follows recorded time and the gravity frame, including reverse seeks and missing sun data', () => {
    const sunlight = new THREE.DirectionalLight(), ambient = new THREE.AmbientLight();
    const origin = new THREE.Vector3(100,200,300), rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,0,1),Math.PI/2);
    const sample = {entity:'grid',position:[110,200,300],size:[20,10,10],gridSize:2.5};
    const replay = buildReplay([{session:'s',records:[
        {at:1000,kind:'health',sun:[0,1,0],sunIntensity:1},
        {at:1000,kind:'sample',...sample},
        {at:2000,kind:'health',sun:[0,0,1],sunIntensity:0},
        {at:3000,kind:'health'},
    ]}]);
    const apply = (at, enabled=true) => updateReplaySun(sunlight,ambient,frameAt(replay,at),origin,rotation,'grid',enabled);
    assert.match(apply(1500),/Recorded/);
    const morning = sunlight.position.clone();
    assert.ok(sunlight.target.position.distanceTo(new THREE.Vector3(0,10,0)) < 1e-8);
    assert.ok(sunlight.position.clone().sub(sunlight.target.position).normalize().distanceTo(new THREE.Vector3(-1,0,0)) < 1e-8);
    assert.equal(sunlight.visible,true); assert.ok(ambient.intensity < 1);
    apply(2500); assert.equal(sunlight.intensity,0); assert.notDeepEqual(sunlight.position,morning);
    apply(1500); assert.ok(sunlight.position.distanceTo(morning) < 1e-8);
    const shadow = sunlight.shadow.camera;
    assert.ok([shadow.left,shadow.right,shadow.top,shadow.bottom,shadow.near,shadow.far].every(Number.isFinite));
    assert.match(apply(3500),/unavailable/); assert.equal(sunlight.visible,false); assert.equal(ambient.intensity,1);
    apply(1500,false); assert.equal(sunlight.visible,false); assert.equal(ambient.intensity,1);
    apply(1500); assert.equal(sunlight.visible,true);
    apply(20000); assert.equal(sunlight.visible,false,'Expired history must not retain sunlight');
    sunlight.shadow.dispose();
});

test('journal records layout changes and cadence without asserting who caused destruction', () => {
    const replay = buildReplay([{session:'a',geometry:{full:[{},{}],damaged:[{}]},records:[
        {at:1,kind:'sample',entity:'grid',layout:['full']},
        {at:2,kind:'sample',entity:'grid',high:true,layout:['damaged']},
        {at:3,kind:'sample',entity:'grid',high:true,layout:['damaged']},
        {at:4,kind:'removed',entity:'grid'},
        {at:5,kind:'damage',entity:'grid',value:10,count:2,source:'source',detail:'collision'},
    ]}]);
    const log = replayJournal(replay);
    assert.equal(log.length,4);
    assert.match(log[1].detail,/2 → 1 blocks/);
    assert.match(log[1].detail,/Incident detail/);
    assert.match(describeEvent(log[2]),/cause not inferred/);
    assert.match(describeEvent(log[3]),/10.0 damage in 2 hit\(s\).*source source/);
});

test('hover reports missing historical fields explicitly and retains names as plain text', () => {
    const text = diagnosticText({at:1000,type:'player',name:'<img onerror=evil>',entity:'p'},null,2000,'approximate');
    assert.match(text,/<img onerror=evil>/);
    assert.match(text,/Natural gravity: not recorded/);
    assert.match(text,/age 1s/);
    assert.match(text,/approximate/);
});

test("500m context prunes unrelated assets and stays anchored when focus changes", () => {
    const replay = buildReplay([{session:"s", geometry:{near:[{model:"near"}],far:[{model:"far"}]}, records:[
        {at:1000,kind:"health"},
        ...[["anchor",0],["edge",500],["outside",501]].map(([entity,x]) => ({at:1000,kind:"sample",entity,position:[x,0,0],layout:[entity==="outside"?"far":"near"]})),
    ]}]);
    const pruned = pruneReplayContext(replay,"anchor");
    assert.deepEqual([...pruned.tracks.keys()],["s/anchor","s/edge"]);
    assert.deepEqual([...pruned.geometry.keys()],["near"]);
    assert.equal(frameAt(pruned,1000,"edge").samples.length,2);
});
test("terrain never borrows future observations or stale checkpoints after an edit or gap", () => {
    const replay = buildReplay([{session:"s",voxels:{before:{},after:{}},records:[
        {at:1000,kind:"voxel-sample",entity:"v",terrainId:"before",value:1000},
        {at:1500,kind:"voxel-invalidated",entity:"v"},
        {at:1600,kind:"voxel-sample",entity:"v",terrainId:"before",value:1000},
        {at:2000,kind:"voxel-sample",entity:"v",terrainId:"after",value:2000},
        {at:3000,kind:"gap"},
    ]}]);
    assert.equal(terrainAt(replay,999,"s").length,0);
    assert.equal(terrainAt(replay,1499,"s")[0].terrainId,"before");
    assert.equal(terrainAt(replay,1700,"s").length,0);
    assert.equal(terrainAt(replay,2000,"s")[0].terrainId,"after");
    assert.equal(terrainAt(replay,3001,"s").length,0);
    assert.equal(terrainAt(replay,2000,"other").length,0);
});
