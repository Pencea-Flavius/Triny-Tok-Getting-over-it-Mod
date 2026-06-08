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

        // Timed effects: slot -> expiry time + cleanup callback.
        // A "slot" groups mutually-exclusive effects (e.g. big_hammer/small_hammer
        // both live in slot "hammer_size"). Only one effect per slot runs at a time.
        private static readonly Dictionary<string, DateTime> _effectExpiry = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, Action> _effectCleanup = new Dictionary<string, Action>();
        // slot -> code currently occupying it (so we can tell "same effect" from "different effect")
        private static readonly Dictionary<string, string> _effectActiveCode = new Dictionary<string, string>();
        // slot -> requests waiting for the slot to free up (different effect donated while one is active)
        private static readonly Dictionary<string, Queue<EffectRequest>> _effectQueue = new Dictionary<string, Queue<EffectRequest>>();

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

        // Decide what to do when a timed effect arrives for `slot`.
        // Returns true → the slot was free, the caller should apply the effect now
        //                (and call ScheduleTimedEffect to register its expiry/cleanup).
        // Returns false → handled here without applying:
        //   • same effect re-donated while active → its timer is extended (+dur).
        //   • a different effect for the same slot → queued, runs once this one expires.
        public bool TryStackOrQueue(EffectRequest request, string slot, float dur)
        {
            lock (_effectExpiry)
            {
                if (_effectExpiry.TryGetValue(slot, out var exp) && DateTime.UtcNow < exp)
                {
                    _effectActiveCode.TryGetValue(slot, out var active);
                    if (active == request.code)
                    {
                        _effectExpiry[slot] = exp.AddSeconds(dur);
                        LoggerInstance.Msg($"Extended '{request.code}' (+{dur}s) on slot '{slot}'");
                    }
                    else
                    {
                        if (!_effectQueue.TryGetValue(slot, out var q))
                            _effectQueue[slot] = q = new Queue<EffectRequest>();
                        q.Enqueue(request);
                        LoggerInstance.Msg($"Queued '{request.code}' behind '{active}' on slot '{slot}'");
                    }
                    return false;
                }

                _effectActiveCode[slot] = request.code;
                return true;
            }
        }

        private static void ExpireEffect(string key)
        {
            Action cleanup = null;
            EffectRequest next = null;
            lock (_effectExpiry)
            {
                _effectExpiry.Remove(key);
                _effectCleanup.TryGetValue(key, out cleanup);
                _effectCleanup.Remove(key);
                _effectActiveCode.Remove(key);
                if (_effectQueue.TryGetValue(key, out var q) && q.Count > 0)
                    next = q.Dequeue();
            }
            // Run the active effect's cleanup first (it enqueues the reset onto
            // PendingActions), THEN re-apply the next queued effect — so the reset
            // runs before the queued effect re-captures the base state.
            cleanup?.Invoke();
            if (next != null)
            {
                lock (PendingActions)
                    PendingActions.Enqueue(() => Instance.ApplyEffect(next));
            }
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

        // Decide where a spawned prop/gift appears.
        // For small amounts (<=10) keep the original gentle stack near the pot.
        // For larger amounts, rain them down: spread randomly over a wide area
        // above the player with a CAPPED height so they never pile into space
        // (which used to launch everything and trigger the end scene).
        private static Vector3 ComputeSpawnPos(Vector2 potPos, float z, int i, int count)
        {
            if (count > 10)
            {
                float xOff = UnityEngine.Random.Range(-9f, 9f);
                float yOff = UnityEngine.Random.Range(7f, 18f);
                return new Vector3(potPos.x + xOff, potPos.y + yOff, z);
            }
            float x = UnityEngine.Random.Range(-1.2f, 1.2f);
            return new Vector3(potPos.x + x, potPos.y + 5f + i * 0.9f, z);
        }

        // Only Hammer: turn body + pot colliders into triggers (pass-through, so only
        // the hammer collides) and hide the man + pot mesh. Mirrors ModOnlyHammer.
        private static void SetOnlyHammer(bool on)
        {
            var playerGO = GameObject.Find("Player");
            if (playerGO == null) return;
            var t = playerGO.transform;

            var bodyCol = playerGO.GetComponent<PolygonCollider2D>();
            if (bodyCol != null) bodyCol.isTrigger = on;

            var potColT = t.Find("PotCollider");
            if (potColT != null)
            {
                var pc = potColT.GetComponent<PolygonCollider2D>();
                if (pc != null) pc.isTrigger = on;
                var sidesT = potColT.Find("Sides");
                if (sidesT != null)
                    foreach (var c in sidesT.GetComponents<PolygonCollider2D>())
                        c.isTrigger = on;
            }

            var dude = t.Find("dude");
            if (dude != null) dude.gameObject.SetActive(!on);

            var potT = t.Find("Pot");
            if (potT != null)
            {
                var mesh = potT.Find("Mesh");
                if (mesh != null) mesh.gameObject.SetActive(!on);
            }
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
                var pos = ComputeSpawnPos(potPos, src.transform.position.z, i, count);
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
                    var pos = ComputeSpawnPos(potPos, src.transform.position.z, i, count);
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
            float power = request.power > 0f ? request.power : 1f;   // force multiplier, default 1x

            LoggerInstance.Msg($"Effect received: {request.code} | GameReady={GameReady()} | pot={(pot != null ? "ok" : "null")} | player={(player != null ? "ok" : "null")} | save={(save != null ? "ok" : "null")}");

            try
            {
                switch (request.code)
                {
                    // ── Instant ───────────────────────────────────────────────────
                    case "launch":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.velocity = new Vector2(pot.potRB.velocity.x, 10f * power);
                        pot.potRB.AddForce(new Vector2(0f, 200000f * power));
                        break;

                    case "reset_progress":
                        if (TryDeferWhilePaused(request, save == null)) return;
                        save.ResetPlayerButNotDialogue();
                        break;

                    case "shove_left":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.AddForce(new Vector2(-100000f * power, 10000f));
                        break;

                    case "shove_right":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        pot.potRB.AddForce(new Vector2(100000f * power, 10000f));
                        break;

                    // ── Gravity (timed) ──────────────────────────────────────────
                    case "low_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "gravity", dur))
                        {
                            gravity = 1;
                            Physics2D.gravity = new Vector2(0f, -10f);
                            ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                            {
                                gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                            }));
                        }
                        break;

                    case "high_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "gravity", dur))
                        {
                            gravity = 2;
                            Physics2D.gravity = new Vector2(0f, -45f);
                            ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                            {
                                gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                            }));
                        }
                        break;

                    case "zero_gravity":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "gravity", dur))
                        {
                            gravity = -2;
                            Physics2D.gravity = new Vector2(0f, 0f);
                            ScheduleTimedEffect("gravity", dur, () => PendingActions.Enqueue(() =>
                            {
                                gravity = 0; Physics2D.gravity = new Vector2(0f, -30f);
                            }));
                        }
                        break;

                    // ── Friction (timed) ─────────────────────────────────────────
                    case "low_friction":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        if (TryStackOrQueue(request, "friction", dur))
                        {
                            pot.slidingFriction.friction = 0.1f;
                            pot.staticFriction.friction = 0.3f;
                            friction = true;
                            ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() =>
                            {
                                if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                                friction = false;
                            }));
                        }
                        break;

                    case "high_friction":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        if (TryStackOrQueue(request, "friction", dur))
                        {
                            pot.slidingFriction.friction = 0.6f;
                            pot.staticFriction.friction = 1f;
                            friction = true;
                            ScheduleTimedEffect("friction", dur, () => PendingActions.Enqueue(() =>
                            {
                                if (pot != null) { pot.slidingFriction.friction = 0.3f; pot.staticFriction.friction = 0.8f; }
                                friction = false;
                            }));
                        }
                        break;

                    // ── Camera (timed) ───────────────────────────────────────────
                    case "flip_camera":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "camera", dur))
                        {
                            flip = true;
                            Camera.main.transform.Rotate(0f, 0f, 180f);
                            ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() =>
                            {
                                Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                                flip = false;
                            }));
                        }
                        break;

                    case "spin_camera":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "camera", dur))
                        {
                            spin = true;
                            ScheduleTimedEffect("camera", dur, () => PendingActions.Enqueue(() =>
                            {
                                Camera.main.transform.Rotate(0f, 0f, Camera.main.transform.rotation.eulerAngles.z * -1f);
                                spin = false;
                            }));
                        }
                        break;

                    // ── Hammer size (timed) ──────────────────────────────────────
                    // Same as the modpack's SetHammerMultiplier: the hammer is the
                    // "Hub" child of Player; set its localScale to (val,val,val).
                    case "big_hammer":
                    case "small_hammer":
                    {
                        if (TryDeferWhilePaused(request, !GameReady() || player == null)) return;
                        var hub = player.transform.Find("Hub");
                        if (hub != null)
                        {
                            if (TryStackOrQueue(request, "hammer_size", dur))
                            {
                                Vector3 baseScale = hub.localScale;
                                float val = request.code == "big_hammer" ? 2.5f : 0.5f;
                                hub.localScale = new Vector3(val, val, val);
                                ScheduleTimedEffect("hammer_size", dur, () => PendingActions.Enqueue(() =>
                                {
                                    var h = player != null ? player.transform.Find("Hub") : null;
                                    if (h != null) h.localScale = baseScale;
                                }));
                            }
                        }
                        else { status = EffectResponse.STATUS_FAILURE; message = "Hub (hammer) not found"; }
                        break;
                    }

                    // ── Rolling Over It (timed) ──────────────────────────────────
                    // Same as the modpack's ModRollingOverIt: widen the HingeJoint2D
                    // angle limits on Player (normally ±15°) so the pot rolls freely.
                    case "rolling_over_it":
                    {
                        if (TryDeferWhilePaused(request, !GameReady() || player == null)) return;
                        var hinge = player.GetComponent<HingeJoint2D>();
                        if (hinge != null)
                        {
                            if (TryStackOrQueue(request, "rolling", dur))
                            {
                                JointAngleLimits2D orig = hinge.limits;
                                hinge.limits = new JointAngleLimits2D { min = -1000000f, max = 1000000f };
                                ScheduleTimedEffect("rolling", dur, () => PendingActions.Enqueue(() =>
                                {
                                    var hj = player != null ? player.GetComponent<HingeJoint2D>() : null;
                                    if (hj != null) hj.limits = orig;
                                }));
                            }
                        }
                        else { status = EffectResponse.STATUS_FAILURE; message = "HingeJoint2D not found"; }
                        break;
                    }

                    // ── Bouncy (timed) ───────────────────────────────────────────
                    // Same as the modpack: raise bounciness on the pot's physics materials.
                    case "bouncy":
                        if (TryDeferWhilePaused(request, !GameReady() || pot == null)) return;
                        if (TryStackOrQueue(request, "bouncy", dur))
                        {
                            float sB  = pot.slidingFriction != null ? pot.slidingFriction.bounciness : 0f;
                            float stB = pot.staticFriction  != null ? pot.staticFriction.bounciness  : 0f;
                            if (pot.slidingFriction != null) pot.slidingFriction.bounciness = 1f;
                            if (pot.staticFriction  != null) pot.staticFriction.bounciness  = 1f;
                            ScheduleTimedEffect("bouncy", dur, () => PendingActions.Enqueue(() =>
                            {
                                if (pot != null && pot.slidingFriction != null) pot.slidingFriction.bounciness = sB;
                                if (pot != null && pot.staticFriction  != null) pot.staticFriction.bounciness  = stB;
                            }));
                        }
                        break;

                    // ── Only Hammer (timed) ──────────────────────────────────────
                    // Same as the modpack's ModOnlyHammer: make the body + pot colliders
                    // triggers (so only the hammer collides) and hide their meshes.
                    case "only_hammer":
                        if (TryDeferWhilePaused(request, !GameReady() || player == null)) return;
                        if (TryStackOrQueue(request, "only_hammer", dur))
                        {
                            SetOnlyHammer(true);
                            ScheduleTimedEffect("only_hammer", dur, () => PendingActions.Enqueue(() => SetOnlyHammer(false)));
                        }
                        break;

                    // ── Input (timed) ────────────────────────────────────────────
                    case "invert_mouse":
                        if (TryDeferWhilePaused(request, !GameReady())) return;
                        if (TryStackOrQueue(request, "invert_mouse", dur))
                        {
                            invert = true;
                            ScheduleTimedEffect("invert_mouse", dur, () => PendingActions.Enqueue(() => { invert = false; }));
                        }
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
