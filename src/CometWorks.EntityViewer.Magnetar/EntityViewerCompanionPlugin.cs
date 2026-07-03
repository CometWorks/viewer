using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CometWorks.EntityViewer.Magnetar.Model;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginSdk;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Plugins;

namespace CometWorks.EntityViewer.Magnetar
{
    public sealed class EntityViewerCompanionPlugin : IPlugin, IQuasarCompanionRequestHandler
    {
        private const string GetEntitySceneOperation = "get-entity-scene";

        private static readonly JsonSerializerSettings PayloadJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly SemaphoreSlim _sceneBuildGate = new SemaphoreSlim(1, 1);
        private string _pluginVersion = string.Empty;

        public string PluginId => "cometworks.entityviewer";

        public void Init(object gameServer)
        {
            _pluginVersion = GetPluginVersion();
            EmissivePartCapturePatches.Apply();
        }

        public void Update()
        {
        }

        public void Dispose()
        {
            EmissivePartCapturePatches.Dispose();
            _sceneBuildGate.Dispose();
        }

        public Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(
            CompanionPluginRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Operation, GetEntitySceneOperation, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Fail(request, $"Unsupported Entity Viewer operation '{request.Operation}'."));
            }

            return CaptureSceneAsync(request, cancellationToken);
        }

        private async Task<CompanionPluginResponse> CaptureSceneAsync(
            CompanionPluginRequest companionRequest,
            CancellationToken cancellationToken)
        {
            var request = JsonConvert.DeserializeObject<EntityRenderSceneRequest>(
                companionRequest.PayloadJson ?? string.Empty,
                PayloadJsonSettings);
            if (request == null || request.EntityId == 0)
                return Fail(companionRequest, "Viewer scene request is missing an entity id.");

            await _sceneBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var work = await ResolveSceneWorkOnGameThreadAsync(companionRequest, request, cancellationToken).ConfigureAwait(false);
                if (work.Response != null)
                    return work.Response;

                return await Task.Run(() =>
                {
                    try
                    {
                        EntityRenderScene scene;
                        if (work.Grid != null)
                            scene = GridRenderSceneInspector.Build(work.Grid, work.GameVersion, _pluginVersion, request.IncludeVoxels, request.IncludeContext);
                        else if (work.Voxel != null)
                            scene = GridRenderSceneInspector.BuildVoxel(work.Voxel, work.GameVersion, _pluginVersion, request.IncludeVoxels);
                        else
                            return Fail(companionRequest, "Viewer entity not found or not loaded on this server.");

                        return Ok(companionRequest, scene);
                    }
                    catch (Exception exception)
                    {
                        return Fail(companionRequest, exception.Message);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sceneBuildGate.Release();
            }
        }

        private static Task<SceneWork> ResolveSceneWorkOnGameThreadAsync(
            CompanionPluginRequest companionRequest,
            EntityRenderSceneRequest request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<SceneWork>();
            var game = MySandboxGame.Static;
            if (game == null)
                return Task.FromResult(new SceneWork { Response = Fail(companionRequest, "Game server not available.") });

            cancellationToken.Register(() => completion.TrySetCanceled());
            game.Invoke(() =>
            {
                try
                {
                    completion.TrySetResult(ResolveSceneWorkOnGameThread(companionRequest, request));
                }
                catch (Exception exception)
                {
                    completion.TrySetResult(new SceneWork { Response = Fail(companionRequest, exception.Message) });
                }
            }, "EntityViewer:ResolveScene");

            return completion.Task;
        }

        private static SceneWork ResolveSceneWorkOnGameThread(
            CompanionPluginRequest companionRequest,
            EntityRenderSceneRequest request)
        {
            if (MySession.Static == null || !MySession.Static.Ready)
                return new SceneWork { Response = Fail(companionRequest, "Session not ready.") };

            var work = new SceneWork
            {
                GameVersion = MySession.Static?.AppVersionFromSave.ToString() ?? string.Empty,
            };

            if (MyEntities.TryGetEntityById<MyCubeGrid>(request.EntityId, out var grid) && grid != null && !grid.MarkedForClose && !grid.Closed)
                work.Grid = grid;
            else if (MyEntities.TryGetEntityById<MyVoxelBase>(request.EntityId, out var voxel) && voxel != null && !voxel.MarkedForClose && !voxel.Closed)
                work.Voxel = voxel;
            else
                work.Response = Fail(companionRequest, "Viewer entity not found or not loaded on this server.");

            return work;
        }

        private static CompanionPluginResponse Ok(CompanionPluginRequest request, object payload)
        {
            return new CompanionPluginResponse
            {
                CorrelationId = request.CorrelationId,
                Success = true,
                PayloadJson = JsonConvert.SerializeObject(payload, PayloadJsonSettings),
            };
        }

        private static CompanionPluginResponse Fail(CompanionPluginRequest request, string error)
        {
            return new CompanionPluginResponse
            {
                CorrelationId = request.CorrelationId,
                Success = false,
                Error = string.IsNullOrWhiteSpace(error) ? "Entity Viewer companion request failed." : error,
            };
        }

        private static string GetPluginVersion()
        {
            var assembly = typeof(EntityViewerCompanionPlugin).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? string.Empty;
        }

        private sealed class SceneWork
        {
            public string GameVersion { get; set; }

            public MyCubeGrid Grid { get; set; }

            public MyVoxelBase Voxel { get; set; }

            public CompanionPluginResponse Response { get; set; }
        }
    }
}
