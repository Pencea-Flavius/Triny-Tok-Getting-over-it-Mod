using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(TrinyTokGOI.TrinyTokGOIMod), "TrinyTok GOI", "1.0.0", "TrinyTok")]
[assembly: MelonGame("Bennett Foddy", "Getting Over It with Bennett Foddy")]

namespace TrinyTokGOI
{
    public class TrinyTokGOIMod : MelonMod
    {
        public static TrinyTokGOIMod Instance;
        private TrinyTokClient client;

        // Active game-object references (populated by Harmony patches each frame)
        public static bool friction     = false;
        public static int  gravity      = 0;  // 0=normal 1=low 2=high -2=zero
        public static bool flip         = false;
        public static bool spin         = false;
        public static bool invert       = false;
        public static PlayerControl player = null;
        public static PotSounds     pot    = null;
        public static Saviour       save   = null;

        // Actions queued from background thread, drained on the Unity main thread
        public static readonly Queue<Action> PendingActions = new Queue<Action>();

        // Timed effects: key -> expiry time + cleanup callback
        private static readonly Dictionary<string, DateTime> _effectExpiry  = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, Action>   _effectCleanup = new Dictionary<string, Action>();

        public static bool GameReady()
        {
            try
            {
                if (GameObject.Find("Player") == null) return false;
                if (player == null) return false;
                var pauseField = typeof(PlayerControl).GetField("menuPause",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pauseField != null && (bool)pauseField.GetValue(player)) return false;
            }
            catch { return false; }
            return true;
        }

        public static void ScheduleTimedEffect(string key, float durationSec, Action onExpire)
        {
            lock (_effectExpiry)
            {
                _effectExpiry[key]  = DateTime.UtcNow.AddSeconds(durationSec);
                _effectCleanup[key] = onExpire;
            }
        }

        public static bool EffectRunning(string key)
        {
            lock (_effectExpiry)
                return _effectExpiry.TryGetValue(key, out var exp) && DateTime.UtcNow < exp;
        }

        private static void ExpireEffect(string key)
        {
            Action cleanup = null;
            lock (_effectExpiry)
            {
                _effectExpiry.Remove(key);
                _effectCleanup.TryGetValue(key, out cleanup);
                _effectCleanup.Remove(key);
            }
            cleanup?.Invoke();
        }

        public override void OnApplicationStart()
        {
            Instance = this;

            var harmony = new HarmonyLib.Harmony("com.trinytok.goi");
            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            client = new TrinyTokClient("127.0.0.1", 52000, LoggerInstance);
            client.Start();

            LoggerInstance.Msg("TrinyTok GOI loaded — connecting to 127.0.0.1:52000");
        }

        public override void OnUpdate()
        {
            FlushPendingActions();
            TickTimedEffects();
        }

        public static void FlushPendingActions()
        {
            lock (PendingActions)
            {
                while (PendingActions.Count > 0)
                {
                    try { PendingActions.Dequeue()?.Invoke(); }
                    catch (Exception ex) { MelonLogger.Error("PendingActions flush error: " + ex.Message); }
                }
            }
        }

        private void TickTimedEffects()
        {
            List<string> expired = null;
            lock (_effectExpiry)
            {
                foreach (var kv in _effectExpiry)
                {
                    if (DateTime.UtcNow >= kv.Value)
                    {
                        if (expired == null) expired = new List<string>();
                        expired.Add(kv.Key);
                    }
                }
            }
            if (expired == null) return;
            foreach (var key in expired)
                ExpireEffect(key);
        }

        public void HandleRequest(EffectRequest request)
        {
            lock (PendingActions)
            {
                PendingActions.Enqueue(() => ApplyEffect(request));
            }
        }

        private void ApplyEffect(EffectRequest request)
        {
            int    status  = EffectResponse.STATUS_SUCCESS;
            string message = "";
            float  dur     = request.duration > 0 ? request.duration / 1000f : 30f;

            try
            {
                switch (request.code)
                {
                    // ── Instant ───────────────────────────────────────────────────
                    case "launch":
                        if (!GameReady()) { status = EffectResponse.STATUS_RETRY; break; }
                        pot.potRB.velocity = new Vector2(pot.potRB.velocity.x, 10f);
                        pot.potRB.AddForce(new Vector2(0f, 200000f));
                        break;

                    case "reset_progress":
                        if (save == null) { status = EffectResponse.STATUS_RETRY; message = "Save not ready"; break; }
                        save.ResetPlayerButNotDialogue();
                        break;

                    case "shove_left":
                        if (!GameReady()) { status = EffectResponse.STATUS_RETRY; break; }
                        pot.potRB.AddForce(new Vector2(-100000f, 10000f));
                        break;

                    case "shove_right":
                        if (!GameReady()) { status = EffectResponse.STATUS_RETRY; break; }
                        pot.potRB.AddForce(new Vector2(100000f, 10000f));
                        break;

                    // ── Gravity (timed) ──────────────────────────────────────────
                    case "low_gravity":
                        if (!GameReady() || gravity != 0) { status = EffectResponse.STATUS_RETRY; break; }
                        gravity = 1;
                        Physics2D.gravity = new Vector2(0f, -10f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() => {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    case "high_gravity":
                        if (!GameReady() || gravity != 0) { status = EffectResponse.STATUS_RETRY; break; }
                        gravity = 2;
                        Physics2D.gravity = new Vector2(0f, -45f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() => {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    case "zero_gravity":
                        if (!GameReady() || gravity != 0) { status = EffectResponse.STATUS_RETRY; break; }
                        gravity = -2;
                        Physics2D.gravity = new Vector2(0f, 0f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() => {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    // ── Friction (timed) ─────────────────────────────────────────
                    case "low_friction":
                        if (!GameReady() || friction || pot == null) { status = EffectResponse.STATUS_RETRY; break; }
                        pot.slidingFriction.friction = 0.1f;
                        pot.staticFriction.friction  = 0.3f;
                        friction = true;
                        ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() => {
                            if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                            friction = false;
                        }));
                        break;

                    case "high_friction":
                        if (!GameReady() || friction || pot == null) { status = EffectResponse.STATUS_RETRY; break; }
                        pot.slidingFriction.friction = 0.6f;
                        pot.staticFriction.friction  = 1f;
                        friction = true;
                        ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() => {
                            if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                            friction = false;
                        }));
                        break;

                    // ── Camera (timed) ───────────────────────────────────────────
                    case "flip_camera":
                        if (!GameReady() || flip || spin) { status = EffectResponse.STATUS_RETRY; break; }
                        flip = true;
                        Camera.main.transform.Rotate(0f, 0f, 180f);
                        ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() => {
                            Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                            flip = false;
                        }));
                        break;

                    case "spin_camera":
                        if (!GameReady() || flip || spin) { status = EffectResponse.STATUS_RETRY; break; }
                        spin = true;
                        ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() => {
                            Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                            spin = false;
                        }));
                        break;

                    // ── Input (timed) ────────────────────────────────────────────
                    case "invert_mouse":
                        if (!GameReady() || invert) { status = EffectResponse.STATUS_RETRY; break; }
                        invert = true;
                        ScheduleTimedEffect("invert_mouse", dur, () => PendingActions.Enqueue(() => { invert = false; }));
                        break;

                    default:
                        status  = EffectResponse.STATUS_FAILURE;
                        message = "Unknown effect: " + request.code;
                        break;
                }
            }
            catch (Exception ex)
            {
                status  = EffectResponse.STATUS_FAILURE;
                message = ex.Message;
                LoggerInstance.Error($"ApplyEffect failed for {request.code}: {ex}");
            }

            client.SendResponse(new EffectResponse(request.id, status, message));
        }

        public override void OnApplicationQuit()
        {
            client?.Stop();
        }
    }

    // ── Harmony Patches ───────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(PlayerControl), "FixedUpdate")]
    internal static class GameLoopPatch
    {
        [HarmonyPrefix]
        public static void Prefix() => TrinyTokGOIMod.FlushPendingActions();

        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance)
        {
            TrinyTokGOIMod.player = __instance;

            // Re-enforce gravity override every physics tick
            switch (TrinyTokGOIMod.gravity)
            {
                case  1:  Physics2D.gravity = new Vector2(0f, -10f); break;
                case  2:  Physics2D.gravity = new Vector2(0f, -45f); break;
                case -2:  Physics2D.gravity = new Vector2(0f,   0f); break;
            }

            if (TrinyTokGOIMod.spin)
                Camera.main.transform.Rotate(0f, 0f, 0.2f);
        }
    }

    [HarmonyPatch(typeof(PotSounds), "FixedUpdate")]
    internal static class PotTrackPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PotSounds __instance) => TrinyTokGOIMod.pot = __instance;
    }

    [HarmonyPatch(typeof(Saviour), "Update")]
    internal static class SaveTrackPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Saviour __instance) => TrinyTokGOIMod.save = __instance;
    }

    [HarmonyPatch(typeof(Player), "GetAxis", new Type[] { typeof(string) })]
    internal static class MouseInvertPatch
    {
        [HarmonyPostfix]
        public static void Postfix(string actionName, ref float __result)
        {
            if (TrinyTokGOIMod.invert && (actionName == "mouseX" || actionName == "mouseY"))
                __result *= -1f;
        }
    }

    [HarmonyPatch(typeof(RewardLogic), "EnableYes")]
    internal static class AchievementBlockPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(RewardLogic), "Start")]
    internal static class SceneReloadPatch
    {
        [HarmonyPostfix]
        public static void Postfix() => SceneManager.LoadScene("Loader");
    }
}
