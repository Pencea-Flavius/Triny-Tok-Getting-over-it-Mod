using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(TrinyTokGOI.TrinyTokGOIMod), "TrinyTok GOI", "1.0.0", "TrinyTok")]
[assembly: MelonGame("Bennett Foddy", "Getting Over It")]

namespace TrinyTokGOI
{
    public class TrinyTokGOIMod : MelonMod
    {
        public static TrinyTokGOIMod Instance;
        private TrinyTokClient client;

        // Active game-object references (populated by Harmony patches each frame)
        public static bool friction = false;
        public static int gravity = 0;  // 0=normal 1=low 2=high -2=zero
        public static bool flip = false;
        public static bool spin = false;
        public static bool invert = false;
        public static PlayerControl player = null;
        public static PotSounds pot = null;
        public static Saviour save = null;

        // Actions queued from background thread, drained on the Unity main thread
        public static readonly Queue<Action> PendingActions = new Queue<Action>();
        // Effects that arrived while game was paused/not-ready; drained on next ready FixedUpdate
        public static readonly Queue<EffectRequest> PauseBuffer = new Queue<EffectRequest>();

        // Timed effects: key -> expiry time + cleanup callback
        private static readonly Dictionary<string, DateTime> _effectExpiry = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, Action> _effectCleanup = new Dictionary<string, Action>();

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
                _effectExpiry[key] = DateTime.UtcNow.AddSeconds(durationSec);
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

        // Called from GameLoopPatch when draining PauseBuffer
        public void ApplyEffectInternal(EffectRequest r) => ApplyEffect(r);

        // Returns true and queues for later if condition means "not ready now".
        // Sends SUCCESS immediately so the bridge doesn't time out.
        private bool TryDeferWhilePaused(EffectRequest request, bool notReady)
        {
            if (!notReady) return false;
            lock (PauseBuffer) PauseBuffer.Enqueue(request);
            LoggerInstance.Msg($"Effect deferred (game not ready): {request.code}");
            client.SendResponse(new EffectResponse(request.id, EffectResponse.STATUS_SUCCESS, "queued until unpaused"));
            return true;
        }

        private static void SpawnGift(int count)
        {
            // The gift box has OpenGift component and is INACTIVE by default —
            // GameObject.Find() skips inactive objects, so search by component instead.
            var allGifts = UnityEngine.Object.FindObjectsOfType<OpenGift>(true);
            GameObject src = allGifts.Length > 0 ? allGifts[0].gameObject : null;

            if (src == null)
            {
                MelonLogger.Warning("SpawnGift: no OpenGift object found in scene");
                return;
            }

            Vector2 potPos = pot.potRB.position;
            for (int i = 0; i < count; i++)
            {
                float xOff = UnityEngine.Random.Range(-1.2f, 1.2f);
                var pos = new Vector3(potPos.x + xOff, potPos.y + 5f + i * 0.9f, src.transform.position.z);
                var clone = UnityEngine.Object.Instantiate(src, pos, src.transform.rotation);
                clone.name = "gift_spawned";
                clone.SetActive(true);

                // Reset internal state on the cloned OpenGift so it can trigger again
                var og = clone.GetComponent<OpenGift>();
                if (og != null)
                {
                    var f = typeof(OpenGift).GetField("done", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (f != null) f.SetValue(og, false);
                }

                // Force physics
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody2D>())
                {
                    rb.isKinematic = false;
                    rb.simulated = true;
                    rb.gravityScale = 1f;
                    rb.WakeUp();
                }
                if (clone.GetComponentInChildren<Rigidbody2D>() == null)
                {
                    var rb = clone.AddComponent<Rigidbody2D>();
                    rb.gravityScale = 1f;
                    if (clone.GetComponentInChildren<Collider2D>() == null)
                        clone.AddComponent<CircleCollider2D>().radius = 0.4f;
                }
            }
        }

        private static void SpawnProp(string sourceName, int count)
        {
            var src = GameObject.Find(sourceName);
            if (src == null) { MelonLogger.Warning($"SpawnProp: '{sourceName}' not found in scene"); return; }
            MelonLogger.Msg($"SpawnProp: '{sourceName}' x{count} | srcActive={src.activeSelf}");
            Vector2 potPos = pot.potRB.position;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    float xOff = UnityEngine.Random.Range(-1.2f, 1.2f);
                    var pos = new Vector3(potPos.x + xOff, potPos.y + 5f + i * 0.9f, src.transform.position.z);
                    var clone = UnityEngine.Object.Instantiate(src, pos, src.transform.rotation);
                    clone.name = sourceName + "_spawned";
                    clone.SetActive(true);

                    var rbs = clone.GetComponentsInChildren<Rigidbody2D>();
                    if (rbs.Length == 0)
                    {
                        var rb = clone.AddComponent<Rigidbody2D>();
                        rb.gravityScale = 1f;
                        if (clone.GetComponentInChildren<Collider2D>() == null)
                        {
                            var col = clone.AddComponent<CircleCollider2D>();
                            col.radius = 0.20f;
                            col.isTrigger = false;
                        }
                    }
                    else
                    {
                        foreach (var rb in rbs)
                        {
                            rb.isKinematic = false;
                            rb.simulated = true;
                            rb.gravityScale = 1f;
                            rb.WakeUp();
                        }
                    }
                    MelonLogger.Msg($"SpawnProp: spawned [{i + 1}/{count}] at ({pos.x:F1},{pos.y:F1},{pos.z:F1})");
                }
                catch (Exception ex) { MelonLogger.Error($"SpawnProp iter {i}: {ex.Message}"); }
            }
        }

        private void ApplyEffect(EffectRequest request)
        {
            int status = EffectResponse.STATUS_SUCCESS;
            string message = "";
            float dur = request.duration > 0 ? request.duration / 1000f : 30f;

            LoggerInstance.Msg($"Effect received: {request.code} | GameReady={GameReady()} | pot={(pot != null ? "ok" : "null")} | player={(player != null ? "ok" : "null")} | save={(save != null ? "ok" : "null")}");

            try
            {
                switch (request.code)
                {
                    // ── Instant ───────────────────────────────────────────────────
                    case "launch":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.velocity = new Vector2(pot.potRB.velocity.x, 10f);
                        pot.potRB.AddForce(new Vector2(0f, 200000f));
                        break;

                    case "reset_progress":
                        if (TryDeferWhilePaused(request, save == null)) return;
                        save.ResetPlayerButNotDialogue();
                        break;

                    case "shove_left":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.AddForce(new Vector2(-100000f, 10000f));
                        break;

                    case "shove_right":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.AddForce(new Vector2(100000f, 10000f));
                        break;

                    // ── Gravity (timed) ──────────────────────────────────────────
                    case "low_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        gravity = 1;
                        Physics2D.gravity = new Vector2(0f, -10f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                        {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    case "high_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        gravity = 2;
                        Physics2D.gravity = new Vector2(0f, -45f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                        {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    case "zero_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        gravity = -2;
                        Physics2D.gravity = new Vector2(0f, 0f);
                        ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                        {
                            gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                        }));
                        break;

                    // ── Friction (timed) ─────────────────────────────────────────
                    case "low_friction":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.slidingFriction.friction = 0.1f;
                        pot.staticFriction.friction = 0.3f;
                        friction = true;
                        ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() =>
                        {
                            if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                            friction = false;
                        }));
                        break;

                    case "high_friction":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.slidingFriction.friction = 0.6f;
                        pot.staticFriction.friction = 1f;
                        friction = true;
                        ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() =>
                        {
                            if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                            friction = false;
                        }));
                        break;

                    // ── Camera (timed) ───────────────────────────────────────────
                    case "flip_camera":
                        if (TryDeferWhilePaused(request, !GameReady() || flip || spin)) return;
                        flip = true;
                        Camera.main.transform.Rotate(0f, 0f, 180f);
                        ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() =>
                        {
                            Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                            flip = false;
                        }));
                        break;

                    case "spin_camera":
                        if (TryDeferWhilePaused(request, !GameReady() || flip || spin)) return;
                        spin = true;
                        ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() =>
                        {
                            Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                            spin = false;
                        }));
                        break;

                    // ── Input (timed) ────────────────────────────────────────────
                    case "invert_mouse":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        invert = true;
                        ScheduleTimedEffect("invert_mouse", dur, () => PendingActions.Enqueue(() => { invert = false; }));
                        break;

                    // ── Prop spawns (instant) ─────────────────────────────────────
                    case "spawn_hat":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        LoggerInstance.Msg($"spawn_hat count={request.count}");
                        SpawnProp("SnowHat", request.count <= 0 ? 1 : request.count);
                        break;

                    case "spawn_orange":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        LoggerInstance.Msg($"spawn_orange count={request.count}");
                        SpawnProp("Props/Orange/ORANGE (1)", request.count <= 0 ? 1 : request.count);
                        break;

                    case "spawn_gift":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        LoggerInstance.Msg($"spawn_gift count={request.count}");
                        SpawnGift(request.count <= 0 ? 1 : request.count);
                        break;

                    default:
                        status = EffectResponse.STATUS_FAILURE;
                        message = "Unknown effect: " + request.code;
                        break;
                }
            }
            catch (Exception ex)
            {
                status = EffectResponse.STATUS_FAILURE;
                message = ex.Message;
                LoggerInstance.Error($"ApplyEffect failed for {request.code}: {ex}");
            }

            LoggerInstance.Msg($"Effect result: {request.code} → status={status} msg={message}");
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
        public static void Prefix()
        {
            // Drain pause buffer first so deferred effects run this same tick
            if (TrinyTokGOIMod.GameReady())
            {
                lock (TrinyTokGOIMod.PauseBuffer)
                {
                    while (TrinyTokGOIMod.PauseBuffer.Count > 0)
                    {
                        var req = TrinyTokGOIMod.PauseBuffer.Dequeue();
                        lock (TrinyTokGOIMod.PendingActions)
                            TrinyTokGOIMod.PendingActions.Enqueue(() => TrinyTokGOIMod.Instance.ApplyEffectInternal(req));
                    }
                }
            }
            TrinyTokGOIMod.FlushPendingActions();
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerControl __instance)
        {
            TrinyTokGOIMod.player = __instance;

            switch (TrinyTokGOIMod.gravity)
            {
                case 1: Physics2D.gravity = new Vector2(0f, -10f); break;
                case 2: Physics2D.gravity = new Vector2(0f, -45f); break;
                case -2: Physics2D.gravity = new Vector2(0f, 0f); break;
            }

            if (TrinyTokGOIMod.spin)
                Camera.main.transform.Rotate(0f, 0f, 0.2f);
        }
    }

    // Transpiler intercepts GetAxis("mouseX"/"mouseY") calls INSIDE PlayerControl.FixedUpdate
    // before mouseInput is calculated — avoids patching Rewired directly (crashes on load).
    [HarmonyPatch(typeof(PlayerControl), "FixedUpdate")]
    internal static class MouseInvertPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getAxis = typeof(Player).GetMethod("GetAxis", new Type[] { typeof(string) });
            var wrapper = typeof(MouseInvertPatch).GetMethod(nameof(InvertedGetAxis),
                BindingFlags.Public | BindingFlags.Static);

            foreach (var instr in instructions)
            {
                if (getAxis != null && wrapper != null
                    && instr.opcode == OpCodes.Callvirt
                    && (instr.operand as MethodInfo) == getAxis)
                {
                    yield return new CodeInstruction(OpCodes.Call, wrapper);
                }
                else
                {
                    yield return instr;
                }
            }
        }

        public static float InvertedGetAxis(Player player, string actionName)
        {
            float v = player.GetAxis(actionName);
            if (TrinyTokGOIMod.invert && (actionName == "mouseX" || actionName == "mouseY"))
                v *= -1f;
            return v;
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
