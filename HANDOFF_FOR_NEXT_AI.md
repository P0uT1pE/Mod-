# Передача разработки Performance Optimizer 1.3.0

## Задача пользователя
Нужен реальный мод оптимизации Lords and Villeins 1.6.15 GOG на Unity Mod Manager + Harmony, особенно на скорости симуляции x4/x8. Не нужен FPS-анализатор. Пользователь хочет либо совместимость с FPSBoost 1.2.0, либо полную замену его функций. Предпочтена совместная работа, пока нет исходников FPSBoost.

## Полученные файлы в этой сессии
- `PerformanceOptimizer_1.2.0_Source(1).zip` — исходный проект.
- `FPSBoost(1).rar` — распакован через libarchive. Содержит:
  - `FPSBoost/FPSBoost.dll` (28,672 bytes)
  - `FPSBoost/FPSBoost.dll.22701.cache`
  - `FPSBoost/Info.json`
  - `FPSBoost/Settings.xml`
  - Исходников C# нет.
- Settings FPSBoost: optimizeReactionTime=true, parallelComputing=true, animalsLeave=true, animalsOnlyEatFromFeeders=true, noSpreadOfTrees=true, autoNightSpeed=true.
- DLL strings also expose patch class names: `GameManager_AfterLoadingFinished_Patch`, `PathfindingManager_LateUpdate_Patch`, `AnimalManager_GetMaxHerdsOnMapByMapSize_Patch`, `WorldManager_SpawnVegetationFromSpreading_Patch`, `ACAnimalHerbivoreEat_GetTreesForEating_Patch`, `ACAnimalHerbivoreEat_GetCropsForEating_Patch`, `AIAgent_PickGoal_Patch`, plus three PathfindingManager grid rebuild patches. Treat these as names/targets only; do not infer exact algorithm from strings.
- `Assembly-CSharp.dll` в этой загрузке отсутствует.

## Известные игровые точки из предшествующего исследования
1. `AIAgent.UpdateOutOfRangeNearbyAgents`: два временных HashSet создаются на вызов. Наш prefix сохраняет игровой смысл проходов, но заменяет временное накопление удаляемых элементов повторно используемым List.
2. `PathfindingManager.LateUpdate`: создаётся временный `List<FindPathRequest>` на вызов/кадр.
3. `AIManager.TriggerCombatPositions`: создаются три временных List<AIAgent>.
4. `AIAgent.FindNewFightingTarget`: создаётся временный List<AIAgent> кандидатов, который сортируется и используется для выбора цели.

## Что изменено в 1.3.0
- `PathBatchReusePatch`: depth-aware ThreadStatic stack буферов, Prefix увеличивает глубину, Finalizer очищает текущий буфер и уменьшает глубину.
- `CombatPositionBuffersPatch`: per-depth Frame содержит affected/classifiedA/classifiedB; вложенные вызовы получают отдельный Frame.
- `FightingCandidateBufferPatch`: per-depth stack списков.
- Transpilers теперь сначала материализуют IL, считают точные конструкторы и меняют инструкции только если количество точно соответствует ожидаемому. При несовпадении оригинальный IL возвращается без изменений.
- Harmony Finalizer используется как единая точка выхода, не дублировать очистку в Postfix: Harmony Finalizer вызывается и при нормальном завершении, и при исключении.
- FPSBoost discovery проверяет loaded assembly или стандартный путь `Mods/FPSBoost/FPSBoost.dll`; при обнаружении Prepare отключает только `AIAgent.UpdateOutOfRangeNearbyAgents` и `PathfindingManager.LateUpdate`.
- Версия обновлена на 1.3.0.

## Важные замечания для следующей нейронки
1. Не заявлять, что мод «оптимизирует всю игру». На данный момент это оптимизация аллокаций в четырёх конкретных методах + ограничение FPS в фоне.
2. Проверить исходный IL `AIManager.TriggerCombatPositions`: ожидается 1 `new List<AIAgent>()` и 2 `new List<AIAgent>(int)`. Если ожидания неверны, transpiler должен оставить оригинальный метод нетронутым.
3. Проверить `AIAgent.FindNewFightingTarget`: ожидается ровно 1 `new List<AIAgent>(int)`.
4. Проверить `PathfindingManager.LateUpdate`: ожидается ровно 1 `new List<FindPathRequest>()`.
5. Особое внимание к семантике pooled list: убедиться, что списки не сохраняются в полях/замыканиях и не возвращаются наружу. Это предположение основано на прежнем декомпилированном исследовании, требуется повторная проверка.
6. Проверить, что Prefix/Finalizer вызываются корректно при всех путях Harmony. Не добавлять одновременно Postfix и Finalizer с одинаковым decrement: это приведёт к двойному уменьшению depth.
7. Для `UpdateOutOfRangeNearbyAgents` проверить null safety для `AIManager.instance` и `fightingData`, и вложенные вызовы. Сейчас этот Prefix использует один ThreadStatic список; он очищается при следующем вызове, но reentrancy safety для него ещё не реализована.
8. Не снижать частоту обновлений жителей/животных без отдельной opt-in настройки. Не уменьшать path/GOAP лимиты по умолчанию.
9. FPSBoost DLL не содержит исходников. Строки DLL могут указывать на методы/настройки, но не доказывают реализацию алгоритмов. Не патчить/отключать его уникальные функции: parallel computing, animalsLeave, animalsOnlyEatFromFeeders, noSpreadOfTrees, autoNightSpeed, optimizeReactionTime, пока не выполнен IL анализ или не получены исходники.
10. В среде разработки нет dotnet/MSBuild/csc/mcs/Mono и игры. 1.3.0 не компилировался и не запускался.

## Следующий обязательный этап
Попросить пользователя приложить актуальный `Assembly-CSharp.dll` (из `Lords and Villeins_Data/Managed`) и, если возможно, исходники FPSBoost. Затем декомпилировать кандидатов из `AIManager`, `AIAgent`, `PathfindingManager`, `AnimalManager`, `EconomyManager`, `Zone` и производственных классов. Искать частые временные аллокации, повторные LINQ/ToList/ToArray, ненужные проходы и boxed enumerators. Каждое изменение должно сохранять семантику и включаться отдельным флагом до тестирования.

## Файлы проекта
`PerformanceTuner/Main.cs`, `Info.json`, `PerformanceTuner.csproj`, `GameDir.props`, README, Build scripts. Assembly name и UMM Id остаются `PerformanceTuner`.
