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
    const player = {actions:16,movement:2064,velocity:[4,0,0]};
    animateReplayCharacter(mesh,player,1000); const pose = mesh.skeleton.bones[0].quaternion.toArray();
    animateReplayCharacter(mesh,player,2000); assert.notDeepEqual(mesh.skeleton.bones[0].quaternion.toArray(),pose); animateReplayCharacter(mesh,player,1000);
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

test("character material clones share GPU textures without invoking JSON serialization", async () => {
    const { cloneModelMaterial } = await import("../src/CometWorks.EntityViewer/wwwroot/model-material.js");
    const texture = new THREE.Texture(); texture.toJSON = () => { throw new Error("must not serialize texture"); };
    const original = new THREE.MeshStandardMaterial({ map: texture });
    original.userData.seColorMaskUniforms = { map: { value: texture }, color: { value: new THREE.Vector3(1, 2, 3) } };
    original.onBeforeCompile = function(shader) { Object.assign(shader.uniforms, this.userData.seColorMaskUniforms); };
    const cloned = cloneModelMaterial(original);
    assert.equal(cloned.map, texture);
    assert.equal(cloned.userData.seColorMaskUniforms.map.value, texture);
    assert.notEqual(cloned.userData.seColorMaskUniforms.color.value, original.userData.seColorMaskUniforms.color.value);
    const shader = { uniforms: {} }; cloned.onBeforeCompile(shader);
    assert.equal(shader.uniforms.color, cloned.userData.seColorMaskUniforms.color);
    cloned.dispose(); original.dispose(); texture.dispose();
});


test("movement transitions seek both ways without inventing movement across gaps", () => {
    const replay = replayOf([sample(1000,0,{type:"player",character:"c",movement:16}),
        {at:1100,kind:"movement",entity:"c",movement:3}, {at:1200,kind:"movement",entity:"c",movement:4},
        {at:1400,kind:"gap"}, sample(1500,0,{type:"player",character:"c",movement:0})]);
    for (const [at, state] of [[1050,16],[1150,3],[1250,4],[1550,0],[1150,3]]) assert.equal(frameAt(replay,at).samples[0].movement,state);
    assert.equal(frameAt(replay,1450).samples.length,0);
});

test("world velocity cannot make flying, falling or standing characters walk", async () => {
    const { movementState } = await import("../src/CometWorks.EntityViewer/wwwroot/replay-movement.js");
    for (const [state, label] of [[0,"standing"],[3,"flying"],[4,"falling"],[16,"walking"],[1040,"running"],[2064,"sprinting"],[18,"crouch walking"],[263,"climbing up"],[519,"climbing down"],[undefined,"movement not recorded"]]) assert.equal(movementState(state), label);
    const bone = new THREE.Bone(); bone.name = "SE_RigLThigh";
    bone.userData = {bindQuaternion:new THREE.Quaternion(),bindPosition:new THREE.Vector3(),motionAxis:new THREE.Vector3(1,0,0)};
    const mesh = {isSkinnedMesh:true,skeleton:{bones:[bone]}};
    for (const movement of [0,3,4,5,undefined]) {
        const sample = {movement,actions:0,velocity:[100,0,0]};
        animateReplayCharacter(mesh,sample,1000); const pose = bone.quaternion.toArray();
        animateReplayCharacter(mesh,sample,2000); assert.deepEqual(bone.quaternion.toArray(),pose);
    }
    animateReplayCharacter(mesh,{movement:16},1000); const walking = bone.quaternion.toArray();
    animateReplayCharacter(mesh,{movement:16},2000); assert.notDeepEqual(bone.quaternion.toArray(),walking);
});
