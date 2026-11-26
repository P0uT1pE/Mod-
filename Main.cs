using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

// Performance Optimizer для Lords and Villeins (build 20448151), формат Unity Mod Manager.
// Написан на C# 5, чтобы собираться штатным компилятором Windows (csc.exe из .NET Framework 4).
namespace PerformanceTuner
{
    [Serializable]
    public class Settings : UnityModManager.ModSettings
    {
        public bool skipIdleNearby = true;       // оптимизировать очистку списков соседей без лишних HashSet
        public bool reusePathBatch = true;       // переиспользовать временный список запросов путей
        public bool poolCombatScratch = true;     // переиспользовать временные списки боевой логики
        public bool backgroundLimiter = true;    // ограничивать FPS, когда окно игры не в фокусе
        public int backgroundFps = 15;
        public int fpsCap = 0;                   // 0 = не менять
        public int vsyncMode = 0;                // 0 = не менять, 1 = выкл, 2 = вкл
        public int npcPathRequests = 0;          // 0 = как в игре
        public int animalPathRequests = 0;       // 0 = как в игре
        public bool adaptivePaths = false;       // автоматически снижать нагрузку поиска путей при низком FPS
        public int adaptiveTargetFps = 60;
        public int goapSearchLimit = 0;          // 0 = как в игре (250)

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public static class Main
    {
        public static Settings settings;
        public static UnityModManager.ModEntry.ModLogger logger;
        private static Harmony harmony;
        private static bool enabled;
        public static bool FpsBoostLoaded { get; private set; }

        private static bool IsAssemblyLoaded(string name)
        {
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
                if (string.Equals(loaded[i].GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsFpsBoostInstalled()
        {
            if (IsAssemblyLoaded("FPSBoost")) return true;
            try
            {
                string dll = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods", "FPSBoost", "FPSBoost.dll"));
                return File.Exists(dll);
            }
            catch { return false; }
        }


        // настройки движка
        private static bool inBackground;
        private static int savedTarget, savedVsync;
        private static bool userOverrideActive;
        private static int origTarget, origVsync;
        private static int lastCap = -1, lastVsync = -1;

        // поиск путей
        private static PathfindingManager lastPm;
        private static int origNpc, origAnimal;
        private static int adaptiveNpc = -1, adaptiveAnimal = -1;
        private static float adaptTimer, adaptSum;
        private static int adaptFrames;

        // планировщик GOAP
        private static FieldInfo goapField;
        private static int origGoap, lastGoap;


        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            logger = modEntry.Logger;

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnUnload = Unload;

            try
            {
                goapField = typeof(GOAPPlanner).GetField("SEARCH_LIMIT", BindingFlags.NonPublic | BindingFlags.Static);
                if (goapField != null)
                {
                    origGoap = (int)goapField.GetValue(null);
                    lastGoap = origGoap;
                }
            }
            catch (Exception e)
            {
                logger.Log("Не удалось подготовить рефлексию: " + e.Message);
            }

            FpsBoostLoaded = IsFpsBoostInstalled();
            harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            enabled = true;
            logger.Log("Performance Optimizer 1.3.0 загружен. FPSBoost: " + (FpsBoostLoaded ? "найден — конфликтующие патчи отключены" : "не найден"));
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            if (!value) RestoreAll();
            return true;
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            RestoreAll();
            if (harmony != null) harmony.UnpatchAll(modEntry.Info.Id);
            enabled = false;
            return true;
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            settings.Save(modEntry);
        }

        private static void RestoreAll()
        {
            try
            {
                if (inBackground)
                {
                    QualitySettings.vSyncCount = savedVsync;
                    Application.targetFrameRate = savedTarget;
                    inBackground = false;
                }
                if (userOverrideActive)
                {
                    QualitySettings.vSyncCount = origVsync;
                    Application.targetFrameRate = origTarget;
                    userOverrideActive = false;
                }
                lastCap = -1;
                lastVsync = -1;

                if (lastPm != null)
                {
                    if (origNpc > 0) lastPm.maxRequestsPerFrame = origNpc;
                    if (origAnimal > 0) lastPm.maxAnimalRequestsPerFrame = origAnimal;
                }
                adaptiveNpc = -1;
                adaptiveAnimal = -1;

                if (goapField != null && origGoap > 0)
                {
                    goapField.SetValue(null, origGoap);
                    lastGoap = origGoap;
                }
            }
            catch (Exception e)
            {
                logger.Log("Ошибка при восстановлении настроек: " + e.Message);
            }
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!enabled) return;
            float udt = Time.unscaledDeltaTime;

            try
            {
                ApplyEngine();
                ApplyPathing(udt);
                ApplyGoap();
            }
            catch (Exception e)
            {
                logger.Log("Ошибка в OnUpdate: " + e.Message);
            }
        }

        private static void ApplyEngine()
        {
            bool bg = settings.backgroundLimiter && !Application.isFocused;
            if (bg)
            {
                if (!inBackground)
                {
                    savedTarget = Application.targetFrameRate;
                    savedVsync = QualitySettings.vSyncCount;
                    inBackground = true;
                }
                int fps = Math.Max(5, settings.backgroundFps);
                if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                return;
            }
            if (inBackground)
            {
                QualitySettings.vSyncCount = savedVsync;
                Application.targetFrameRate = savedTarget;
                inBackground = false;
                lastCap = -1;
                lastVsync = -1;
            }

            bool want = settings.fpsCap > 0 || settings.vsyncMode != 0;
            if (want && !userOverrideActive)
            {
                origTarget = Application.targetFrameRate;
                origVsync = QualitySettings.vSyncCount;
                userOverrideActive = true;
                lastCap = -1;
                lastVsync = -1;
            }
            if (!want)
            {
                if (userOverrideActive)
                {
                    QualitySettings.vSyncCount = origVsync;
                    Application.targetFrameRate = origTarget;
                    userOverrideActive = false;
                }
                return;
            }
            if (settings.fpsCap != lastCap || settings.vsyncMode != lastVsync)
            {
                int vs = QualitySettings.vSyncCount;
                if (settings.vsyncMode == 1) vs = 0;
                else if (settings.vsyncMode == 2) vs = 1;
                if (settings.fpsCap > 0) vs = 0; // ограничение кадров работает только при выключенной вертикальной синхронизации
                QualitySettings.vSyncCount = vs;
                Application.targetFrameRate = settings.fpsCap > 0 ? settings.fpsCap : origTarget;
                lastCap = settings.fpsCap;
                lastVsync = settings.vsyncMode;
            }
        }

        private static void ApplyPathing(float udt)
        {
            PathfindingManager pm = PathfindingManager.instance;
            if (pm == null) return;
            if (!object.ReferenceEquals(pm, lastPm))
            {
                lastPm = pm;
                origNpc = pm.maxRequestsPerFrame;
                origAnimal = pm.maxAnimalRequestsPerFrame;
                adaptiveNpc = -1;
                adaptiveAnimal = -1;
                adaptTimer = 0f;
                adaptSum = 0f;
                adaptFrames = 0;
            }

            int npc = settings.npcPathRequests > 0 ? settings.npcPathRequests : origNpc;
            int animal = settings.animalPathRequests > 0 ? settings.animalPathRequests : origAnimal;

            if (settings.adaptivePaths && npc > 0 && animal > 0)
            {
                if (adaptiveNpc < 0 || adaptiveNpc > npc) adaptiveNpc = npc;
                if (adaptiveAnimal < 0 || adaptiveAnimal > animal) adaptiveAnimal = animal;
                adaptTimer += udt;
                adaptSum += udt;
                adaptFrames++;
                if (adaptTimer >= 1f)
                {
                    float avgMs = adaptSum / adaptFrames * 1000f;
                    float target = 1000f / Math.Max(20, settings.adaptiveTargetFps);
                    if (avgMs > target * 1.15f)
                    {
                        adaptiveNpc = Math.Max(1, adaptiveNpc - 1);
                        adaptiveAnimal = Math.Max(1, adaptiveAnimal - 1);
                    }
                    else if (avgMs < target * 0.85f)
                    {
                        adaptiveNpc = Math.Min(npc, adaptiveNpc + 1);
                        adaptiveAnimal = Math.Min(animal, adaptiveAnimal + 1);
                    }
                    adaptTimer = 0f;
                    adaptSum = 0f;
                    adaptFrames = 0;
                }
                npc = adaptiveNpc;
                animal = adaptiveAnimal;
            }

            if (npc > 0 && pm.maxRequestsPerFrame != npc) pm.maxRequestsPerFrame = npc;
            if (animal > 0 && pm.maxAnimalRequestsPerFrame != animal) pm.maxAnimalRequestsPerFrame = animal;
        }

        private static void ApplyGoap()
        {
            if (goapField == null || origGoap <= 0) return;
            int want = settings.goapSearchLimit > 0 ? settings.goapSearchLimit : origGoap;
            if (want != lastGoap)
            {
                goapField.SetValue(null, want);
                lastGoap = want;
            }
        }

        // ---------- интерфейс (Ctrl+F10) ----------

        private static int IntSlider(string label, int value, int min, int max, string zeroText)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(360));
            value = (int)Math.Round(GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220)));
            string txt = (value == 0 && zeroText != null) ? zeroText : value.ToString();
            GUILayout.Label(txt, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            return value;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Оптимизации памяти и игровых систем");
            // --- безопасное ---
            GUILayout.Label("Безопасные оптимизации");
            if (Main.FpsBoostLoaded)
            {
                GUILayout.Label("FPSBoost обнаружен: пересекающиеся оптимизации боя и очереди путей отключены автоматически.");
            }
            else
            {
                settings.skipIdleNearby = GUILayout.Toggle(settings.skipIdleNearby,
                    "Оптимизировать очистку участников боя без временных HashSet");
                settings.reusePathBatch = GUILayout.Toggle(settings.reusePathBatch,
                    "Переиспользовать временный список запросов поиска путей");
            }
            settings.poolCombatScratch = GUILayout.Toggle(settings.poolCombatScratch,
                "Переиспользовать временные списки боевой логики (меньше мусора памяти)");
            settings.backgroundLimiter = GUILayout.Toggle(settings.backgroundLimiter,
                "Ограничивать FPS, когда окно игры не в фокусе (экономит процессор и видеокарту)");
            if (settings.backgroundLimiter)
                settings.backgroundFps = IntSlider("    FPS в фоне", settings.backgroundFps, 5, 60, null);
            GUILayout.Space(10);

            // --- кадры ---
            GUILayout.Label("Кадры");
            settings.fpsCap = IntSlider("Ограничение FPS (0 = не менять)", settings.fpsCap, 0, 240, "не менять");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Вертикальная синхронизация", GUILayout.Width(360));
            settings.vsyncMode = GUILayout.Toolbar(settings.vsyncMode, new string[] { "не менять", "выкл", "вкл" }, GUILayout.Width(360));
            GUILayout.EndHorizontal();
            GUILayout.Label("  Ограничение FPS включает отключение вертикальной синхронизации автоматически.");
            GUILayout.Space(10);

            // --- поиск путей ---
            GUILayout.Label("Поиск путей (экспериментально: меньше нагрузка, но жители медленнее реагируют)");
            settings.npcPathRequests = IntSlider("Запросов пути жителей за кадр", settings.npcPathRequests, 0, 30, "как в игре");
            settings.animalPathRequests = IntSlider("Запросов пути животных за кадр", settings.animalPathRequests, 0, 30, "как в игре");
            settings.adaptivePaths = GUILayout.Toggle(settings.adaptivePaths,
                "Автоматически снижать лимиты, пока FPS ниже цели, и возвращать при запасе");
            if (settings.adaptivePaths)
                settings.adaptiveTargetFps = IntSlider("    Целевой FPS", settings.adaptiveTargetFps, 20, 144, null);
            GUILayout.Space(10);

            // --- планировщик ---
            GUILayout.Label("Планировщик действий жителей (экспериментально)");
            settings.goapSearchLimit = IntSlider("Предел поиска плана (в игре 250)", settings.goapSearchLimit, 0, 500, "как в игре");
            GUILayout.Label("  Меньше значение: быстрее, но у жителей чаще не находится план. Если жители начали «зависать», верните 0.");
        }
    }

    // ---------- патчи ----------

    // Preserve the original combat-neighbour rules while reusing the removal list.
    // A per-depth buffer avoids corruption if the game re-enters this method.
    [HarmonyPatch(typeof(AIAgent), "UpdateOutOfRangeNearbyAgents")]
    internal static class SkipIdleNearbyPatch
    {
        public static bool Prepare() { return !Main.FpsBoostLoaded; }

        [ThreadStatic] private static List<List<AIAgent>> buffers;
        [ThreadStatic] private static int depth;

        private static List<AIAgent> CurrentBuffer()
        {
            int slot = Math.Max(0, depth - 1);
            if (buffers == null) buffers = new List<List<AIAgent>>();
            while (buffers.Count <= slot) buffers.Add(null);
            if (buffers[slot] == null) buffers[slot] = new List<AIAgent>(8);
            buffers[slot].Clear();
            return buffers[slot];
        }

        public static bool Prefix(AIAgent __instance,
            HashSet<AIAgent> ___fleeWorthyNearbyAgents,
            HashSet<AIAgent> ___fightWorthyNearbyAgents)
        {
            depth++;
            if (Main.settings == null || !Main.settings.skipIdleNearby) return true;
            if (__instance == null || ___fleeWorthyNearbyAgents == null || ___fightWorthyNearbyAgents == null)
                return true;
            if (AIManager.instance == null || AIManager.instance.fightingData == null ||
                GameManager.instance == null) return true;

            FightingData fightingData = AIManager.instance.fightingData;
            float combatDistance = fightingData.combatDistance;
            combatDistance *= combatDistance;
            List<AIAgent> buffer = CurrentBuffer();
            Vector3Int delta;

            foreach (AIAgent nearby in ___fleeWorthyNearbyAgents)
            {
                delta = __instance.posInGrid - nearby.posInGrid;
                if ((float)delta.sqrMagnitude > combatDistance || nearby.GetCombatState() != AIAgent.CombatState.Fighting)
                    buffer.Add(nearby);
            }
            for (int i = 0; i < buffer.Count; i++) ___fleeWorthyNearbyAgents.Remove(buffer[i]);

            buffer.Clear();
            foreach (AIAgent nearby in ___fightWorthyNearbyAgents)
            {
                delta = __instance.posInGrid - nearby.posInGrid;
                if ((float)delta.sqrMagnitude > combatDistance) buffer.Add(nearby);
            }
            for (int i = 0; i < buffer.Count; i++) ___fightWorthyNearbyAgents.Remove(buffer[i]);
            buffer.Clear();

            if (__instance.GetCombatState() == AIAgent.CombatState.Fleeing &&
                (___fleeWorthyNearbyAgents.Count == 0 || WorldTime.TimestampIsDeltaTimeAgo(
                    GameManager.instance.GetWorldTime(), __instance.startFleeingTimestamp,
                    fightingData.stopFleeingHardCapHours * 60 * 60)))
            {
                __instance.SetCombatState(AIAgent.CombatState.None);
            }

            if (__instance.currentGoal is GMoveToFight)
            {
                AIAgent target = __instance.GetFightingTarget();
                if (target != null && target.GetCombatState() == AIAgent.CombatState.Fleeing)
                {
                    delta = __instance.posInGrid - target.posInGrid;
                    int stopChaseDistance = AIManager.instance.fightingData.stopChaseDistance;
                    stopChaseDistance *= stopChaseDistance;
                    if (delta.sqrMagnitude > stopChaseDistance)
                        __instance.RemoveAndTryFindNewFightingTarget();
                }
            }
            return false;
        }

        public static Exception Finalizer(Exception __exception)
        {
            int slot = depth - 1;
            if (slot >= 0 && buffers != null && slot < buffers.Count && buffers[slot] != null)
                buffers[slot].Clear();
            if (depth > 0) depth--;
            return __exception;
        }
    }

    // Reentrant-safe pooling for PathfindingManager.LateUpdate.
    [HarmonyPatch(typeof(PathfindingManager), "LateUpdate")]
    internal static class PathBatchReusePatch
    {
        [ThreadStatic] private static List<List<FindPathRequest>> buffers;
        [ThreadStatic] private static int depth;

        private static List<FindPathRequest> GetBatch()
        {
            if (Main.settings == null || !Main.settings.reusePathBatch)
                return new List<FindPathRequest>();
            int slot = Math.Max(0, depth - 1);
            if (buffers == null) buffers = new List<List<FindPathRequest>>();
            while (buffers.Count <= slot) buffers.Add(null);
            if (buffers[slot] == null) buffers[slot] = new List<FindPathRequest>(16);
            buffers[slot].Clear();
            return buffers[slot];
        }

        public static void Prefix() { depth++; }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            ConstructorInfo ctor = AccessTools.Constructor(typeof(List<FindPathRequest>), Type.EmptyTypes);
            int count = 0;
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Newobj && object.Equals(codes[i].operand, ctor)) count++;
            if (count != 1)
            {
                if (Main.logger != null) Main.logger.Log("Path batch pooling пропущен: ожидалось 1 создание списка, найдено " + count + ".");
                return codes;
            }
            MethodInfo getter = AccessTools.Method(typeof(PathBatchReusePatch), "GetBatch");
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Newobj && object.Equals(codes[i].operand, ctor))
                { codes[i].opcode = OpCodes.Call; codes[i].operand = getter; }
            return codes;
        }

        private static void Exit()
        {
            int slot = depth - 1;
            if (slot >= 0 && buffers != null && slot < buffers.Count && buffers[slot] != null)
                buffers[slot].Clear();
            if (depth > 0) depth--;
        }
        public static Exception Finalizer(Exception __exception) { Exit(); return __exception; }
    }



    // Depth-aware pools: nested combat callbacks get separate scratch buffers.
    [HarmonyPatch(typeof(AIManager), "TriggerCombatPositions")]
    internal static class CombatPositionBuffersPatch
    {
        private sealed class Frame
        {
            public readonly List<AIAgent> affected = new List<AIAgent>(16);
            public readonly List<AIAgent> classifiedA = new List<AIAgent>(16);
            public readonly List<AIAgent> classifiedB = new List<AIAgent>(16);
            public int classifiedIndex;
            public void Clear()
            {
                affected.Clear(); classifiedA.Clear(); classifiedB.Clear();
                classifiedIndex = 0;
            }
        }

        [ThreadStatic] private static List<Frame> frames;
        [ThreadStatic] private static int depth;

        public static void Prefix()
        {
            depth++;
            int slot = depth - 1;
            if (frames == null) frames = new List<Frame>();
            while (frames.Count <= slot) frames.Add(null);
            if (frames[slot] == null) frames[slot] = new Frame();
            frames[slot].Clear();
        }

        private static Frame Current
        {
            get
            {
                int slot = depth - 1;
                if (slot < 0 || frames == null || slot >= frames.Count || frames[slot] == null)
                    return null;
                return frames[slot];
            }
        }

        private static List<AIAgent> GetAffected()
        {
            if (Main.settings == null || !Main.settings.poolCombatScratch) return new List<AIAgent>();
            Frame f = Current;
            return f == null ? new List<AIAgent>() : f.affected;
        }

        private static List<AIAgent> GetClassified(int capacity)
        {
            if (Main.settings == null || !Main.settings.poolCombatScratch) return new List<AIAgent>(capacity);
            Frame f = Current;
            if (f == null) return new List<AIAgent>(capacity);
            List<AIAgent> result = (f.classifiedIndex++ % 2 == 0) ? f.classifiedA : f.classifiedB;
            result.Clear();
            if (result.Capacity < capacity) result.Capacity = capacity;
            return result;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            ConstructorInfo empty = AccessTools.Constructor(typeof(List<AIAgent>), Type.EmptyTypes);
            ConstructorInfo capacity = AccessTools.Constructor(typeof(List<AIAgent>), new Type[] { typeof(int) });
            int emptyCount = 0, capacityCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Newobj) continue;
                if (object.Equals(codes[i].operand, empty)) emptyCount++;
                else if (object.Equals(codes[i].operand, capacity)) capacityCount++;
            }
            if (emptyCount != 1 || capacityCount != 2)
            {
                if (Main.logger != null) Main.logger.Log("Пулы боевых списков пропущены: пустых=" +
                    emptyCount + ", с capacity=" + capacityCount + " (ожидалось 1 и 2).");
                return codes;
            }
            MethodInfo getEmpty = AccessTools.Method(typeof(CombatPositionBuffersPatch), "GetAffected");
            MethodInfo getCapacity = AccessTools.Method(typeof(CombatPositionBuffersPatch), "GetClassified");
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Newobj) continue;
                if (object.Equals(codes[i].operand, empty))
                { codes[i].opcode = OpCodes.Call; codes[i].operand = getEmpty; }
                else if (object.Equals(codes[i].operand, capacity))
                { codes[i].opcode = OpCodes.Call; codes[i].operand = getCapacity; }
            }
            return codes;
        }

        private static void Exit()
        {
            int slot = depth - 1;
            if (slot >= 0 && frames != null && slot < frames.Count && frames[slot] != null)
                frames[slot].Clear();
            if (depth > 0) depth--;
        }
        public static Exception Finalizer(Exception __exception) { Exit(); return __exception; }
    }



    // Reentrant-safe scratch list for target selection.
    [HarmonyPatch(typeof(AIAgent), "FindNewFightingTarget")]
    internal static class FightingCandidateBufferPatch
    {
        [ThreadStatic] private static List<List<AIAgent>> buffers;
        [ThreadStatic] private static int depth;

        public static void Prefix() { depth++; }

        private static List<AIAgent> GetCandidates(int capacity)
        {
            if (Main.settings == null || !Main.settings.poolCombatScratch) return new List<AIAgent>(capacity);
            int slot = Math.Max(0, depth - 1);
            if (buffers == null) buffers = new List<List<AIAgent>>();
            while (buffers.Count <= slot) buffers.Add(null);
            if (buffers[slot] == null) buffers[slot] = new List<AIAgent>(capacity);
            List<AIAgent> result = buffers[slot];
            result.Clear();
            if (result.Capacity < capacity) result.Capacity = capacity;
            return result;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            ConstructorInfo ctor = AccessTools.Constructor(typeof(List<AIAgent>), new Type[] { typeof(int) });
            int count = 0;
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Newobj && object.Equals(codes[i].operand, ctor)) count++;
            if (count != 1)
            {
                if (Main.logger != null) Main.logger.Log("Пул кандидатов боя пропущен: ожидался 1 список, найдено " + count + ".");
                return codes;
            }
            MethodInfo getter = AccessTools.Method(typeof(FightingCandidateBufferPatch), "GetCandidates");
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Newobj && object.Equals(codes[i].operand, ctor))
                { codes[i].opcode = OpCodes.Call; codes[i].operand = getter; }
            return codes;
        }

        private static void Exit()
        {
            int slot = depth - 1;
            if (slot >= 0 && buffers != null && slot < buffers.Count && buffers[slot] != null)
                buffers[slot].Clear();
            if (depth > 0) depth--;
        }
        public static Exception Finalizer(Exception __exception) { Exit(); return __exception; }
    }



}
