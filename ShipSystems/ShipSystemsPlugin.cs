using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Y4NGZCore.Diagnostics;
using Y4NGZCore.Lifecycle;

namespace Y4NGZCompany.ShipSystems
{
    /// <summary>
    /// The Ship Systems plugin: fuel, power and the battery, ship layout/upgrades and refits, the
    /// monitor row and its banners, the custom terminal's moons and constellations, and the
    /// custom store and its purchase networking.
    ///
    /// <para><b>#613 task 2.1.</b> This type was created in #612 task 1.4 as a plain static entry
    /// point called from <c>Y4NGZCompany.Plugin.Awake</c>, so that Contracted named one Ship
    /// Systems symbol instead of three bootstraps. It is now the real
    /// <see cref="BepInPlugin"/> of its own assembly, and the body of <see cref="Awake"/> is the
    /// same three <c>InitializeModule</c> calls in the same order, plus the lifecycle Contracted
    /// used to perform on this feature's behalf: its own Harmony owner, its own host object, its
    /// own config file, and its own log channels.</para>
    ///
    /// <para><b>Namespace, not assembly.</b> Everything in this assembly keeps the
    /// <c>Y4NGZCompany.*</c> namespaces it has always had — companion mods resolve those by
    /// string and the plan freezes them. Only the assembly changed.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    // Hard: this assembly compiles against Y4NGZCore (the module contract layer, the monitor
    // arbiter, the UI theme substrate, the shared facility audio loader) and must load after it.
    [BepInDependency("com.y4ngz.core", "1.0.10")]
    // Hard: the custom terminal presentation is built on InteractiveTerminalAPI, and DawnLib is a
    // compile-time reference of the moon-catalogue integration.
    [BepInDependency("WhiteSpike.InteractiveTerminalAPI", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.github.teamxiaolan.dawnlib", BepInDependency.DependencyFlags.HardDependency)]
    // NO dependency on com.y4ngz.company is declared here, and none may be added. #613 shipped
    // one, and Contracted declares the mirror-image soft dependency on this plugin
    // (Y4NGZCompany/Plugin.cs). BepInEx 5 counts SOFT dependencies as edges in the chainloader's
    // topological sort, so the pair is a two-node cycle: Chainloader.Start threw
    //   "Cyclic Dependency: com.y4ngz.company.shipsystems / com.y4ngz.company /
    //    com.y4ngz.betterarmory"
    // and ZERO of the profile's 121 plugins loaded. (BetterArmory only soft-depends on
    // Contracted; it was on the DFS stack, not part of the cycle.)
    //
    // Contracted's edge is the one that was kept, so the order is Core -> Ship Systems ->
    // Contracted whenever both are installed. The edge that used to be here claimed Contracted
    // had to chainload first "so the UI theme it publishes into Y4NGZCore is already bound before
    // the first monitor-row page is drawn". That claim was checked against the code and is false
    // on both halves:
    //
    //   * Nothing in this assembly reads the theme at Awake. Every read goes through
    //     Y4NGZCore's UiThemeState (ShipUiTheme, both TerminalPalettes, MonitorRowCardCache) and
    //     every one of them is a computed property evaluated at paint time, not a snapshot taken
    //     at load. Unpublished, UiThemeState returns HudColorPreset.Default / the Orange palette
    //     — documented there as the same colours a default-config Contracted profile renders.
    //   * The first monitor-row page cannot be drawn before a lobby exists.
    //     MonitorRowDirector is constructed in ShipSystemsController.EnsureWorldSystems, which
    //     runs only once IsShipWorldReady() sees StartOfRound.Instance.elevatorTransform in a
    //     loaded, active scene — many seconds after every plugin's Awake. It subscribes to
    //     UiThemeState.ThemeChanged at construction, so a theme Contracted publishes later still
    //     repaints the wall.
    //
    // So loading after Contracted buys nothing this plugin needs, and cost every plugin in the
    // profile. Do not reintroduce the attribute:
    // ModuleLifecycleRegression.NoDependencyCycleAmongTheY4NGZPlugins parses all six shipped
    // plugin sources and fails if any cycle among our own GUIDs is ever declared again.
    //
    // Soft: fuel prefab and item registration goes through LethalLib by reflection
    // (FuelRuntimeUtility), so it degrades cleanly, but it must chainload first to be found.
    [BepInDependency("evaisa.lethallib", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("imabatby.lethallevelloader", BepInDependency.DependencyFlags.SoftDependency)]
    // #666: the TerminalStuff/LethalConstellations soft deps moved to Y4NGZTerminalUpgrades
    // with the custom terminal, and the Wider Ship / 2 Story Ship / CustomPosters soft deps
    // moved with ShipLayoutModIntegration. This plugin is fuel and power only.
    public sealed class ShipSystemsPlugin : BaseUnityPlugin
    {
        /// <summary>
        /// This plugin's BepInEx GUID, which is also the Harmony owner id every Ship Systems
        /// patch is applied under. Aliased to the Core table so the three shipped GUIDs are
        /// declared in one place; the literal is frozen.
        /// </summary>
        public const string Guid = ModuleHarmonyIds.ShipSystems;

        /// <summary>
        /// Plugin display name — what BepInEx prints in the chainloader banner. NOT the config
        /// file's name: BepInEx 5 names <c>BaseUnityPlugin.Config</c> after the plugin GUID, so
        /// this plugin's file is <c>com.y4ngz.company.shipsystems.cfg</c>. See the config note in
        /// <see cref="Awake"/>.
        /// </summary>
        public const string Name = "ShipSystems";

        /// <summary>Plugin version.</summary>
        public const string Version = "1.0.1";

        /// <summary>
        /// The wire-protocol revision this build of Ship Systems speaks, advertised through
        /// <see cref="Y4NGZCore.Modules.Net.ModuleHandshakeService"/>. A peer running Ship Systems
        /// at a different revision is refused with a message naming both.
        ///
        /// <para><b>What it covers:</b> the ten frozen fuel/power <c>LGUShipSystems.*</c> named
        /// messages and their payload layouts, and the four woven RPCs on
        /// <c>FuelPumpItem</c> / <c>FuelSourceController</c>.</para>
        ///
        /// <para><b>When to bump it:</b> only when one of those payloads, or the host-authoritative
        /// meaning of one, changes such that a peer on the previous revision would misread it.
        /// <b>Not</b> for a <see cref="Version"/> change — bumping per release would make every
        /// patch refuse to play with itself, which is the failure mode this exists to avoid, not
        /// the one it exists to catch.</para>
        ///
        /// <para>Revision <c>1</c> was the #613 split build, whose manifest covered all
        /// eighteen messages plus the store's two. Revision <c>2</c> is the #666 build: the
        /// eight vote/upgrade/refit names and the store moved to Y4NGZTerminalUpgrades, so a
        /// revision-1 peer would expect this module to speak messages it no longer owns. The
        /// monolith predates the handshake and announces nothing, so a monolith peer is
        /// reported as "Ship Systems not installed" rather than as a mismatch.</para>
        /// </summary>
        public const int ShipSystemsProtocolRevision = 2;

        /// <summary>The Harmony owner id, by its historical name. Kept for call sites that read it.</summary>
        internal const string HarmonyId = Guid;

        /// <summary>
        /// Name of the GameObject that owns <see cref="ShipSystemsHost"/>. Every per-frame tick
        /// and every teardown this plugin performs hangs off that object's lifetime.
        /// </summary>
        internal const string HostObjectName = "Y4NGZShipSystems_Host";

        internal static ShipSystemsPlugin Instance { get; private set; }

        /// <summary>
        /// The MonoBehaviour anything in this assembly should run a coroutine on. Never the
        /// plugin component itself: that lives on BepInEx's shared, externally destroyable
        /// manager object, while this is our own. Same reasoning as Contracted's #393 note.
        /// </summary>
        internal static MonoBehaviour CoroutineHost { get; private set; }

        private Harmony _harmony;
        private ShipSystemsHost _host;

        /// <summary>
        /// Set by this component's own <c>OnApplicationQuit</c>, which Unity raises on every
        /// active MonoBehaviour before it starts destroying them. Read by
        /// <see cref="OnDestroy"/> only to choose the log line — never to change what is torn
        /// down. <see cref="ShipSystemsHost"/> keeps its own copy of the same flag because the
        /// two components can be destroyed in either order.
        /// </summary>
        private bool _applicationQuitting;

        private void Awake()
        {
            Instance = this;

            // Bind only this plugin's own three channels. Contracted binds its own, Monitor
            // Takeover binds its own. In the monolith all six were one source and the output was
            // byte-identical to the pre-facade build; now the three below carry this plugin's
            // BepInEx prefix and the other three carry theirs, with no edit at any call site.
            //
            // On a profile with no Contracted installed, the Contracts/Bundy/Takeover channels
            // are never bound at all. That is why ModuleLog's accessors return a null-safe
            // fallback rather than null: nothing in this assembly writes to those channels, but a
            // shared Core service reached from here must not throw because a channel its other
            // caller owns has no source.
            ModuleLog.ShipSystems = Logger;
            ModuleTickRegistry.WarningSink = message => Logger.LogWarning(message);

            SplitAssemblyDiagnostics.Report(Logger);

            // Import before the first Bind so BepInEx resolves migrated values, not defaults.
            ShipSystemsConfigMigration.MigrateConfigSafely(Config, Logger);

            // #393: not on BepInEx's shared "BepInEx_Manager" object. With the BepInEx default
            // (Chainloader/HideManagerGameObject=false) that object is findable by name and any
            // mod doing Find-based cleanup can destroy it plus every plugin component on it. A
            // self-owned, hide-flagged host is unreachable by name, so our lifetime is ours alone.
            var hostObject = new GameObject(HostObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<ShipSystemsHost>();
            CoroutineHost = _host;

            // Runs THIS assembly's NetcodePatcher runtime initializers. RPC registration is
            // per-assembly: patching Y4NGZCompany.dll does nothing for this one, and the
            // Initialize overload has no "current assembly" default precisely so nobody can pass
            // Y4NGZCore by accident.
            //
            // #613 task 2.2 verified what this actually finds, against a netcode-patched copy of
            // this assembly (NetcodePatcher 4.5.0, the same arguments the Release target uses):
            //
            //   * FuelPumpItem and FuelSourceController - the assembly's only NetworkBehaviours -
            //     are woven with __initializeVariables, __initializeRpcs and one __rpc_handler per
            //     RPC (RestartStalledServerRpc, ApplyRefineryLockClientRpc,
            //     RequestInteractServerRpc, ShowTipClientRpc). NGO calls those overrides itself.
            //   * ShipSystemsController and StorePurchaseNetwork are NOT woven and do not need to
            //     be. Neither is a NetworkBehaviour; both speak CustomMessagingManager named
            //     messages only, which the patcher does not touch. The plan called all four
            //     "netcode-bearing", which is true, but only two of them are *weavable*.
            //   * What this call actually runs is exactly ONE generated method:
            //     __GEN.NetworkVariableSerializationHelper.InitializeSerialization, which
            //     registers the memcpy serializer and equality checker for `float` - the type of
            //     FuelPumpItem's NetworkVariable<float> _fillProgress01.
            //
            // That last point is a live hazard worth naming: the generated helper exists only
            // because a NetworkVariable exists. Remove FuelPumpItem's _fillProgress01 and the
            // patcher emits zero RuntimeInitializeOnLoadMethods, whereupon Initialize's
            // requireInitializers guard throws and takes this whole plugin down at Awake - even
            // though the assembly is correctly woven for its RPCs, which need no runtime
            // initializer at all. Verify the generated initializer and actual plugin startup.
            Y4NGZCompany.Core.NetcodeRuntimeBootstrap.Initialize(Logger, typeof(ShipSystemsPlugin).Assembly);

            // #613 task 2.2 - the cross-plugin module handshake. Published before anything can
            // join, so the first client to connect is compared against a complete manifest.
            //
            // The revision is the WIRE protocol, not the build: it covers the 18 frozen
            // LGUShipSystems.* names on ShipSystemsController, the two Y4NGZCompany.StorePurchase*
            // names on StorePurchaseNetwork, and the four woven RPCs above. Bump it only when one
            // of those payloads or its host-authoritative meaning changes; do NOT bump it for a
            // Version change, or every patch release would refuse to play with itself.
            Y4NGZCore.Modules.Net.ModuleHandshakeService.Publish(
                Y4NGZCore.Lifecycle.ModuleIds.ShipSystems,
                ShipSystemsProtocolRevision,
                Version);

            _harmony = new Harmony(Guid);

            Initialize(Config, Logger, _harmony, InitializeModule);

            // Only now that every module is initialized does the host start ticking and become
            // responsible for teardown.
            _host.Activate(_harmony, Logger);

            Logger.LogInfo($"{Name} v{Version} loaded.");
        }

        /// <summary>
        /// Safety net only, and it must stay one. The real teardown lives on
        /// <see cref="ShipSystemsHost"/>; this runs the same sequence solely for the two cases
        /// that host cannot cover — see the three reachable branches below.
        ///
        /// <para><b>It must NOT tear down while the host is alive (#613 follow-up).</b> This
        /// component rides on BepInEx's shared <c>BepInEx_Manager</c> object, which with the
        /// BepInEx default <c>Chainloader/HideManagerGameObject=false</c> has no hide flags and
        /// is reachable by <c>GameObject.Find("BepInEx_Manager")</c>. Another mod's Find-based
        /// cleanup destroys it and every plugin component on it — the #393 scenario, which is
        /// exactly why <see cref="ShipSystemsHost"/> lives on its own hide-flagged object in the
        /// first place.</para>
        ///
        /// <para>Observed, on the profile that reached a lobby with no Contracted installed: one
        /// frame after "Chainloader startup complete" the shared object went down (BetterArmory
        /// and Y4NGZUpgrades both logged their own detection of it), this <c>OnDestroy</c> ran,
        /// and <see cref="ShipSystemsHost.TearDown"/> called
        /// <c>ModuleTickRegistry.RunTeardown(ShipSystems)</c> — which removes this owner's tick
        /// registrations and latches the owner so <c>Add</c> refuses any re-registration — plus
        /// <c>Harmony.UnpatchSelf</c>. The host object itself survived and went on pumping the
        /// registry all session with this plugin's subscribers no longer in it. Result: no
        /// monitor row, no power display, no fuel chute, and not one line of log to say why,
        /// because the loud "host destroyed" message lives on
        /// <see cref="ShipSystemsHost.OnDestroy"/>, which never ran.</para>
        ///
        /// <para><b>The three reachable branches, exactly.</b></para>
        /// <list type="number">
        /// <item><description><see cref="ShipSystemsHost.HasTornDown"/> — the host already ran the
        /// shutdown (normal application quit, or its own object was destroyed). Nothing to do.</description></item>
        /// <item><description>A host that exists <em>and</em> is activated — the plugin is running
        /// normally and something destroyed BepInEx's shared object out from under it. Warn once
        /// and leave every patch and registration applied. This is what LethalCCTV (no
        /// <c>OnDestroy</c> at all), BetterArmory and Y4NGZUpgrades already do.</description></item>
        /// <item><description>Everything else — no host, or a host that was created but never
        /// activated. The second half is not hypothetical: <see cref="Awake"/> assigns
        /// <c>_host</c> before <c>NetcodeRuntimeBootstrap.Initialize</c> (whose
        /// <c>requireInitializers</c> guard throws when this assembly is not woven) and before
        /// <c>ModuleHandshakeService.Publish</c>, so a throw between the two leaves a live,
        /// unactivated host. <see cref="ShipSystemsHost.OnDestroy"/> returns at its
        /// <c>!_activated</c> early-out in that state, so this branch is the only path that can
        /// unwind whatever the partial <c>Awake</c> managed to apply. <c>TearDown</c> tolerates
        /// a null Harmony, which is the shape a pre-<c>Activate</c> failure leaves
        /// behind.</description></item>
        /// </list>
        /// </summary>
        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (ShipSystemsHost.HasTornDown)
                return;

            // Unity's overloaded null: a destroyed host compares equal to null and falls through
            // to the real teardown below. So does a host that exists but never reached Activate -
            // nothing else will ever tear that state down. Only a LIVE, ACTIVATED host means "the
            // plugin is still running and this is not a shutdown".
            if (_host != null && _host.IsActivated)
            {
                // Same branch, two very different situations. On an ordinary application quit
                // Unity may destroy BepInEx's manager object — and this component with it —
                // before it reaches the host, so this branch is on the normal exit path too.
                // Warning about "mid-session" destruction and "gameplay continues normally"
                // there would put a false alarm in every log the mod ever produces, which is
                // how a real one stops being read. The teardown decision is unchanged either
                // way: the host is alive and will run the shutdown from its own OnDestroy.
                if (_applicationQuitting)
                {
                    Logger.LogDebug(
                        $"[Y4NGZShipSystems] Plugin component destroyed during application quit; "
                        + $"'{HostObjectName}' is still alive and owns the shutdown.");
                    return;
                }

                Logger.LogWarning(
                    "[Y4NGZShipSystems] The plugin component's GameObject was destroyed mid-session "
                    + $"(frame={Time.frameCount}; BepInEx [Chainloader] HideManagerGameObject is "
                    + "false, so BepInEx_Manager is destroyable by any mod's Find-based cleanup). "
                    + $"'{HostObjectName}' is alive, so nothing was torn down: the tick "
                    + "registrations and every Harmony patch this plugin applied remain ACTIVE and "
                    + "gameplay continues normally.");
                return;
            }

            ShipSystemsHost.TearDown(_harmony, Logger);
        }

        /// <summary>
        /// Initialise Ship Systems, Terminal and the Store, in that order.
        ///
        /// <para>Still a separate method taking its collaborators as parameters, rather than
        /// inlined into <see cref="Awake"/>: the three <c>InitializeModule</c> calls, their labels
        /// and their order are unchanged from the pre-split <c>Plugin.Awake</c>, so a module that
        /// throws is isolated exactly as before and the "Terminal owns the presentation" flag is
        /// still read after Terminal has bound it. Keeping the signature also keeps the method
        /// callable from an off-engine harness.</para>
        /// </summary>
        internal static void Initialize(
            BepInEx.Configuration.ConfigFile config,
            ManualLogSource logger,
            Harmony harmony,
            Action<string, Action> isolate)
        {
            // #666: one module now. The Terminal and Shop bootstraps moved to
            // Y4NGZTerminalUpgrades with the systems they wire, and the refit input-watchdog
            // registration moved with the refit code.
            isolate("Ship Systems", () => Bootstrap.ShipSystemsBootstrap.Initialize(config, logger, harmony));

            // Registered here rather than from the bootstrap so it is registered whether or
            // not the bootstrap returned early on a disabled config toggle — which is what
            // the pre-split host did, since it called Shutdown unconditionally.
            ModuleTickRegistry.RegisterTeardown(
                ModuleHarmonyIds.ShipSystems,
                ModuleTeardownOrder.ShipSystems,
                "ShipSystemsBootstrap",
                Bootstrap.ShipSystemsBootstrap.Shutdown);
        }

        /// <summary>
        /// Per-module isolation, identical in shape and wording to Contracted's. A module that
        /// throws during init must not stop the other two from initialising.
        /// </summary>
        internal static void InitializeModule(string label, Action initialize)
        {
            try
            {
                initialize();
            }
            catch (Exception ex)
            {
                ModuleLog.ShipSystems?.LogError(
                    $"Y4NGZShipSystems module '{label}' failed to initialize: {ex}");
            }
        }
    }

    /// <summary>
    /// Runtime host for the Ship Systems plugin: owns this plugin's per-frame tick pump and its
    /// teardown.
    ///
    /// <para>#393: rides on the plugin's own hide-flagged, <c>DontDestroyOnLoad</c> GameObject
    /// rather than BepInEx's shared manager object, which is discoverable by
    /// <c>GameObject.Find("BepInEx_Manager")</c> and therefore destroyable by another mod's
    /// cleanup. Losing this component stops the ship-systems tick and the store's network pump,
    /// and <c>Harmony.UnpatchSelf</c> strips every patch this plugin applied — so an unexpected
    /// destroy is logged loudly.</para>
    /// </summary>
    internal sealed class ShipSystemsHost : MonoBehaviour
    {
        private static bool _tornDown;

        private Harmony _harmony;
        private ManualLogSource _log;
        private bool _activated;
        private bool _applicationQuitting;

        /// <summary>True once this plugin's shutdown sequence has run, from either owner.</summary>
        internal static bool HasTornDown => _tornDown;

        /// <summary>
        /// True once <see cref="Activate"/> has run — i.e. once <c>Awake</c> finished and this
        /// host became responsible for the tick and the teardown.
        ///
        /// <para>Read by <see cref="ShipSystemsPlugin.OnDestroy"/>, which must distinguish "the
        /// shared BepInEx manager object died under a running plugin" (this is true: leave
        /// everything applied) from "Awake threw after creating the host but before activating it"
        /// (this is false: nobody else will ever tear the partial state down, so the safety net
        /// must). The host's own <c>OnDestroy</c> returns early in the second case, so without
        /// this the teardown would be unreachable from either side.</para>
        /// </summary>
        internal bool IsActivated => _activated;

        internal void Activate(Harmony harmony, ManualLogSource log)
        {
            _harmony = harmony;
            _log = log;
            _activated = true;
        }

        /// <summary>
        /// Pumps <see cref="ModuleTickRegistry"/>. Three plugins now have a host and all three
        /// call this; the registry runs every registered subscriber in
        /// <see cref="ModuleTickOrder"/> regardless of which host's frame it was reached from,
        /// which is the whole reason the ordering moved into Core in #612 task 1.4 — three
        /// independent <c>LateUpdate</c>s would otherwise have Unity's script execution order
        /// deciding whether the input watchdog samples last.
        ///
        /// <para><b>More than one host pumps it, so the pump is frame-scoped.</b>
        /// <see cref="ModuleTickRegistry.Tick(int)"/> runs the list at most once per
        /// <c>Time.frameCount</c>; whichever host's <c>LateUpdate</c> Unity reaches first that
        /// frame does the work and the other returns 0. Without that, two hosts would run every
        /// subscriber twice a frame - the ship-systems tick, the quota tick, the gameplay-UI
        /// publish and the watchdog's sample, all doubled and silent. Both hosts must still pump,
        /// because either plugin can be the one that is not installed.</para>
        ///
        /// <para>The registry also isolates a throwing subscriber, so one plugin's fault cannot
        /// abort another plugin's tick.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!_activated)
                return;

            // #613 task 2.1: frame-scoped. Both plugins' hosts pump the whole registry so
            // the ModuleTickOrder sequence survives having more than one host, and the
            // frame token is what stops the second pump of the same frame running every
            // subscriber a second time. See ModuleTickRegistry.Tick(int).
            ModuleTickRegistry.Tick(Time.frameCount);
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (!_activated)
                return;

            if (!_applicationQuitting)
            {
                _log?.LogError(
                    "[Y4NGZShipSystems] Host destroyed outside application shutdown " +
                    $"(frame={Time.frameCount}, host='{(gameObject != null ? gameObject.name : "<null>")}', " +
                    $"scene='{(gameObject != null ? gameObject.scene.name : "<null>")}'). " +
                    "Ship systems, the terminal, the store network pump and every Harmony patch " +
                    "this plugin applied are going down with it.");
            }

            TearDown(_harmony, _log);
        }

        /// <summary>
        /// Idempotent: the first caller (host or the plugin's safety-net OnDestroy) wins.
        ///
        /// <para><b>This host tears down exactly one owner.</b> Contracted's host names
        /// <see cref="ModuleHarmonyIds.Contracted"/> and (until Wave 3 moves it)
        /// <see cref="ModuleHarmonyIds.MonitorTakeover"/>; this one names
        /// <see cref="ModuleHarmonyIds.ShipSystems"/> and nothing else. That is precisely why
        /// <see cref="ModuleTickRegistry"/> latches teardown per owner rather than once per
        /// process: a process-wide latch would have whichever host died first tear down two
        /// plugins that are still running, and then silence their own <c>OnDestroy</c> when it
        /// came.</para>
        ///
        /// <para>One Harmony owner, one <c>UnpatchSelf</c>. Unpatching another plugin's id from
        /// here is the exact failure the split's separate ids exist to make impossible.</para>
        /// </summary>
        internal static void TearDown(Harmony harmony, ManualLogSource log)
        {
            if (_tornDown)
                return;
            _tornDown = true;

            ModuleTickRegistry.RunTeardown(ModuleHarmonyIds.ShipSystems);

            if (harmony == null)
                return;

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                log?.LogWarning(
                    "Y4NGZShipSystems shutdown warning: UnpatchSelf failed for Harmony id "
                    + $"'{harmony.Id}': {ex.Message}");
            }
        }
    }
}
