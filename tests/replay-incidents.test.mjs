import test from 'node:test';
import assert from 'node:assert/strict';
import { buildReplay, pruneReplayContext, frameAt, incidentClips, replayJournal, terrainAt } from '../src/CometWorks.EntityViewer/wwwroot/replay-model.js';
const meta = (at, radius = 500, extra = {}) => ({at,kind:'incident',incidentId:extra.id || 'a',incident:{id:'a',startedAt:10000,lastActivityAt:at,until:at+60000,deadline:910000,seed:[0,0,0],radius,maxRadius:10000,...extra}});
const sample = (at, entity, x, layout) => ({at,kind:'sample',entity,position:[x,0,0],layout:layout ? [layout] : undefined});
const health = at => ({at,kind:'health',sun:[1,0,0],sunIntensity:1});
const event = (at, entity, x, incidentId) => ({at,kind:'damage',entity,position:[x,0,0],incidentId});
const replay = (records, extra = {}) => buildReplay([{session:'s',records,...extra}]);

test('recorded contexts expand to 10km and reverse seeks do not use future bounds', () => {
    const r = pruneReplayContext(replay([
        health(10000), sample(10000,'g',0,'near'), sample(10000,'distant',9000,'far'), sample(10000,'outside',12000,'outside'),
        meta(10000),event(10000,'g',0,'a'),event(11000,'distant',9000),
        health(20000),meta(20000,10000),event(20000,'distant',9000,'a'),
    ], {geometry:{near:[],far:[],outside:[]}}), 'g');
    assert.deepEqual([...r.geometry.keys()],['near','far']);
    assert.deepEqual(frameAt(r,10000).samples.map(s=>s.entity),['g']);
    assert.deepEqual(frameAt(r,20000).samples.map(s=>s.entity),['g','distant']);
    assert.deepEqual(frameAt(r,10000).samples.map(s=>s.entity),['g']);
    assert.equal(replayJournal(r).some(e=>e.kind==='damage' && e.at===11000),false);
    assert.equal(incidentClips(r,0,90000).length,1);
});

test('partial ranges retain authoritative boundaries even without trigger events', () => {
    const r = pruneReplayContext(replay([health(300000),sample(300000,'g',0),meta(300000,5000)]),'g');
    const [clip] = incidentClips(r,300000,330000);
    assert.equal(clip.events.length,0);
    assert.equal(clip.startedAt,10000);
    assert.equal(clip.actualEnd,360000);
    assert.equal(clip.start,300000);
    assert.equal(clip.end,330000);
    assert.equal(clip.partial,true);
    assert.equal(frameAt(r,300000).contexts[0].radius,5000);
});

test('merged incident identities form one clip and preserve earlier contexts', () => {
    const r = pruneReplayContext(replay([
        health(10000),sample(10000,'g',0),sample(10000,'b',1800),meta(10000),event(10000,'g',0,'a'),
        meta(11000,500,{id:'b',seed:[1800,0,0],startedAt:11000}),event(11000,'b',1800,'b'),
        meta(12000,500,{id:'b',seed:[1800,0,0],startedAt:11000,until:12000,ended:true,mergedInto:'a'}),
        meta(12000,2300,{mergedIds:['b']}),event(12000,'g',900,'a'),
    ]),'g');
    const clips = incidentClips(r,0,90000);
    assert.equal(clips.length,1); assert.equal(clips[0].events.length,3);
    assert.deepEqual(frameAt(r,10000).samples.map(s=>s.entity),['g']);
    assert.deepEqual(frameAt(r,12000).samples.map(s=>s.entity),['g','b']);
});

test('one-hour incidents trim pre-roll so the complete clip remains queryable', () => {
    const r = replay([meta(3610000,500,{lastActivityAt:3600000,until:3610000,deadline:3610000,ended:true,reason:'duration'})]);
    const [clip] = incidentClips(r,0,4000000);
    assert.equal(clip.actualEnd-clip.actualStart,3600000);
    assert.equal(clip.actualStart,10000);
});

test('explicit incident selection ignores unrelated anchor events and prepares nearby terrain', () => {
    const r = pruneReplayContext(replay([
        health(10000),sample(10000,'g',0),sample(10000,'other',15000),meta(10000),
        event(11000,'other',15000),
        {at:10000,kind:'voxel-sample',entity:'v',terrainId:'terrain',position:[3000,0,0],value:10000},
        meta(12000,4000),
    ],{voxels:{terrain:{size:[33,33,33],lod:0}}}),'other','a');
    assert.equal(replayJournal(r).some(e=>e.kind==='damage'),false);
    assert.equal(r.voxels.has('terrain'),true);
    assert.equal(terrainAt(r,10000,'s').length,0);
    assert.equal(terrainAt(r,12000,'s').length,1);
});

test('renewed deadlines and resource gaps remain visible in incident clips', () => {
    const r = replay([meta(10000),event(69000,'g',0,'a'),meta(69000),{at:70000,kind:'gap'}]);
    const [clip] = incidentClips(r,0,150000);
    assert.equal(clip.actualEnd,129000);
    assert.equal(clip.incomplete,true);
});


test('anonymous remote explosions do not attach unrelated incidents to the selected entity', () => {
    const r = pruneReplayContext(replay([
        health(10000),sample(10000,'g',0),sample(10000,'remote',9000),
        meta(10000,500,{seed:[9000,0,0]}),{at:10000,kind:'explosion',entity:'',position:[9000,0,0],incidentId:'a'},
    ]),'g');
    assert.equal(r.contextIncidentIds.size,0);
    assert.equal(r.tracks.has('s/remote'),false);
});
