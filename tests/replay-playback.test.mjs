import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { buildReplay, frameAt, incidentClips, evidenceAt, observationStep } from "../src/CometWorks.EntityViewer/wwwroot/replay-model.js";
import { createReplayCharacter, animateReplayCharacter } from "../src/CometWorks.EntityViewer/wwwroot/replay-character.js";
const health = at => ({at, kind:"health", value:1});
const sample = (at, x = 0, extra = {}) => ({at,kind:"sample",entity:"g",position:[x,0,0],origin:[x,0,0],rotation:[0,0,0,1],velocity:[10,0,0],...extra});
const replayOf = records => buildReplay([{session:"s",records:[health(1000),...records]}]);

test("interpolation moves transforms but never anticipates geometry or actions", () => {
    const replay = replayOf([sample(1000,0,{layout:["intact"]}),sample(2000,10,{layout:["destroyed"]})]);
    const reconstructed = frameAt(replay,1500,"",true).samples[0];
    assert.deepEqual(reconstructed.position,[5,0,0]);
    assert.deepEqual(reconstructed.layout,["intact"]);
    assert.equal(reconstructed.at,1000);
    assert.equal(reconstructed.interpolated,true);
    assert.equal(frameAt(replay,1500).samples[0].position[0],0);
    assert.equal(observationStep(replay,1500,1),2000);
    assert.equal(observationStep(replay,1500,-1),1000);
});
test("gaps, teleports, removals, control changes and sparse observations cannot interpolate", () => {
    for (const following of [[sample(2000,1000)], [{at:1750,kind:"gap"},sample(2000,10)], [{at:1750,kind:"merge",entity:"g",related:"other"},sample(2000,10)], [{at:2000,entity:"g",kind:"removed"}], [sample(3000,10)], [sample(2000,10,{source:"new controller"})]]) {
        const replay = replayOf([sample(1000),...following]);
        assert.equal(frameAt(replay,1500,"",true).samples[0].interpolated,undefined);
    }
});
test("short firing transitions and death play and reverse without requiring another transform", () => {
    const replay = replayOf([sample(1000,0,{type:"player",character:"c",actions:0}),
        {at:1100,kind:"action",entity:"c",actions:2}, {at:1200,kind:"action",entity:"c",actions:0}, {at:1400,kind:"death",entity:"c",actions:64}]);
    for (const [at, flags] of [[1150,2],[1250,0],[1450,64],[1150,2],[1050,0]]) assert.equal(frameAt(replay,at).samples[0].actions,flags);
});
test("incidents group nearby damage and lineage with coarse pre-roll and quiet tail", () => {
    const replay = replayOf([{at:12000,kind:"damage",entity:"g",position:[0,0,0]},
        {at:13000,kind:"split",entity:"g",related:"fragment"}, {at:14000,kind:"damage",entity:"fragment"},
        {at:15000,kind:"gap"}, {at:50000,kind:"damage",entity:"g"}]);
    const clips = incidentClips(replay,1000,60000);
    assert.equal(clips.length,2); assert.equal(clips[0].start,2000); assert.equal(clips[0].end,29000);
    assert.equal(clips[0].events.length,3); assert.equal(clips[0].incomplete,true);
    assert.equal(clips[1].end,60000);
});
test("incident clips and historical attribution never cross sessions or guess ownership", () => {
    const records = [health(1000),sample(1000,0,{type:"player",entity:"p",character:"c",source:"seat",weapon:"gun",name:"Ada"}),{at:1500,kind:"damage",entity:"g",source:"gun"}];
    const replay = buildReplay([{session:"s",records},{session:"new",records:[health(1600),{at:1800,kind:"damage",entity:"g",source:"gun"}]}]);
    assert.equal(incidentClips(replay,1000,30000).length,2);
    assert.equal(incidentClips(replay,1000,30000)[0].end,1599);
    assert.equal(evidenceAt(replay,replay.events[0]).historicalController.name,"Ada");
    assert.equal(evidenceAt(replay,replay.events[1]).historicalController,"unknown");
    assert.equal(evidenceAt(replay,{...replay.events[0],source:"owned-grid"}).historicalController,"unknown");
});
test("GPU character animation is deterministic when scrubbing and restores the asset bind pose", () => {
    const geometry = new THREE.BufferGeometry(); geometry.setAttribute("position",new THREE.Float32BufferAttribute([0,0,0,0,1,0,1,0,0],3));
    const model = {geometry,materials:[new THREE.MeshStandardMaterial()],skin:{bones:[{name:"SE_RigLThigh",parent:-1,transform:new THREE.Matrix4().toArray()}],indices:new Uint8Array(12),weights:new Float32Array([1,0,0,0,1,0,0,0,1,0,0,0])}};
    const mesh = createReplayCharacter(model,{}); assert.equal(mesh.isSkinnedMesh,true);
    const player = {actions:16,velocity:[4,0,0]};
    animateReplayCharacter(mesh,player,1000); const pose = mesh.skeleton.bones[0].quaternion.toArray();
    animateReplayCharacter(mesh,player,2000); animateReplayCharacter(mesh,player,1000);
    assert.deepEqual(mesh.skeleton.bones[0].quaternion.toArray(),pose);
    animateReplayCharacter(mesh,player,1000,false); assert.deepEqual(mesh.skeleton.bones[0].quaternion.toArray(),[0,0,0,1]);
    mesh.skeleton.dispose(); mesh.geometry.dispose(); mesh.material.forEach(m=>m.dispose()); geometry.dispose(); model.materials[0].dispose();
});

test("gapped and ambiguous control evidence stays unknown", () => {
    const records = [sample(1000,0,{entity:"p",type:"player",source:"seat"}),{at:1200,kind:"gap"},sample(1300,0,{entity:"p",type:"player",source:"seat"}),{at:1500,kind:"damage",entity:"g",source:"seat"}];
    const replay = replayOf(records);
    assert.equal(evidenceAt(replay,replay.events.at(-1)).historicalController,"unknown");
    const ambiguous = replayOf([records[0],sample(1000,0,{entity:"p2",type:"player",source:"seat"}),records.at(-1)]);
    assert.equal(evidenceAt(ambiguous,ambiguous.events[0]).historicalController,"unknown");
});
