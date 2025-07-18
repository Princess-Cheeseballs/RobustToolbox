using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using JetBrains.Annotations;
using Robust.Client.ComponentTrees;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Robust.Client.GameObjects
{
    /// <summary>
    /// Updates the layer animation for every visible sprite.
    /// </summary>
    [UsedImplicitly]
    public sealed partial class SpriteSystem : EntitySystem
    {
        [Dependency] private readonly IConfigurationManager _cfg = default!;
        [Dependency] private readonly IEyeManager _eye = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPrototypeManager _proto = default!;
        [Dependency] private readonly IResourceCache _resourceCache = default!;
        [Dependency] private readonly ILogManager _logManager = default!;
        [Dependency] private readonly IComponentFactory _factory = default!;

        // Note that any new system dependencies have to be added to RobustUnitTest.BaseSetup()
        [Dependency] private readonly SharedTransformSystem _xforms = default!;
        [Dependency] private readonly SpriteTreeSystem _tree = default!;
        [Dependency] private readonly AppearanceSystem _appearance = default!;

        public static readonly ProtoId<ShaderPrototype> UnshadedId = "unshaded";
        private readonly Queue<SpriteComponent> _inertUpdateQueue = new();

        public static readonly ResPath TextureRoot = SpriteSpecifierSerializer.TextureRoot;

        /// <summary>
        ///     Entities that require a sprite frame update.
        /// </summary>
        private readonly HashSet<EntityUid> _queuedFrameUpdate = new();

        private ISawmill _sawmill = default!;
        private EntityQuery<SpriteComponent> _query;

        public override void Initialize()
        {
            base.Initialize();

            UpdatesAfter.Add(typeof(SpriteTreeSystem));

            SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
            SubscribeLocalEvent<SpriteComponent, ComponentInit>(OnInit);

            Subs.CVar(_cfg, CVars.RenderSpriteDirectionBias, OnBiasChanged, true);
            _sawmill = _logManager.GetSawmill("sprite");
            _query = GetEntityQuery<SpriteComponent>();
        }

        public bool IsVisible(Layer layer)
        {
            return layer.Visible && layer.CopyToShaderParameters == null;
        }

        private void OnInit(EntityUid uid, SpriteComponent component, ComponentInit args)
        {
            // I'm not 100% this is needed, but I CBF with this ATM. Somebody kill server sprite component please.
            QueueUpdateInert(uid, component);
        }

        private void OnBiasChanged(double value)
        {
            SpriteComponent.DirectionBias = value;
        }

        private void DoUpdateIsInert(SpriteComponent component)
        {
            component._inertUpdateQueued = false;
            component.IsInert = true;

            foreach (var layer in component.Layers)
            {
                // Since StateId is a struct, we can't null-check it directly.
                if (!layer.State.IsValid || !layer.Visible || !layer.AutoAnimated || layer.Blank)
                {
                    continue;
                }

                var rsi = layer.RSI ?? component.BaseRSI;
                if (rsi == null || !rsi.TryGetState(layer.State, out var state))
                {
                    state = GetFallbackState();
                }

                if (state.IsAnimated)
                {
                    component.IsInert = false;
                    break;
                }
            }
        }

        /// <inheritdoc />
        public override void FrameUpdate(float frameTime)
        {
            while (_inertUpdateQueue.TryDequeue(out var sprite))
            {
                DoUpdateIsInert(sprite);
            }

            var realtime = _timing.RealTime.TotalSeconds;
            var spriteQuery = GetEntityQuery<SpriteComponent>();
            var syncQuery = GetEntityQuery<SyncSpriteComponent>();
            var metaQuery = GetEntityQuery<MetaDataComponent>();

            foreach (var uid in _queuedFrameUpdate)
            {
                if (!spriteQuery.TryGetComponent(uid, out var sprite) ||
                    metaQuery.GetComponent(uid).EntityPaused)
                {
                    continue;
                }

                if (sprite.IsInert)
                    continue;

                var sync = syncQuery.HasComponent(uid);

                foreach (var layer in sprite.Layers)
                {
                    if (!layer.State.IsValid || !layer.Visible || !layer.AutoAnimated)
                        continue;

                    var rsi = layer.RSI ?? sprite.BaseRSI;
                    if (rsi == null || !rsi.TryGetState(layer.State, out var state))
                        state = GetFallbackState();

                    if (!state.IsAnimated)
                        continue;

                    if (sync)
                    {
                        layer.AnimationTime = (float)(realtime % state.TotalDelay);
                        layer.AnimationTimeLeft = -layer.AnimationTime;
                        layer.AnimationFrame = 0;
                    }
                    else
                    {
                        layer.AnimationTime += frameTime;
                        layer.AnimationTimeLeft -= frameTime;
                    }

                    layer.AdvanceFrameAnimation(state);
                }
            }

            _queuedFrameUpdate.Clear();
        }

        /// <summary>
        ///     Force update of the sprite component next frame
        /// </summary>
        public void ForceUpdate(EntityUid uid)
        {
            _queuedFrameUpdate.Add(uid);
        }

        /// <summary>
        /// Gets the specified frame for this sprite at the specified time.
        /// </summary>
        /// <param name="loop">Should we clamp on the last frame and not loop</param>
        public Texture GetFrame(SpriteSpecifier spriteSpec, TimeSpan curTime, bool loop = true)
        {
            Texture? sprite = null;

            switch (spriteSpec)
            {
                case SpriteSpecifier.Rsi rsi:
                    var rsiActual = _resourceCache.GetResource<RSIResource>(rsi.RsiPath).RSI;
                    rsiActual.TryGetState(rsi.RsiState, out var state);
                    var frames = state!.GetFrames(RsiDirection.South);
                    var delays = state.GetDelays();
                    var totalDelay = delays.Sum();

                    // No looping
                    if (!loop && curTime.TotalSeconds >= totalDelay)
                    {
                        sprite = frames[^1];
                    }
                    // Loopable
                    else
                    {
                        var time = curTime.TotalSeconds % totalDelay;
                        var delaySum = 0f;

                        for (var i = 0; i < delays.Length; i++)
                        {
                            var delay = delays[i];
                            delaySum += delay;

                            if (time > delaySum)
                                continue;

                            sprite = frames[i];
                            break;
                        }
                    }

                    sprite ??= Frame0(spriteSpec);
                    break;
                case SpriteSpecifier.Texture texture:
                    sprite = texture.GetTexture(_resourceCache);
                    break;
                default:
                    throw new NotImplementedException();
            }

            return sprite;
        }
    }

    /// <summary>
    ///     This event gets raised before a sprite gets drawn using it's post-shader.
    /// </summary>
    public sealed class BeforePostShaderRenderEvent : EntityEventArgs
    {
        public readonly SpriteComponent Sprite;
        public readonly IClydeViewport Viewport;

        public BeforePostShaderRenderEvent(SpriteComponent sprite, IClydeViewport viewport)
        {
            Sprite = sprite;
            Viewport = viewport;
        }
    }
}
