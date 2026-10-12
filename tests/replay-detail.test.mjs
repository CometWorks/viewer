import test from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from 'three';
import { replayViewRotation, diagnosticText } from '../src/CometWorks.EntityViewer/wwwroot/replay-scene.js';
import { buildReplay, replayJournal, describeEvent } from '../src/CometWorks.EntityViewer/wwwroot/replay-model.js';

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
