using System.Diagnostics;
using System.Text.Json;
using CometWorks.EntityViewer.Replay;
using CometWorks.EntityViewer.Magnetar.Replay;

// Repeatable CPU/allocation microbenchmarks of production recorder classes, not a live server claim.
var results = new List<object>();
Measure("idle-1000-grids-60s", () => {
    var schedules = Enumerable.Range(0,1000).Select(_=>new ReplaySchedule()).ToArray();
    return () => {
        int written=0;
        for(var tick=0;tick<60;tick++) foreach(var schedule in schedules) {
#if BASELINE
            var r = new ReplayRecord();
#else
            var r = schedule.RentSample();
#endif
            r.Elapsed=tick*1000; r.Position=Vector(r.Position,1,2,3);r.Origin=Vector(r.Origin,0,0,0);
            r.Velocity=Vector(r.Velocity,0,0,0);r.AngularVelocity=Vector(r.AngularVelocity,0,0,0);
            r.Size=Vector(r.Size,10,10,10);r.Gravity=Vector(r.Gravity,0,-9.81,0);
            r.Rotation ??= [0,0,0,1];
            if(schedule.Observe(r,tick*1000,out _)) written++;
        }
        if(written!=2000) throw new Exception("Idle heartbeat changed");
    };
});
Measure("incident-100000-stationary-triggers", () => {
    int snapshots=0;var tracker=new ReplayIncidentTracker(_=>snapshots++);
    var settings=new ReplayRecordingSettings { MaxIncidentMinutes=60 };double[] position=[0,0,0];
    return ()=> {for(var i=0;i<100000;i++) tracker.Observe(new ReplayRecord {Kind="damage",At=1700000000000+i*16,Position=position},i*16,settings);
        if(tracker.Count!=1 || snapshots!=100000) throw new Exception("Activity metadata changed");};
});
Measure("context-100000-lookups-32-incidents", () => {
    var tracker=new ReplayIncidentTracker(_=>{});var settings=new ReplayRecordingSettings();
    for(var i=0;i<32;i++) tracker.Observe(new ReplayRecord {Kind="damage",At=1700000000000,Position=[i*20000,0,0]},0,settings);
    double[] position=[620000,0,0];
    return ()=> {double sum=0;for(var i=0;i<100000;i++) {
#if BASELINE
        sum+=tracker.HighUntil(position,1);
#else
        sum+=tracker.HighUntil(position[0],position[1],position[2],1);
#endif
    } if(sum!=6000000000) throw new Exception("Context changed");};
});
Measure("layout-10000-samples-50000-blocks", () => {
    var layouts=new ReplayLayoutBuilder();layouts.Apply(new ReplayRecord {Kind="layout-reset",Entity="g",Count=50000});
    for(var i=0;i<50000;i++) layouts.Apply(new ReplayRecord {Kind="layout-block",Entity="g",Block=new ReplayBlock {Cell=[i,0,0],Min=[i,0,0],Max=[i,0,0],Translation=[i*2.5,0,0],Rotation=[0,0,0,1],Color=[0,0,0],Definition="Armor",Model="Models/Armor.mwm",Build=1,Integrity=1}});
    layouts.Apply(new ReplayRecord {Kind="layout-ready",Entity="g"});
    var sample=new ReplayRecord {Kind="sample",Entity="g",GeometryReady=true};
    layouts.Attach(sample);
    return ()=> {long pages=0;for(var i=0;i<10000;i++) pages+=layouts.Attach(sample).Count;if(pages!=2570000) throw new Exception("Layout dependencies changed");};
});
var archivePath = Path.Combine(Path.GetTempPath(), "recorder-benchmark-" + Guid.NewGuid().ToString("N"));
try {
    Directory.CreateDirectory(archivePath);
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var data = ReplayChunkStore.Compress([1]);
    for (var i = 0; i < 5000; i++) File.WriteAllBytes(Path.Combine(archivePath, ReplayChunkStore.NewId(now - i, 0) + ".gz"), data);
    var archive = new ReplayChunkStore(archivePath, 64L * 1024 * 1024);
    Measure("retention-1000-checks-5000-segments", () => () => { for (var i = 0; i < 1000; i++) archive.Prune(now); });
    if (archive.List(now).Length != 5000 || archive.LostChunks != 0) throw new Exception("Retention changed");
} finally { Directory.Delete(archivePath, true); }
Console.WriteLine(JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
void Measure(string name,Func<Action> prepare) {
    for(int warm=0;warm<2;warm++) prepare()();
    var times=new List<double>();var allocations=new List<long>();
    for(int run=0;run<5;run++) {
        var action=prepare();GC.Collect();GC.WaitForPendingFinalizers();
        var bytes=GC.GetAllocatedBytesForCurrentThread();var started=Stopwatch.GetTimestamp();action();
        times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-bytes);
    }
    times.Sort();allocations.Sort();results.Add(new {name,milliseconds=times[2],allocatedBytes=allocations[2]});
}
static double[] Vector(double[]? target,double x,double y,double z) {target??=new double[3];target[0]=x;target[1]=y;target[2]=z;return target;}
