using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SkillsExtended.LockPicking;
using SkillsExtended.Skills.LockPicking;

namespace LockpickWearByLevel;

[BepInPlugin("MusicManiac.LockpickWearByLevel", "LockpickWearByLevel", "1.0.0")]
[BepInDependency("com.cj.skillsextended")]
[BepInDependency("MusicManiac.SkillsExtendedStackableLockpicks", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string StackablePickTemplate = "6903c1d204b5a6101e8d0001";
    private Harmony? _harmony;
    internal static ConfigEntry<int> MinimumLockLevel { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;
        MinimumLockLevel = Config.Bind(
            "Lockpick Wear",
            "MinimumLockLevel",
            3,
            new ConfigDescription(
                "Successful lockpicks consume one use at this lock level and higher. Levels below it keep Skills Extended's normal behavior.",
                new AcceptableValueRange<int>(1, 5)));
        _harmony = new Harmony("MusicManiac.LockpickWearByLevel");
        _harmony.PatchAll(typeof(Plugin).Assembly);
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();
}

[HarmonyPatch]
internal static class ChargeSuccessfulPickPatch
{
    private static readonly FieldInfo UsesField = AccessTools.Field(typeof(PickingAuthority), "_uses");
    private static readonly MethodInfo PlayerMethod = AccessTools.Method(typeof(PickingRuntime), "Player");
    private static readonly MethodInfo ToolMethod = AccessTools.Method(typeof(PickingRuntime), "Tool");

    private static MethodBase TargetMethod() => AccessTools.Method(typeof(PickingAuthority), "Finish");

    private static void Postfix(PickingAuthority __instance, PickSession __0, PickReply __1)
    {
        if (__0.Engine.Outcome != PickOutcome.Unlocked || __0.Difficulty < Plugin.MinimumLockLevel.Value)
            return;

        var uses = __0.Uses + 1;
        __0.Uses = uses;
        var runtime = PickingRuntime.Instance;
        var player = runtime == null ? null : (Player?)PlayerMethod.Invoke(runtime, new object[] { __0.Actor });
        var pick = player == null ? null : (Key?)ToolMethod.Invoke(null, new object[] { player, __0.Tool });
        ((Dictionary<string, int>)UsesField.GetValue(__instance))[__0.Tool] =
            pick?.TemplateId.ToString() == Plugin.StackablePickTemplate ? 0 : uses;
        __1.ToolUses = uses;
    }
}

[HarmonyPatch(typeof(PickingRuntime), nameof(PickingRuntime.Receive))]
internal static class ApplySuccessfulPickPatch
{
    private static readonly MethodInfo ToolMethod = AccessTools.Method(typeof(PickingRuntime), "Tool");
    private static readonly HashSet<string> ConsumedAttempts = new(StringComparer.Ordinal);

    private static void Postfix(PickingRuntime __instance, PickReply reply)
    {
        if (reply?.State?.Outcome != PickOutcome.Unlocked || !string.IsNullOrEmpty(reply.Error))
            return;

        Player? player = __instance.World?.MainPlayer;
        if (player == null || player.ProfileId != reply.Actor || string.IsNullOrEmpty(reply.Tool))
            return;

        var pick = (Key)ToolMethod.Invoke(null, new object[] { player, reply.Tool });
        if (pick?.KeyComponent == null)
            return;

        var component = pick.KeyComponent;
        if (reply.ToolUses <= component.NumberOfUsages)
            return;

        if (pick.TemplateId.ToString() == Plugin.StackablePickTemplate)
        {
            if (ConsumedAttempts.Contains(reply.Attempt))
                return;

            if (pick.StackObjectsCount > 1)
            {
                var stackOwner = pick.Parent.GetOwner() as ItemController;
                if (stackOwner == null)
                    return;

                var split = ItemManipulator.SplitToNowhere(pick, 1, stackOwner, stackOwner, false);
                if (split.Failed)
                {
                    Plugin.Log.LogError("Could not consume a stackable lockpick: " + split.Error);
                    return;
                }
                component.NumberOfUsages = 0;
                ConsumedAttempts.Add(reply.Attempt);
                return;
            }
        }

        component.NumberOfUsages = reply.ToolUses;

        var maximum = component.Template.MaximumNumberOfUsage;
        if (maximum > 0 && component.NumberOfUsages >= maximum)
        {
            var owner = pick.Parent.GetOwner() as ItemController;
            if (owner != null)
                ItemManipulator.Discard(pick, owner, false);
        }
    }
}
